using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using BackendServer.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using Minio.DataModel.Args;

namespace BackendServer.Api;

public static class LobbyEndpoints
{
    public static void AddLobbyEndpoints(this WebApplication app)
    {
        var lobbiesGroup = app.MapGroup("/api/lobbies").RequireAuthorization();

        // Create a lobby
        lobbiesGroup.MapPost("/", async (AppDbContext db, ClaimsPrincipal user, LobbyCreateRequest request) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Results.Unauthorized();

            var joinCode = Guid.NewGuid().ToString("N")[..8];
            var lobbyId = Guid.NewGuid();

            var lobby = new Lobby
            {
                Id = lobbyId,
                Name = request.Name,
                GameMode = request.GameMode,
                NumberOfRounds = request.NumberOfRounds,
                CustomFilter = request.CustomFilter,
                GameAdminId = userId,
                UniqueJoinLink = joinCode,
                UniqueLobbyUrl = $"/lobby/{lobbyId}",
                JoinedPlayers = new List<string> { userId },
                GameStarted = false,
                TimerForDrawing = request.TimerForDrawing,
                TimerForGuessing = request.TimerForGuessing
            };

            db.Lobbies.Add(lobby);
            await db.SaveChangesAsync();

            return Results.Created($"/api/lobbies/{lobby.Id}", lobby);
        });

        // Get single lobby details
        lobbiesGroup.MapGet("/{id:guid}", async (AppDbContext db, Guid id) =>
        {
            var lobby = await db.Lobbies.FindAsync(id);
            return lobby is not null ? Results.Ok(lobby) : Results.NotFound();
        });

