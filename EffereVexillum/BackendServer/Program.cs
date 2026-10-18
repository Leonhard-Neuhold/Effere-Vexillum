using BackendServer.Api;
using BackendServer.Database;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Minio;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Add DbContext
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Host=postgres;Port=5432;Database=effere;Username=postgres;Password=postgres";
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.EnableDynamicJson();
var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(dataSource, o => o.EnableRetryOnFailure(
        maxRetryCount: 10,
        maxRetryDelay: TimeSpan.FromSeconds(30),
        errorCodesToAdd: null)));

// Add Identity
builder.Services.AddAuthorization();
builder.Services.AddIdentityApiEndpoints<IdentityUser>()
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", corsBuilder =>
    {
        corsBuilder.SetIsOriginAllowed(_ => true)
               .AllowCredentials()
               .AllowAnyMethod()
               .AllowAnyHeader();
    });
});

builder.Services.AddMinio(configureClient => configureClient
    .WithEndpoint(builder.Configuration["Minio:Endpoint"] ?? "localhost:9000")
    .WithCredentials(builder.Configuration["Minio:AccessKey"] ?? "minioadmin", builder.Configuration["Minio:SecretKey"] ?? "minioadmin")
    .WithSSL(builder.Configuration.GetValue<bool>("Minio:Secure"))
    .Build());

