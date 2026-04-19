namespace BackendServer.Database;

public class Stats
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid GameId { get; set; }
    public Game? Game { get; set; }
    
    // PlayerId -> Cumulated Points
    public Dictionary<string, int> CumulatedPoints { get; set; } = new();
}