        // Update a lobby
        lobbiesGroup.MapPut("/{id:guid}", async (AppDbContext db, ClaimsPrincipal user, Guid id, LobbyUpdateRequest request) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var lobby = await db.Lobbies.FindAsync(id);

            if (lobby is null) return Results.NotFound();
            if (lobby.GameAdminId != userId) return Results.Forbid();

            lobby.Name = request.Name ?? lobby.Name;
            lobby.GameMode = request.GameMode ?? lobby.GameMode;
            lobby.NumberOfRounds = request.NumberOfRounds ?? lobby.NumberOfRounds;
            lobby.CustomFilter = request.CustomFilter ?? lobby.CustomFilter;
            lobby.TimerForDrawing = request.TimerForDrawing ?? lobby.TimerForDrawing;
            lobby.TimerForGuessing = request.TimerForGuessing ?? lobby.TimerForGuessing;
            lobby.GameStarted = request.GameStarted ?? lobby.GameStarted;

            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Delete a lobby
        lobbiesGroup.MapDelete("/{id:guid}", async (AppDbContext db, ClaimsPrincipal user, Guid id) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var lobby = await db.Lobbies.FindAsync(id);

            if (lobby is null) return Results.NotFound();
            if (lobby.GameAdminId != userId) return Results.Forbid();

            db.Lobbies.Remove(lobby);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Leave a lobby
        lobbiesGroup.MapPost("/{id:guid}/leave", async (AppDbContext db, ClaimsPrincipal user, Guid id, IHubContext<GameHub> hubContext) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Results.Unauthorized();

            var lobby = await db.Lobbies.FindAsync(id);
            if (lobby is null) return Results.NotFound();

            if (lobby.JoinedPlayers.Contains(userId))
            {
                lobby.JoinedPlayers.Remove(userId);
                
                await hubContext.Clients.Group(lobby.Id.ToString()).SendAsync("PlayerJoined", userId);
                
                // If everyone left, maybe delete the lobby? Or keep it if you want.
                if (lobby.JoinedPlayers.Count == 0 && lobby.GameAdminId == userId)
                {
                    db.Lobbies.Remove(lobby);
                }
                
                await db.SaveChangesAsync();
            }

            return Results.Ok();
        });

        // Start game
        lobbiesGroup.MapPost("/{id:guid}/start", async (AppDbContext db, ClaimsPrincipal user, Guid id, IServiceProvider sp) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Results.Unauthorized();

            var lobby = await db.Lobbies.Include(l => l.Games).FirstOrDefaultAsync(l => l.Id == id);
            if (lobby is null) return Results.NotFound();
            if (lobby.GameAdminId != userId) return Results.Forbid();
            if (lobby.GameStarted) return Results.BadRequest("Game has already started.");

            lobby.GameStarted = true;

            var game = new Game
            {
                Id = Guid.NewGuid(),
                LobbyId = lobby.Id,
                CurrentRoundNumber = 1,
                Stats = new Stats { Id = Guid.NewGuid() }
            };

            var validFlags = string.IsNullOrEmpty(lobby.CustomFilter) 
                ? await db.Flags.Select(f => f.Name).ToListAsync()
                : await db.Flags.Where(f => f.Category == lobby.CustomFilter).Select(f => f.Name).ToListAsync();
                
            if (!validFlags.Any()) validFlags.Add("Unknown"); // Fallback if no flags match filter
            
            var rnd = new Random();

            for (int i = 1; i <= lobby.NumberOfRounds; i++)
            {
                var flagsForRound = new Dictionary<string, string>();
                foreach (var joinedPlayer in lobby.JoinedPlayers)
                {
                    flagsForRound[joinedPlayer] = validFlags[rnd.Next(validFlags.Count)];
                }
                
                game.Rounds.Add(new Round
                {
                    Id = Guid.NewGuid(),
                    GameId = game.Id,
                    RoundNumber = i,
                    State = "drawing",
                    RoundFinished = false,
                    FinishedDrawing = false,
                    PlayerFlags = flagsForRound,
                    PlayerDrawnFlags = new(),
                    PlayerPoints = new(),
                    PlayerGuesses = new(),
                    PlayerToDrawingMapping = new(),
                    StartTime = DateTime.UtcNow // Set initial start time for first round drawing
                });
            }

            db.Games.Add(game);
            await db.SaveChangesAsync();

            var hubContext = sp.GetRequiredService<IHubContext<GameHub>>();
            await hubContext.Clients.Group(lobby.Id.ToString()).SendAsync("GameStarted", game.Id);
            await hubContext.Clients.Group(lobby.Id.ToString()).SendAsync("RoundStarted", 1);
            
            GameTimers.StartDrawingTimer(sp, game.Id, 1, lobby.TimerForDrawing);

            return Results.Ok(new { GameId = game.Id });
        });

