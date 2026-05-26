using Minio.ApiEndpoints;

namespace BackendServer.Api;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Minio;
using Minio.DataModel.Args;
using BackendServer.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using System.Text.Encodings.Web;

public static class Endpoints
{
    public static void AddEndpoints(this WebApplication app)
    {
        var userGroup = app.MapGroup("/api/user").RequireAuthorization();

        userGroup.MapGet("/profile", async (ClaimsPrincipal claimsPrincipal, UserManager<IdentityUser> userManager, AppDbContext db) =>
        {
            var user = await userManager.GetUserAsync(claimsPrincipal);
            if (user == null) return Results.Unauthorized();

            // Fetch completed games containing the user
            // We load into memory first if EF Core can't translate dictionary properly here
            var allFinishedGames = await db.Games
                .Include(g => g.Stats)
                .Include(g => g.Lobby)
                .Where(g => g.GameFinished && g.Stats != null)
                .OrderByDescending(g => g.Lobby!.CreationDate)
                .Take(100) // limit logic for safety if lots of games
                .ToListAsync();

            var userGames = new List<object>();

            foreach (var g in allFinishedGames)
            {
                if (g.Stats?.CumulatedPoints != null && g.Stats.CumulatedPoints.ContainsKey(user.Id))
                {
                    var userScore = g.Stats.CumulatedPoints[user.Id];

                    // Calculate placement
                    var scores = g.Stats.CumulatedPoints.Values.OrderByDescending(v => v).Distinct().ToList();
                    var placement = scores.IndexOf(userScore) + 1; // 1-based placement
                    
                    string placementString = placement switch
                    {
                        1 => "1st",
                        2 => "2nd",
                        3 => "3rd",
                        _ => $"{placement}th"
                    };

                    userGames.Add(new
                    {
                        Date = g.Lobby?.CreationDate ?? DateTime.UtcNow,
                        LobbyName = g.Lobby?.Name ?? "Unknown",
                        Score = userScore,
                        Placement = placementString
                    });
                }
            }

            var topGames = userGames.OrderByDescending(x => ((dynamic)x).Score).Take(3).ToList();

            return Results.Ok(new
            {
                user.UserName,
                user.Email,
                user.TwoFactorEnabled,
                TopGames = topGames
            });
        });

        userGroup.MapPut("/username", async (ClaimsPrincipal claimsPrincipal, UserManager<IdentityUser> userManager, UpdateUsernameRequest req) =>
        {
            var user = await userManager.GetUserAsync(claimsPrincipal);
            if (user == null) return Results.Unauthorized();
            
            var result = await userManager.SetUserNameAsync(user, req.NewUsername);
            if (result.Succeeded) return Results.Ok();
            
            return Results.BadRequest(result.Errors);
        });

        userGroup.MapGet("/2fa-setup", async (ClaimsPrincipal claimsPrincipal, UserManager<IdentityUser> userManager, UrlEncoder urlEncoder) =>
        {
            var user = await userManager.GetUserAsync(claimsPrincipal);
            if (user == null) return Results.Unauthorized();

            // Ensure the user has an authenticator key
            var unformattedKey = await userManager.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrEmpty(unformattedKey))
            {
                await userManager.ResetAuthenticatorKeyAsync(user);
                unformattedKey = await userManager.GetAuthenticatorKeyAsync(user);
            }

            var email = await userManager.GetEmailAsync(user);
            var format = "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6";
            var authenticatorUri = string.Format(
                format,
                urlEncoder.Encode("EffereVexillum"),
                urlEncoder.Encode(email ?? user.UserName ?? "User"),
                unformattedKey);

            return Results.Ok(new { SharedKey = unformattedKey, AuthenticatorUri = authenticatorUri });
        });

        userGroup.MapPost("/2fa-enable", async (ClaimsPrincipal claimsPrincipal, UserManager<IdentityUser> userManager, Enable2FARequest req) =>
        {
            var user = await userManager.GetUserAsync(claimsPrincipal);
            if (user == null) return Results.Unauthorized();

            var isValid = await userManager.VerifyTwoFactorTokenAsync(user, userManager.Options.Tokens.AuthenticatorTokenProvider, req.Code);
            if (!isValid)
            {
                return Results.BadRequest(new { Message = "Verification code is invalid." });
            }

            await userManager.SetTwoFactorEnabledAsync(user, true);
            var recoveryCodes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);

            return Results.Ok(new { RecoveryCodes = recoveryCodes });
        });

        userGroup.MapPost("/2fa-disable", async (ClaimsPrincipal claimsPrincipal, UserManager<IdentityUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(claimsPrincipal);
            if (user == null) return Results.Unauthorized();

            await userManager.SetTwoFactorEnabledAsync(user, false);
            return Results.Ok();
        });

        app.MapGet("/api/flags", async (AppDbContext db) => 
        {
            var flags = await db.Flags.Select(f => new { f.Name, f.Category, f.Aliases }).ToListAsync();
            return Results.Ok(flags);
        });

        app.MapGet("/health", () => "Service healthy!");

        var picturesGroup = app.MapGroup("/api/pictures");

        picturesGroup.MapGet("/", async ([FromServices] IMinioClient minioClient, [FromQuery] string? filter, [FromQuery] string? label) =>
        {
            try
            {
                var bucketName = "pictures";
                var found = await minioClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucketName));
                if (!found) return Results.Ok(new string[] { });

                var listArgs = new ListObjectsArgs().WithBucket(bucketName).WithRecursive(true);
                
                var pictures = new List<string>();
                using var cts = new CancellationTokenSource();

                await foreach (var item in minioClient.ListObjectsEnumAsync(listArgs, cts.Token).ConfigureAwait(false))
                {
                    bool matchesFilter = string.IsNullOrEmpty(filter) || item.Key.Contains(filter, StringComparison.OrdinalIgnoreCase);
                    bool matchesLabel = string.IsNullOrEmpty(label) || item.Key.Contains(label, StringComparison.OrdinalIgnoreCase);
                    
                    if (matchesFilter && matchesLabel && !item.IsDir)
                    {
                        pictures.Add(item.Key);
                    }
                }

                return Results.Ok(pictures);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        picturesGroup.MapGet("/{**objectName}", async ([FromServices] IMinioClient minioClient, string objectName) =>
        {
            try
            {
                var bucketName = "pictures";
                var memoryStream = new MemoryStream();
                var getObjectArgs = new GetObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(objectName)
                    .WithCallbackStream((stream) => stream.CopyTo(memoryStream));
                
                await minioClient.GetObjectAsync(getObjectArgs);
                memoryStream.Position = 0;
                
                return Results.File(memoryStream, "application/octet-stream", objectName);
            }
            catch (Exception ex)
            {
                return Results.NotFound(ex.Message);
            }
        });

        var drawingsGroup = app.MapGroup("/api/drawings");
        
        drawingsGroup.MapGet("/{**objectName}", async ([FromServices] IMinioClient minioClient, string objectName) =>
        {
            try
            {
                var bucketName = "drawings";
                var memoryStream = new MemoryStream();
                var getObjectArgs = new GetObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(objectName)
                    .WithCallbackStream((stream) => stream.CopyTo(memoryStream));
                
                await minioClient.GetObjectAsync(getObjectArgs);
                memoryStream.Position = 0;
                
                return Results.File(memoryStream, "application/octet-stream", objectName);
            }
            catch (Exception ex)
            {
                return Results.NotFound(ex.Message);
            }
        });
    }
}

public record UpdateUsernameRequest(string NewUsername);
public record Enable2FARequest(string Code);
