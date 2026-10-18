using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using BackendServer.Database;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace BackendServer.Api;

// "Stille Post" game mode (lobby GameMode == "Chain").
// Every player starts a chain with a secret flag. In each step a player works on a different chain:
// even steps draw (the flag name on step 0, the previous guess afterwards), odd steps guess the previous drawing.
// There are as many steps as players, so every chain passes through every player exactly once.
// Game.CurrentRoundNumber is reused as the 1-based current step number.
public static class ChainEndpoints
{
    public static void AddChainEndpoints(this WebApplication app)
    {
        // Id of the running chain game of a lobby
        app.MapGet("/api/lobbies/{id:guid}/chain-game", async (AppDbContext db, Guid id) =>
        {
            var game = await db.Games
                .Where(g => g.LobbyId == id && !g.GameFinished)
                .OrderByDescending(g => g.CurrentRoundNumber)
                .FirstOrDefaultAsync();
            return game is null ? Results.NotFound("No active game.") : Results.Ok(new { GameId = game.Id });
        }).RequireAuthorization();

        // What the calling player has to do in the current step
        app.MapGet("/api/games/{id:guid}/chain-task", async (AppDbContext db, ClaimsPrincipal user, Guid id) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Results.Unauthorized();

            var game = await db.Games.FindAsync(id);
            if (game is null) return Results.NotFound("Game not found.");

            var chains = await db.Chains.Where(c => c.GameId == id).OrderBy(c => c.ChainIndex).ToListAsync();
            if (chains.Count == 0) return Results.NotFound("Not a chain game.");

            var stepIndex = game.CurrentRoundNumber - 1;
            var chain = ChainLogic.ChainForPlayer(chains, userId, stepIndex);
            if (chain is null) return Results.Forbid();

            var step = chain.Steps[stepIndex];
            var previous = stepIndex > 0 ? chain.Steps[stepIndex - 1].Content : null;

            return Results.Ok(new
            {
                GameId = game.Id,
                StepNumber = game.CurrentRoundNumber,
                TotalSteps = chains.Count,
                GameFinished = game.GameFinished,
                Type = step.Type,
                FlagToDraw = step.Type == "draw" ? (stepIndex == 0 ? chain.OriginalFlag : previous) : null,
                ImageToGuess = step.Type == "guess" ? previous : null,
                Submitted = step.Content != null
            });
        }).RequireAuthorization();

        // Submit the result of the current step (drawing path or guess text)
        app.MapPost("/api/chain/submit", async (AppDbContext db, ClaimsPrincipal user, ChainSubmitRequest request, IServiceProvider sp) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Results.Unauthorized();

            var game = await db.Games.FindAsync(request.GameId);
            if (game is null) return Results.NotFound("Game not found.");
            if (game.GameFinished) return Results.BadRequest("Game is already finished.");

            var stepIndex = game.CurrentRoundNumber - 1;
            if (request.StepNumber != game.CurrentRoundNumber) return Results.BadRequest("Step is not active.");
            if (string.IsNullOrWhiteSpace(request.Content)) return Results.BadRequest("Content is required.");

            var chains = await db.Chains.Where(c => c.GameId == game.Id).OrderBy(c => c.ChainIndex).ToListAsync();
            var chain = ChainLogic.ChainForPlayer(chains, userId, stepIndex);
            if (chain is null) return Results.Forbid();

            var step = chain.Steps[stepIndex];
            if (step.Content != null) return Results.BadRequest("Already submitted.");

            step.Content = request.Content.Trim();
            db.Entry(chain).Property(c => c.Steps).IsModified = true;
            await db.SaveChangesAsync();

            if (chains.All(c => c.Steps[stepIndex].Content != null))
            {
                await ChainLogic.FinishStep(sp, game.Id, game.CurrentRoundNumber);
            }

            return Results.Ok();
        }).RequireAuthorization();

        // Full chains for the reveal, only available once the game is over
        app.MapGet("/api/games/{id:guid}/chains", async (AppDbContext db, Guid id) =>
        {
            var game = await db.Games.FindAsync(id);
            if (game is null) return Results.NotFound("Game not found.");
            if (!game.GameFinished) return Results.BadRequest("Game is not finished yet.");

            var chains = await db.Chains.Where(c => c.GameId == id).OrderBy(c => c.ChainIndex).ToListAsync();

            var playerIds = chains.Select(c => c.OwnerPlayerId).ToList();
            var names = await db.Users
                .Where(u => playerIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.UserName ?? "Unknown");

            return Results.Ok(chains.Select(c => new
            {
                Owner = names.GetValueOrDefault(c.OwnerPlayerId) ?? "Unknown",
                c.OriginalFlag,
                Steps = c.Steps.Select(s => new
                {
                    s.StepIndex,
                    Player = names.GetValueOrDefault(s.PlayerId) ?? "Unknown",
                    s.Type,
                    s.Content
                })
            }));
        }).RequireAuthorization();
    }
}

