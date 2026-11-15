using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Infrastructure.Persistence;
using PostalService.Identity.Application;
using PostalService.Identity.Domain;

namespace PostalService.Identity.Infrastructure;

/// <summary>identity.db — owned exclusively by the Identity service.</summary>
public sealed class IdentityDbContext : DbContext, IUnitOfWork
{
    public IdentityDbContext(DbContextOptions<IdentityDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.UseUtcDateTimes();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("Users");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Property(x => x.UserName).HasMaxLength(50).IsRequired();
            b.Property(x => x.NormalizedUserName).HasMaxLength(50).IsRequired();
            b.HasIndex(x => x.NormalizedUserName).IsUnique();
            b.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            b.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
            b.Property(x => x.Role).HasMaxLength(30).IsRequired();
            b.Property(x => x.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<RefreshToken>(b =>
        {
            b.ToTable("RefreshTokens");
            b.HasKey(x => x.Id);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => x.FamilyId);
            b.HasIndex(x => new { x.UserId, x.RevokedAtUtc });
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class UserRepository : IUserRepository
{
    private readonly IdentityDbContext _db;

    public UserRepository(IdentityDbContext db) => _db = db;

    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) => _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByUserNameAsync(string userName, CancellationToken ct)
    {
        var normalized = User.Normalize(userName);
        return _db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized, ct);
    }

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct) =>
        await _db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(ct);

    public Task<bool> AnyAsync(CancellationToken ct) => _db.Users.AnyAsync(ct);

    public void Add(User user) => _db.Users.Add(user);
}

public sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly IdentityDbContext _db;

    public RefreshTokenRepository(IdentityDbContext db) => _db = db;

    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct) =>
        _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task<IReadOnlyList<RefreshToken>> GetActiveFamilyAsync(Guid familyId, CancellationToken ct) =>
        await _db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null).ToListAsync(ct);

    public async Task<IReadOnlyList<RefreshToken>> GetActiveForUserAsync(Guid userId, CancellationToken ct) =>
        await _db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAtUtc == null).ToListAsync(ct);

    public void Add(RefreshToken token) => _db.RefreshTokens.Add(token);
}

public sealed class SeedUser
{
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public static class IdentitySeeder
{
    /// <summary>
    /// Creates the users listed under "Seed:Users" when the database is empty. Passwords come from
    /// configuration (appsettings.Development.json locally, a secret store elsewhere) — never from code.
    /// </summary>
    public static async Task SeedAsync(IdentityDbContext db, IServiceProvider services)
    {
        if (await db.Users.AnyAsync()) return;
        var config = services.GetRequiredService<IConfiguration>();
        var hasher = services.GetRequiredService<IPasswordHasher>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("IdentitySeeder");
        var seedUsers = config.GetSection("Seed:Users").Get<List<SeedUser>>() ?? new List<SeedUser>();

        foreach (var seed in seedUsers.Where(s => !string.IsNullOrWhiteSpace(s.Password)))
            db.Users.Add(User.Create(seed.UserName, seed.DisplayName, seed.Role, hasher.Hash(seed.Password), DateTime.UtcNow));

        await db.SaveChangesAsync();
        logger.LogInformation("Seeded {Count} users", seedUsers.Count);
    }
}
