namespace BackendServer.Database;

public class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid LobbyId { get; set; }
    public Lobby? Lobby { get; set; }
    
    public int CurrentRoundNumber { get; set; }
    
    public bool GameFinished { get; set; } = false;
    
    public List<Round> Rounds { get; set; } = new();
    
    public Stats? Stats { get; set; }
}
