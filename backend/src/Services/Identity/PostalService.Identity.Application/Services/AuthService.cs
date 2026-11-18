using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PostalService.BuildingBlocks.Application;
using PostalService.Identity.Domain;

namespace PostalService.Identity.Application;

// ---------- Contracts ----------

public sealed record LoginRequest(string UserName, string Password);

public sealed record UserDto(Guid Id, string UserName, string DisplayName, string Role, bool IsActive,
    IReadOnlyList<string> Permissions, DateTime? LastLoginAtUtc);

public sealed record AuthResult(UserDto User, string AccessToken, DateTime AccessTokenExpiresAtUtc,
    string RefreshToken, DateTime RefreshTokenExpiresAtUtc);

public sealed record CreateUserRequest(string UserName, string DisplayName, string Role, string Password);

public sealed record UpdateUserRequest(string DisplayName, string Role, bool IsActive);

public sealed record ResetPasswordRequest(string NewPassword);

// ---------- Ports ----------

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<User?> GetByUserNameAsync(string userName, CancellationToken ct);
    Task<IReadOnlyList<User>> ListAsync(CancellationToken ct);
    Task<bool> AnyAsync(CancellationToken ct);
    void Add(User user);
}

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct);
    Task<IReadOnlyList<RefreshToken>> GetActiveFamilyAsync(Guid familyId, CancellationToken ct);
    Task<IReadOnlyList<RefreshToken>> GetActiveForUserAsync(Guid userId, CancellationToken ct);
    void Add(RefreshToken token);
}

public interface IPasswordHasher
{
    string Hash(string password);
    PasswordCheck Verify(string hash, string password);
}

public enum PasswordCheck { Failed, Success, SuccessRehashNeeded }

public interface ITokenService
{
    (string Token, DateTime ExpiresAtUtc) CreateAccessToken(User user, IReadOnlyList<string> permissions);
    (string RawToken, string Hash) CreateRefreshToken();
    string HashRefreshToken(string rawToken);
    TimeSpan RefreshTokenLifetime { get; }
}

// ---------- Validators ----------

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.UserName).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public static class PasswordPolicy
{
    public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MinimumLength(10).MaximumLength(128)
            .Matches("[A-Z]").WithMessage("Password needs an upper-case letter.")
            .Matches("[a-z]").WithMessage("Password needs a lower-case letter.")
            .Matches("[0-9]").WithMessage("Password needs a digit.")
            .Matches("[^A-Za-z0-9]").WithMessage("Password needs a symbol.");
}

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.UserName).NotEmpty().MaximumLength(50).Matches("^[A-Za-z0-9._-]+$");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Role).Must(r => Roles.All.Contains(r)).WithMessage("Unknown role.");
        RuleFor(x => x.Password).StrongPassword();
    }
}

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Role).Must(r => Roles.All.Contains(r)).WithMessage("Unknown role.");
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator() => RuleFor(x => x.NewPassword).StrongPassword();
}

// ---------- Use cases ----------

public sealed class AuthService
{
    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _hasher;
    private readonly ITokenService _tokens;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _clock;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly ILogger<AuthService> _logger;

    public AuthService(IUserRepository users, IRefreshTokenRepository refreshTokens, IPasswordHasher hasher,
        ITokenService tokens, IUnitOfWork unitOfWork, TimeProvider clock, IValidator<LoginRequest> loginValidator,
        ILogger<AuthService> logger)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _hasher = hasher;
        _tokens = tokens;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _loginValidator = loginValidator;
        _logger = logger;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <returns>Null for any failure; callers return one generic message to avoid user enumeration.</returns>
    public async Task<AuthResult?> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        await _loginValidator.ValidateAndThrowAsync(request, ct);
        var user = await _users.GetByUserNameAsync(request.UserName, ct);
        var now = UtcNow;

        if (user is null)
        {
            _hasher.Hash(request.Password); // equalise timing with the "wrong password" path
            _logger.LogWarning("Authentication attempt failed");
            return null;
        }

        if (!user.IsActive || user.IsLockedOut(now))
        {
            _logger.LogWarning("Authentication attempt failed");
            return null;
        }

        var check = _hasher.Verify(user.PasswordHash, request.Password);
        if (check == PasswordCheck.Failed)
        {
            user.RegisterFailedLogin(now);
            await _unitOfWork.SaveChangesAsync(ct);
            _logger.LogWarning("Authentication attempt failed for user {UserId}", user.Id);
            return null;
        }

        if (check == PasswordCheck.SuccessRehashNeeded)
            user.ChangePasswordHash(_hasher.Hash(request.Password));

