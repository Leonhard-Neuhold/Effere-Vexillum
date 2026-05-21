namespace BackendServer.Database;

public class Flag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    // The name of the flag (e.g., "Germany", "Texas", "United Nations")
    public string Name { get; set; } = string.Empty;
    
    // The category (e.g., "country", "territory", "subcountry", "province", "state", "county", "organization", "military", "civil rights")
    public string Category { get; set; } = string.Empty;

    // Alternative names or translations (e.g. German names, aliases)
    public List<string> Aliases { get; set; } = new();
}
