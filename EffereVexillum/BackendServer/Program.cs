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
    options.UseNpgsql(dataSource));

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
    db.Database.EnsureCreated();
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

app.Run("http://0.0.0.0:8080");
