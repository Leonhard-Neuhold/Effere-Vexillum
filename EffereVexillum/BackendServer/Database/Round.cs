namespace BackendServer.Database;

public class Round
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid GameId { get; set; }
    public Game? Game { get; set; }
    
    public int RoundNumber { get; set; }
    
    public DateTime StartTime { get; set; } // e.g. in seconds
    
    // State: drawing or guessing
    public string State { get; set; } = "drawing";
    
    public bool RoundFinished { get; set; } = false;
    
    public bool FinishedDrawing { get; set; } = false;
    
    // PlayerId -> Random string literal
    public Dictionary<string, string> PlayerFlags { get; set; } = new();
    
    // PlayerId -> Path to picture in minio
    public Dictionary<string, string> PlayerDrawnFlags { get; set; } = new();
    
    // PlayerId -> Points
    public Dictionary<string, int> PlayerPoints { get; set; } = new();
    
    // PlayerId -> Guesses
    public Dictionary<string, string> PlayerGuesses { get; set; } = new();

    // Guesser PlayerId -> Drawer PlayerId
    public Dictionary<string, string> PlayerToDrawingMapping { get; set; } = new();
}