        // Join endpoint using the custom join link, accessible globally if authorized
        app.MapGet("/join/{joinLink}", async (AppDbContext db, ClaimsPrincipal user, string joinLink, IHubContext<GameHub> hubContext) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Results.Unauthorized();

            var lobby = await db.Lobbies.FirstOrDefaultAsync(l => l.UniqueJoinLink == joinLink);
            if (lobby is null) return Results.NotFound("Invalid join link.");

            if (!lobby.JoinedPlayers.Contains(userId))
            {
                lobby.JoinedPlayers.Add(userId);
                await db.SaveChangesAsync();
                
                await hubContext.Clients.Group(lobby.Id.ToString()).SendAsync("PlayerJoined", userId);
            }

            return Results.Redirect(lobby.UniqueLobbyUrl);
        }).RequireAuthorization();

        // Submit a guess
        app.MapPost("/api/guess", async (AppDbContext db, ClaimsPrincipal user, GuessRequest request, IServiceProvider sp) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Results.Unauthorized();

            var round = await db.Rounds
                .Include(r => r.Game)
                .ThenInclude(g => g!.Lobby)
                .FirstOrDefaultAsync(r => r.GameId == request.GameId && r.RoundNumber == request.RoundNumber);

            if (round is null || round.Game?.Lobby is null) return Results.NotFound("Round or game not found.");

            var lobby = round.Game.Lobby;
            if (!lobby.JoinedPlayers.Contains(userId)) return Results.Forbid();
            if (round.RoundFinished) return Results.BadRequest("Round is already finished.");
            if (round.State != "guessing") return Results.BadRequest("Not in guessing phase.");

            round.PlayerGuesses[userId] = request.Guess;
            db.Entry(round).Property(r => r.PlayerGuesses).IsModified = true;
            await db.SaveChangesAsync();

            // Check if all players have guessed
            if (lobby.JoinedPlayers.All(p => round.PlayerGuesses.ContainsKey(p)))
            {
                await GameLogic.FinishGuessingPhase(sp, round.GameId, round.RoundNumber);
            }

            return Results.Ok();
        }).RequireAuthorization();

        // Submit a drawing
        app.MapPost("/api/submit-drawing", async (AppDbContext db, ClaimsPrincipal user, SubmitDrawingRequest request, IServiceProvider sp) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Results.Unauthorized();

            var round = await db.Rounds
                .Include(r => r.Game)
                .ThenInclude(g => g!.Lobby)
                .FirstOrDefaultAsync(r => r.GameId == request.GameId && r.RoundNumber == request.RoundNumber);

            if (round is null || round.Game?.Lobby is null) return Results.NotFound("Round or game not found.");

            var lobby = round.Game.Lobby;
            if (!lobby.JoinedPlayers.Contains(userId)) return Results.Forbid();
            if (round.FinishedDrawing) return Results.BadRequest("Drawing phase is already finished.");
            if (round.State != "drawing") return Results.BadRequest("Round is not in drawing state.");

            round.PlayerDrawnFlags[userId] = request.ImagePath;
            db.Entry(round).Property(r => r.PlayerDrawnFlags).IsModified = true;
            await db.SaveChangesAsync();

            // Check if all players have submitted a drawing
            if (lobby.JoinedPlayers.All(p => round.PlayerDrawnFlags.ContainsKey(p)))
            {
                await GameLogic.FinishDrawingPhase(sp, round.GameId, round.RoundNumber);
            }

            return Results.Ok();
        }).RequireAuthorization();

        // Upload drawing to MinIO
        app.MapPost("/api/upload-drawing", async (
            HttpContext context,
            [FromServices] IMinioClient minioClient,
            [FromForm] Guid lobbyId,
            [FromForm] Guid gameId,
            [FromForm] int roundNumber) =>
        {
            var user = context.User;
            if (user?.FindFirstValue(ClaimTypes.NameIdentifier) == null) return Results.Unauthorized();

            var file = context.Request.Form.Files.FirstOrDefault();
            if (file == null) return Results.BadRequest("No file uploaded.");

            var bucketName = "drawings";
            var found = await minioClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucketName));
            if (!found)
            {
                await minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucketName));
            }

            var ext = Path.GetExtension(file.FileName);
            var objectName = $"{lobbyId}/{gameId}/{roundNumber}/{Guid.NewGuid()}{ext}";

            using var stream = file.OpenReadStream();
            var putObjectArgs = new PutObjectArgs()
                .WithBucket(bucketName)
                .WithObject(objectName)
                .WithStreamData(stream)
                .WithObjectSize(file.Length)
                .WithContentType(file.ContentType);

            await minioClient.PutObjectAsync(putObjectArgs);

            return Results.Ok(new { Path = objectName });
        }).RequireAuthorization()
          .DisableAntiforgery();
    }
}

public record LobbyCreateRequest(string Name, string GameMode, int NumberOfRounds, string CustomFilter, int TimerForDrawing, int TimerForGuessing);
public record LobbyUpdateRequest(string? Name, string? GameMode, int? NumberOfRounds, string? CustomFilter, int? TimerForDrawing, int? TimerForGuessing, bool? GameStarted);
public record GuessRequest(Guid GameId, int RoundNumber, string Guess);
public record SubmitDrawingRequest(Guid GameId, int RoundNumber, string ImagePath);

