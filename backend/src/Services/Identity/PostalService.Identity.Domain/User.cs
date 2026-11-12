using PostalService.BuildingBlocks.Domain;

namespace PostalService.Identity.Domain;

public sealed class User : AggregateRoot<Guid>
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private static readonly string[] ValidRoles =
        { "Administrator", "PostOfficeManager", "ShipmentOperator", "ShipmentViewer" };

    private User() { } // EF Core

    public string UserName { get; private set; } = string.Empty;
    public string NormalizedUserName { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string Role { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTime? LockoutEndUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastLoginAtUtc { get; private set; }

    public static User Create(string userName, string displayName, string role, string passwordHash, DateTime utcNow)
    {
        var name = Guard.NotBlank(userName, "userName", 50);
        return new User
        {
            Id = Guid.NewGuid(),
            UserName = name,
            NormalizedUserName = Normalize(name),
            DisplayName = Guard.NotBlank(displayName, "displayName", 100),
            Role = ValidateRole(role),
            PasswordHash = passwordHash,
            IsActive = true,
            CreatedAtUtc = utcNow,
            Version = 1,
        };
    }

    public static string Normalize(string userName) => userName.Trim().ToUpperInvariant();

    public bool IsLockedOut(DateTime utcNow) => LockoutEndUtc is { } end && end > utcNow;

    public void RegisterFailedLogin(DateTime utcNow)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= MaxFailedAttempts)
        {
            LockoutEndUtc = utcNow.Add(LockoutDuration);
            FailedLoginCount = 0;
        }
        IncrementVersion();
    }

    public void RegisterSuccessfulLogin(DateTime utcNow)
    {
        FailedLoginCount = 0;
        LockoutEndUtc = null;
        LastLoginAtUtc = utcNow;
        IncrementVersion();
    }

    public void Update(string displayName, string role, bool isActive)
    {
        DisplayName = Guard.NotBlank(displayName, "displayName", 100);
        Role = ValidateRole(role);
        IsActive = isActive;
        IncrementVersion();
    }

    public void ChangePasswordHash(string passwordHash)
    {
        PasswordHash = passwordHash;
        IncrementVersion();
    }

    private static string ValidateRole(string role) =>
        ValidRoles.Contains(role)
            ? role
            : throw new DomainException("role.invalid", $"Unknown role '{role}'.");
}

/// <summary>
/// Opaque refresh token. Only a SHA-256 hash is stored. Tokens rotate on every use; presenting an
/// already-rotated token revokes the whole family (theft detection).
/// </summary>
public sealed class RefreshToken
{
    private RefreshToken() { } // EF Core

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid FamilyId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public Guid? ReplacedByTokenId { get; private set; }

    public static RefreshToken Issue(Guid userId, Guid familyId, string tokenHash, DateTime utcNow, TimeSpan lifetime) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = tokenHash,
            CreatedAtUtc = utcNow,
            ExpiresAtUtc = utcNow.Add(lifetime),
        };

    public bool IsActive(DateTime utcNow) => RevokedAtUtc is null && ExpiresAtUtc > utcNow;

    public void Revoke(DateTime utcNow, Guid? replacedBy = null)
    {
        RevokedAtUtc ??= utcNow;
        ReplacedByTokenId ??= replacedBy;
    }
}