public record ChainSubmitRequest(Guid GameId, int StepNumber, string Content);

public static class ChainLogic
{
    public static string StepType(int stepIndex) => stepIndex % 2 == 0 ? "draw" : "guess";

    // In step k, player i works on chain (i - k) mod n
    public static Chain? ChainForPlayer(List<Chain> chains, string playerId, int stepIndex)
    {
        var n = chains.Count;
        var playerIndex = chains.FindIndex(c => c.OwnerPlayerId == playerId);
        if (playerIndex < 0 || stepIndex < 0 || stepIndex >= n) return null;
        return chains[((playerIndex - stepIndex) % n + n) % n];
    }

    public static async Task<IResult> StartGame(AppDbContext db, IServiceProvider sp, Lobby lobby)
    {
        var players = lobby.JoinedPlayers.OrderBy(_ => Guid.NewGuid()).ToList();
        if (players.Count < 2) return Results.BadRequest("Chain mode needs at least 2 players.");

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
        if (!validFlags.Any()) validFlags.Add("Unknown");

        var rnd = new Random();
        var n = players.Count;

        for (int c = 0; c < n; c++)
        {
            var chain = new Chain
            {
                Id = Guid.NewGuid(),
                GameId = game.Id,
                ChainIndex = c,
                OwnerPlayerId = players[c],
                OriginalFlag = validFlags[rnd.Next(validFlags.Count)]
            };

            // Chain c is worked on by player (c + k) mod n in step k
            for (int k = 0; k < n; k++)
            {
                chain.Steps.Add(new ChainStep
                {
                    StepIndex = k,
                    PlayerId = players[(c + k) % n],
                    Type = StepType(k)
                });
            }

            db.Chains.Add(chain);
        }

        db.Games.Add(game);
        await db.SaveChangesAsync();

        var hubContext = sp.GetRequiredService<IHubContext<GameHub>>();
        await hubContext.Clients.Group(lobby.Id.ToString()).SendAsync("GameStarted", game.Id);
        await hubContext.Clients.Group(lobby.Id.ToString()).SendAsync("RoundStarted", 1);

        StartStepTimer(sp, game.Id, 1, StepSeconds(lobby, 0));

        return Results.Ok(new { GameId = game.Id });
    }

    private static int StepSeconds(Lobby lobby, int stepIndex) =>
        StepType(stepIndex) == "draw" ? lobby.TimerForDrawing : lobby.TimerForGuessing;

    public static void StartStepTimer(IServiceProvider sp, Guid gameId, int stepNumber, int seconds)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await FinishStep(sp, gameId, stepNumber);
        });
    }

    // Advances the game past the given step, called when everyone submitted or the timer ran out.
    // Unsubmitted steps stay empty (Content == null), the next player then works with a gap.
    public static async Task FinishStep(IServiceProvider sp, Guid gameId, int stepNumber)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<GameHub>>();

        var game = await db.Games.Include(g => g.Lobby).FirstOrDefaultAsync(g => g.Id == gameId);
        if (game?.Lobby == null) return;

        var totalSteps = await db.Chains.CountAsync(c => c.GameId == gameId);
        var isLast = stepNumber >= totalSteps;

        // Atomic compare-and-set so the timer and the last submit can't both advance the same step
        var open = db.Games.Where(g => g.Id == gameId && g.CurrentRoundNumber == stepNumber && !g.GameFinished);
        var advanced = isLast
            ? await open.ExecuteUpdateAsync(s => s.SetProperty(g => g.GameFinished, true))
            : await open.ExecuteUpdateAsync(s => s.SetProperty(g => g.CurrentRoundNumber, stepNumber + 1));
        if (advanced == 0) return;

        var lobby = game.Lobby;
        if (isLast)
        {
            await db.Lobbies.Where(l => l.Id == lobby.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.GameStarted, false));
            await hubContext.Clients.Group(lobby.Id.ToString()).SendAsync("GameFinished", gameId);
        }
        else
        {
            await hubContext.Clients.Group(lobby.Id.ToString()).SendAsync("RoundStarted", stepNumber + 1);
            StartStepTimer(sp, gameId, stepNumber + 1, StepSeconds(lobby, stepNumber));
        }
    }
}