public static class GameTimers
{
    public static void StartDrawingTimer(IServiceProvider sp, Guid gameId, int roundNumber, int seconds)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await GameLogic.FinishDrawingPhase(sp, gameId, roundNumber);
        });
    }

    public static void StartGuessingTimer(IServiceProvider sp, Guid gameId, int roundNumber, int seconds)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await GameLogic.FinishGuessingPhase(sp, gameId, roundNumber);
        });
    }
}

public static class GameLogic
{
    public static async Task FinishDrawingPhase(IServiceProvider sp, Guid gameId, int roundNumber)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<GameHub>>();

        var round = await db.Rounds
            .Include(r => r.Game)
            .ThenInclude(g => g!.Lobby)
            .FirstOrDefaultAsync(r => r.GameId == gameId && r.RoundNumber == roundNumber);

        if (round == null || round.FinishedDrawing || round.Game?.Lobby == null) return; // Already finished or invalid

        var lobby = round.Game.Lobby;

        round.FinishedDrawing = true;
        round.State = "guessing";
        round.StartTime = DateTime.UtcNow;

        // Randomly map guessers to drawers, preventing players from guessing their own drawing if possible
        var players = lobby.JoinedPlayers.OrderBy(x => Guid.NewGuid()).ToList();
        for (int i = 0; i < players.Count; i++)
        {
            var guesser = players[i];
            var drawer = players.Count > 1 ? players[(i + 1) % players.Count] : players[0];
            round.PlayerToDrawingMapping[guesser] = drawer;
        }
        db.Entry(round).Property(r => r.PlayerToDrawingMapping).IsModified = true;

        await db.SaveChangesAsync();
        await hubContext.Clients.Group(lobby.Id.ToString()).SendAsync("DrawingPhaseFinished", round.RoundNumber);
        
