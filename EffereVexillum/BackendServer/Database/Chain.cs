namespace BackendServer.Database;

// One "Stille Post" chain: starts with a flag, passed through every player alternating draw/guess.
public class Chain
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GameId { get; set; }
    public Game? Game { get; set; }

    // Position of the chain within the game; chain i starts with player i
    public int ChainIndex { get; set; }

    public string OwnerPlayerId { get; set; } = string.Empty;

    public string OriginalFlag { get; set; } = string.Empty;

    // Ordered by StepIndex; one entry per step of the game
    public List<ChainStep> Steps { get; set; } = new();
}

public class ChainStep
{
    public int StepIndex { get; set; }

    public string PlayerId { get; set; } = string.Empty;

    // "draw" or "guess"
    public string Type { get; set; } = "draw";

    // Path to picture in minio for "draw", the guessed text for "guess"; null if skipped or not yet submitted
    public string? Content { get; set; }
}
