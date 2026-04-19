namespace BackendServer.Database;
public class Lobby
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreationDate { get; set; } = DateTime.UtcNow;
    public string UniqueLobbyUrl { get; set; } = string.Empty;
    public string UniqueJoinLink { get; set; } = string.Empty;
    public List<string> JoinedPlayers { get; set; } = new();
    public string GameMode { get; set; } = string.Empty;
    public string GameAdminId { get; set; } = string.Empty;
    public int NumberOfRounds { get; set; }
    public string CustomFilter { get; set; } = string.Empty;
    public bool GameStarted { get; set; }
    public int TimerForDrawing { get; set; } // e.g. in seconds
    public int TimerForGuessing { get; set; } // e.g. in seconds
    public List<Game> Games { get; set; } = new();
}
