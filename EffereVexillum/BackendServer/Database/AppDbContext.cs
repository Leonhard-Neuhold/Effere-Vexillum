using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BackendServer.Database;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext(options)
{
    public DbSet<Lobby> Lobbies { get; set; } = null!;
    public DbSet<Game> Games { get; set; } = null!;
    public DbSet<Round> Rounds { get; set; } = null!;
    public DbSet<Stats> Stats { get; set; } = null!;
    public DbSet<Flag> Flags { get; set; } = null!;
    public DbSet<UserProfile> UserProfiles { get; set; } = null!;
    
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        
        // Setup JSON column mappings for dictionaries
        builder.Entity<Round>()
            .Property(r => r.PlayerFlags)
            .HasColumnType("jsonb");
            
        builder.Entity<Round>()
            .Property(r => r.PlayerPoints)
            .HasColumnType("jsonb");
            
        builder.Entity<Round>()
            .Property(r => r.PlayerGuesses)
            .HasColumnType("jsonb");
            
        builder.Entity<Round>()
            .Property(r => r.PlayerDrawnFlags)
            .HasColumnType("jsonb");
            
        builder.Entity<Round>()
            .Property(r => r.PlayerToDrawingMapping)
            .HasColumnType("jsonb");
            
        builder.Entity<Stats>()
            .Property(s => s.CumulatedPoints)
            .HasColumnType("jsonb");
            
        builder.Entity<Flag>()
            .Property(f => f.Aliases)
            .HasColumnType("jsonb");
    }
}