builder.Services.AddSignalR();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    
    // Add retry loop in case the database container is still starting up
    int retries = 10;
    while (retries > 0)
    {
        try
        {
            db.Database.EnsureCreated();
            break;
        }
        catch (Exception ex)
        {
            retries--;
            Console.WriteLine($"Database not ready, retrying in 5 seconds... ({ex.Message})");
            if (retries == 0) throw;
            Thread.Sleep(5000);
        }
    }

    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Flags\" ADD COLUMN \"Aliases\" jsonb NOT NULL DEFAULT '[]'::jsonb;");
    }
    catch
    {
        // ignored
    }

    try
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""Chains"" (
                ""Id"" uuid NOT NULL,
                ""GameId"" uuid NOT NULL,
                ""ChainIndex"" integer NOT NULL,
                ""OwnerPlayerId"" text NOT NULL,
                ""OriginalFlag"" text NOT NULL,
                ""Steps"" jsonb NOT NULL,
                CONSTRAINT ""PK_Chains"" PRIMARY KEY (""Id""),
                CONSTRAINT ""FK_Chains_Games_GameId"" FOREIGN KEY (""GameId"") REFERENCES ""Games"" (""Id"") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ""IX_Chains_GameId"" ON ""Chains"" (""GameId"");");
    }
    catch
    {
        // ignored
    }

    try
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""UserProfiles"" (
                ""UserId"" text NOT NULL,
                ""AvatarDataUrl"" text NOT NULL,
                CONSTRAINT ""PK_UserProfiles"" PRIMARY KEY (""UserId"")
            );
        ");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not create UserProfiles table: {ex.Message}");
    }

    var minioClient = scope.ServiceProvider.GetRequiredService<IMinioClient>();
    
    try 
    {
        // 1. Fetch REST Countries API data
        var countryDataList = new List<RestCountryDto>();
        try 
        {
            using var httpClient = new HttpClient();
            var response = await httpClient.GetAsync("https://restcountries.com/v3.1/all?fields=name,translations,region");
            if (response.IsSuccessStatusCode)
            {
                countryDataList = await response.Content.ReadFromJsonAsync<List<RestCountryDto>>() ?? new List<RestCountryDto>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching RestCountries data: {ex.Message}");
        }

        var bucketName = "pictures";
        var found = await minioClient.BucketExistsAsync(new Minio.DataModel.Args.BucketExistsArgs().WithBucket(bucketName));
        if (!found)
        {
            await minioClient.MakeBucketAsync(new Minio.DataModel.Args.MakeBucketArgs().WithBucket(bucketName));
            found = true;
        }

        // Upload flag images from the mounted Flags directory that are not in the bucket yet
        var flagsDir = builder.Configuration["Flags:Directory"] ?? "/flags";
        if (Directory.Exists(flagsDir))
        {
            var existingKeys = new HashSet<string>();
            var existingArgs = new Minio.DataModel.Args.ListObjectsArgs().WithBucket(bucketName).WithRecursive(true);
            await foreach (var item in minioClient.ListObjectsEnumAsync(existingArgs).ConfigureAwait(false))
                existingKeys.Add(item.Key);

            var contentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
                [".gif"] = "image/gif", [".svg"] = "image/svg+xml"
            };
            var uploaded = 0;
            foreach (var file in Directory.EnumerateFiles(flagsDir))
            {
                var key = Path.GetFileName(file);
                if (existingKeys.Contains(key) || !contentTypes.TryGetValue(Path.GetExtension(key), out var contentType)) continue;
                await minioClient.PutObjectAsync(new Minio.DataModel.Args.PutObjectArgs()
                    .WithBucket(bucketName).WithObject(key).WithFileName(file).WithContentType(contentType));
                uploaded++;
            }
            Console.WriteLine($"Flag upload: {uploaded} new image(s) uploaded from {flagsDir}.");
        }

        if (found)
        {
            var listArgs = new Minio.DataModel.Args.ListObjectsArgs().WithBucket(bucketName).WithRecursive(true);
            using var cts = new CancellationTokenSource();
            await foreach (var item in minioClient.ListObjectsEnumAsync(listArgs, cts.Token).ConfigureAwait(false))
            {
                if (!item.IsDir)
                {
                    var expectedName = Path.GetFileNameWithoutExtension(item.Key).Replace("_", " ").Trim();
                    
                    var existingFlag = await db.Flags.FirstOrDefaultAsync(f => f.Name == expectedName);
                    if (existingFlag == null)
                    {
                        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var category = "Other";

                        // Try to find the country in our fetched data
                        var matchedCountry = countryDataList.FirstOrDefault(c => 
                            c.Name?.Common?.Equals(expectedName, StringComparison.OrdinalIgnoreCase) == true ||
                            c.Name?.Official?.Equals(expectedName, StringComparison.OrdinalIgnoreCase) == true);
                            
                        if (matchedCountry != null)
                        {
                            category = matchedCountry.Region ?? "country";
                            
                            if (matchedCountry.Name?.Official != null && matchedCountry.Name.Official != expectedName)
                                aliases.Add(matchedCountry.Name.Official);

                            if (matchedCountry.Translations != null)
                            {
                                foreach (var t in matchedCountry.Translations.Values)
                                {
                                    if (!string.IsNullOrWhiteSpace(t.Common)) aliases.Add(t.Common);
                                    if (!string.IsNullOrWhiteSpace(t.Official)) aliases.Add(t.Official);
                                }
                            }
                        }

                        var flag = new Flag
                        {
                            Name = expectedName,
                            Category = category,
                            Aliases = aliases.ToList()
                        };
                        db.Flags.Add(flag);
                    }
                    else if (existingFlag.Aliases == null || existingFlag.Aliases.Count == 0)
                    {
                        // In case we want to update existing flags that don't have aliases
                        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        
                        var matchedCountry = countryDataList.FirstOrDefault(c => 
                            c.Name?.Common?.Equals(expectedName, StringComparison.OrdinalIgnoreCase) == true ||
                            c.Name?.Official?.Equals(expectedName, StringComparison.OrdinalIgnoreCase) == true);
                            
                        if (matchedCountry != null)
                        {
                            existingFlag.Category = matchedCountry.Region ?? existingFlag.Category;
                            
                            if (matchedCountry.Name?.Official != null && matchedCountry.Name.Official != expectedName)
                                aliases.Add(matchedCountry.Name.Official);

                            if (matchedCountry.Translations != null)
                            {
                                foreach (var t in matchedCountry.Translations.Values)
                                {
                                    if (!string.IsNullOrWhiteSpace(t.Common)) aliases.Add(t.Common);
                                    if (!string.IsNullOrWhiteSpace(t.Official)) aliases.Add(t.Official);
                                }
                            }
                        }

                        if (aliases.Count > 0)
                        {
                            existingFlag.Aliases = aliases.ToList();
                            db.Flags.Update(existingFlag);
                        }
                    }
                }
            }
            await db.SaveChangesAsync();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error seeding flags from minio: {ex.Message}");
    }
}

app.UseCors("AllowAll");

app.MapGroup("/api/auth").MapIdentityApi<IdentityUser>();

app.MapHub<GameHub>("/lobby");

app.MapPost("/api/auth/logout", async (SignInManager<IdentityUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Ok();
}).RequireAuthorization();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "Hello World!");
app.AddEndpoints();
app.AddLobbyEndpoints();
app.AddChainEndpoints();

app.Run("http://0.0.0.0:8080");

public class RestCountryDto
{
    public NameDto? Name { get; set; }
    public string? Region { get; set; }
    public Dictionary<string, TranslationDto>? Translations { get; set; }
}

public class NameDto
{
    public string? Common { get; set; }
    public string? Official { get; set; }
}

public class TranslationDto
{
    public string? Official { get; set; }
    public string? Common { get; set; }
}