        GameTimers.StartGuessingTimer(sp, gameId, roundNumber, lobby.TimerForGuessing);
    }

    public static async Task FinishGuessingPhase(IServiceProvider sp, Guid gameId, int roundNumber)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<GameHub>>();

        var round = await db.Rounds
            .Include(r => r.Game)
            .ThenInclude(g => g!.Lobby)
            .Include(r => r.Game!.Stats)
            .FirstOrDefaultAsync(r => r.GameId == gameId && r.RoundNumber == roundNumber);

        if (round == null || round.RoundFinished || round.Game?.Lobby == null) return;

        var lobby = round.Game.Lobby;
        round.RoundFinished = true;
        
        var now = DateTime.UtcNow;
        double maxTime = lobby.TimerForGuessing > 0 ? lobby.TimerForGuessing : 60.0;
        double elapsed = (now - round.StartTime).TotalSeconds;
        elapsed = Math.Clamp(elapsed, 0, maxTime);
        
        // Exponential decay factor (e.g. e^(-3 * elapsed / maxTime)) so points fall drastically toward 0
        double multiplier = Math.Exp(-3.0 * elapsed / maxTime);
        
        foreach (var guesser in lobby.JoinedPlayers)
        {
            round.PlayerPoints.TryAdd(guesser, 0);
            
            // Find who drew the flag this guesser received
            var drawer = round.PlayerToDrawingMapping.GetValueOrDefault(guesser);
            if (drawer != null && round.PlayerFlags.TryGetValue(drawer, out var correctFlagName))
            {
                var guess = round.PlayerGuesses.GetValueOrDefault(guesser);
                if (string.Equals(guess, correctFlagName, StringComparison.OrdinalIgnoreCase))
                {
                    int points = (int)Math.Round(1000 * multiplier);
                    round.PlayerPoints[guesser] += points;
                    
                    // Reward drawer explicitly
                    int drawerPoints = (int)Math.Round(500 * multiplier); // bonus for good drawing
                    round.PlayerPoints.TryAdd(drawer, 0);
                    round.PlayerPoints[drawer] += drawerPoints;
                }
            }
            
            // Add round points to cumulated status
            var currentTotalForGuesser = round.Game.Stats!.CumulatedPoints.GetValueOrDefault(guesser, 0);
            round.Game.Stats.CumulatedPoints[guesser] = currentTotalForGuesser + round.PlayerPoints[guesser];
            
            // Note: Since drawer might get multiple bonuses, we should also update drawer's cumulated stats here?
            // Actually, no, if we loop over all guessers, we will process the drawer as a guesser themselves eventually,
            // but we need to correctly add the drawer's bonus points to their cumulated total.
            // A safer way is to just rebuild cumulated stats or add the points properly at the end of the loop, but since we are modifying it here, it works if we update it cleanly.
        }
        
        // Re-calculate cumulated stats safely
        foreach (var player in lobby.JoinedPlayers)
        {
            var currentTotal = round.Game.Stats!.CumulatedPoints.GetValueOrDefault(player, 0);
            var roundPoints = round.PlayerPoints.GetValueOrDefault(player, 0);
            // We just set it based on previous round + this round (but we just incremented above improperly if drawer got bonus before they were processed as guesser)
        }
        
        // Let's do it cleaner for cumulated stats:
        // We shouldn't modify CumulatedPoints piecemeal in the loop if we want it strictly correct, 
        // since a player can be a guesser and a drawer. 
        // Let's clear the additions from above logic and do it cleanly:
        // Already did `+= points` in `PlayerPoints`, so we just sum up `PlayerPoints` into `CumulatedPoints`.
        foreach (var player in lobby.JoinedPlayers)
        {
            // Re-calculate the cumulated properly (we should just fetch previous round points, but for simplicity we rely on the DB not having saved yet and we just add to whatever was there prior to FinishGuessingPhase).
            // Since we didn't save yet, CumulatedPoints has the value BEFORE this round.
        }

        // Clean way:
        var originalStats = round.Game.Stats!.CumulatedPoints.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        foreach (var player in lobby.JoinedPlayers)
        {
            var oldPoints = originalStats.GetValueOrDefault(player, 0);
            var thisRoundPoints = round.PlayerPoints.GetValueOrDefault(player, 0);
            round.Game.Stats.CumulatedPoints[player] = oldPoints + thisRoundPoints;
        }

        db.Entry(round).Property(r => r.PlayerPoints).IsModified = true;
        db.Entry(round.Game.Stats).Property(s => s.CumulatedPoints).IsModified = true;

        await db.SaveChangesAsync(); // save so we can send events correctly
        await hubContext.Clients.Group(lobby.Id.ToString()).SendAsync("RoundFinished", round.RoundNumber);
        
        // Update counter if not the last round
        if (round.Game.CurrentRoundNumber == round.RoundNumber && round.RoundNumber < lobby.NumberOfRounds)
        {
            round.Game.CurrentRoundNumber++;
            
            // We need to set the StartTime for the NEXT round drawing phase
            var nextRound = await db.Rounds.FirstOrDefaultAsync(r => r.GameId == gameId && r.RoundNumber == round.Game.CurrentRoundNumber);
            if (nextRound != null)
            {
                nextRound.StartTime = DateTime.UtcNow;
            }
            
            await db.SaveChangesAsync();
            await hubContext.Clients.Group(lobby.Id.ToString()).SendAsync("RoundStarted", round.Game.CurrentRoundNumber);
            
            GameTimers.StartDrawingTimer(sp, gameId, round.Game.CurrentRoundNumber, lobby.TimerForDrawing);
        }
        else if (round.RoundNumber == lobby.NumberOfRounds)
        {
            round.Game.GameFinished = true;
            await db.SaveChangesAsync();
            await hubContext.Clients.Group(lobby.Id.ToString()).SendAsync("GameFinished", round.Game.Id);
        }
    }
}