        user.RegisterSuccessfulLogin(now);
        var result = Issue(user, familyId: Guid.NewGuid(), now);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("Authentication succeeded for user {UserId}", user.Id);
        return result;
    }

    /// <summary>
    /// Rotates the refresh token. Reusing a token that was already rotated revokes the whole family:
    /// either the legitimate user or an attacker holds a stolen copy, and we cannot tell which.
    /// </summary>
    public async Task<AuthResult?> RefreshAsync(string? rawRefreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken)) return null;
        var now = UtcNow;
        var stored = await _refreshTokens.GetByHashAsync(_tokens.HashRefreshToken(rawRefreshToken), ct);
        if (stored is null) return null;

        if (stored.RevokedAtUtc is not null)
        {
            foreach (var token in await _refreshTokens.GetActiveFamilyAsync(stored.FamilyId, ct))
                token.Revoke(now);
            await _unitOfWork.SaveChangesAsync(ct);
            return null;
        }

        if (!stored.IsActive(now)) return null;

        var user = await _users.GetByIdAsync(stored.UserId, ct);
        if (user is null || !user.IsActive) return null;

        var result = Issue(user, stored.FamilyId, now, previous: stored);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("Session refreshed for user {UserId}", user.Id);
        return result;
    }

    public async Task LogoutAsync(string? rawRefreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken)) return;
        var stored = await _refreshTokens.GetByHashAsync(_tokens.HashRefreshToken(rawRefreshToken), ct);
        if (stored is null) return;
        foreach (var token in await _refreshTokens.GetActiveFamilyAsync(stored.FamilyId, ct))
            token.Revoke(UtcNow);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("User signed out {UserId}", stored.UserId);
    }

    public async Task<UserDto?> GetUserAsync(Guid id, CancellationToken ct) =>
        await _users.GetByIdAsync(id, ct) is { IsActive: true } user ? UserMapper.ToDto(user) : null;

    private AuthResult Issue(User user, Guid familyId, DateTime now, RefreshToken? previous = null)
    {
        var permissions = RolePermissions.For(user.Role);
        var (access, accessExpires) = _tokens.CreateAccessToken(user, permissions);
        var (raw, hash) = _tokens.CreateRefreshToken();
        var refresh = RefreshToken.Issue(user.Id, familyId, hash, now, _tokens.RefreshTokenLifetime);
        _refreshTokens.Add(refresh);
        previous?.Revoke(now, refresh.Id);
        return new AuthResult(UserMapper.ToDto(user), access, accessExpires, raw, refresh.ExpiresAtUtc);
    }
}

public sealed class UserService
{
    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _hasher;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _clock;
    private readonly IValidator<CreateUserRequest> _createValidator;
    private readonly IValidator<UpdateUserRequest> _updateValidator;
    private readonly IValidator<ResetPasswordRequest> _passwordValidator;
    private readonly ILogger<UserService> _logger;

    public UserService(IUserRepository users, IRefreshTokenRepository refreshTokens, IPasswordHasher hasher,
        IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider clock,
        IValidator<CreateUserRequest> createValidator, IValidator<UpdateUserRequest> updateValidator,
        IValidator<ResetPasswordRequest> passwordValidator, ILogger<UserService> logger)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _hasher = hasher;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _clock = clock;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _passwordValidator = passwordValidator;
        _logger = logger;
    }

    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken ct) =>
        (await _users.ListAsync(ct)).Select(UserMapper.ToDto).ToList();

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct)
    {
        await _createValidator.ValidateAndThrowAsync(request, ct);
        if (await _users.GetByUserNameAsync(request.UserName, ct) is not null)
            throw new ConflictException("user.exists", $"User '{request.UserName}' already exists.");

        var user = User.Create(request.UserName, request.DisplayName, request.Role, _hasher.Hash(request.Password),
            _clock.GetUtcNow().UtcDateTime);
        _users.Add(user);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("User created {UserId}", user.Id);
        return UserMapper.ToDto(user);
    }

    public async Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        await _updateValidator.ValidateAndThrowAsync(request, ct);
        var user = await _users.GetByIdAsync(id, ct) ?? throw new NotFoundException("User", id);
        if (user.Id == _currentUser.UserId && (!request.IsActive || request.Role != Roles.Administrator))
            throw new ConflictException("user.self-demotion", "You cannot deactivate or demote your own account.");

        var privilegesChanged = user.Role != request.Role || (user.IsActive && !request.IsActive);
        user.Update(request.DisplayName, request.Role, request.IsActive);
        if (privilegesChanged) await RevokeSessionsAsync(user.Id, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("User updated {UserId}", user.Id);
        return UserMapper.ToDto(user);
    }

    public async Task ResetPasswordAsync(Guid id, ResetPasswordRequest request, CancellationToken ct)
    {
        await _passwordValidator.ValidateAndThrowAsync(request, ct);
        var user = await _users.GetByIdAsync(id, ct) ?? throw new NotFoundException("User", id);
        user.ChangePasswordHash(_hasher.Hash(request.NewPassword));
        await RevokeSessionsAsync(user.Id, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("User password reset {UserId}", user.Id);
    }

    /// <summary>Access tokens are short-lived (15 min); revoking refresh tokens ends the session at next refresh.</summary>
    private async Task RevokeSessionsAsync(Guid userId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var token in await _refreshTokens.GetActiveForUserAsync(userId, ct))
            token.Revoke(now);
    }
}

public static class UserMapper
{
    public static UserDto ToDto(User u) => new(u.Id, u.UserName, u.DisplayName, u.Role, u.IsActive,
        RolePermissions.For(u.Role), u.LastLoginAtUtc);
}

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        return services;
    }
}
