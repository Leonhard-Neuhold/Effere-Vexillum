using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BackendServer.Database;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext(options);
