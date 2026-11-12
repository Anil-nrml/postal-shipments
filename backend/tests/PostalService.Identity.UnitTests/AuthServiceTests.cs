using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;
using PostalService.BuildingBlocks.Application;
using PostalService.Identity.Application;
using PostalService.Identity.Domain;
using PostalService.Identity.Infrastructure;
using PostalService.Tests;

namespace PostalService.Identity.UnitTests;

/// <summary>In-memory fakes keep these tests about behaviour, not mocking setup.</summary>
internal sealed class InMemoryUsers : IUserRepository
{
    public readonly List<User> Items = new();
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(u => u.Id == id));
    public Task<User?> GetByUserNameAsync(string n, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(u => u.NormalizedUserName == User.Normalize(n)));
    public Task<IReadOnlyList<User>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<User>>(Items);
    public Task<bool> AnyAsync(CancellationToken ct) => Task.FromResult(Items.Count > 0);
    public void Add(User user) => Items.Add(user);
}

internal sealed class InMemoryRefreshTokens : IRefreshTokenRepository
{
    public readonly List<RefreshToken> Items = new();
    public Task<RefreshToken?> GetByHashAsync(string h, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(t => t.TokenHash == h));
    public Task<IReadOnlyList<RefreshToken>> GetActiveFamilyAsync(Guid f, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RefreshToken>>(Items.Where(t => t.FamilyId == f && t.RevokedAtUtc == null).ToList());
    public Task<IReadOnlyList<RefreshToken>> GetActiveForUserAsync(Guid u, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RefreshToken>>(Items.Where(t => t.UserId == u && t.RevokedAtUtc == null).ToList());
    public void Add(RefreshToken token) => Items.Add(token);
}

public sealed class AuthServiceTests : IDisposable
{
    private readonly TestClock _clock = new();
    private readonly InMemoryUsers _users = new();
    private readonly InMemoryRefreshTokens _tokens = new();
    private readonly AspNetPasswordHasher _hasher = new();
    private readonly SigningKeyProvider _keys;
    private readonly JwtTokenService _tokenService;
    private readonly string _keyDir = Path.Combine(Path.GetTempPath(), "postal-tests-" + Guid.NewGuid().ToString("N"));

    public AuthServiceTests()
    {
        var options = Options.Create(new TokenOptions { SigningKeyPath = Path.Combine(_keyDir, "key.pem") });
        var env = Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Testing" && e.ContentRootPath == _keyDir);
        _keys = new SigningKeyProvider(options, env, NullLogger<SigningKeyProvider>.Instance);
        _tokenService = new JwtTokenService(options, _keys, _clock);
        _users.Add(User.Create("operator", "Clerk", Roles.ShipmentOperator, _hasher.Hash("Operator@12345!"), _clock.UtcNow));
    }

    public void Dispose()
    {
        if (Directory.Exists(_keyDir)) Directory.Delete(_keyDir, recursive: true);
    }

    private AuthService Sut() => new(_users, _tokens, _hasher, _tokenService, Mock.Of<IUnitOfWork>(), _clock,
        new LoginRequestValidator());

    [Fact]
    public async Task Valid_login_issues_tokens_with_role_permissions()
    {
        var result = await Sut().LoginAsync(new LoginRequest("OPERATOR", "Operator@12345!"), default);

        result.Should().NotBeNull();
        result!.User.Permissions.Should().Contain(Permissions.ShipmentsUpdateStatus).And.NotContain(Permissions.PostOfficesWrite);
        _tokens.Items.Should().ContainSingle(t => t.IsActive(_clock.UtcNow));

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(result.AccessToken, new TokenValidationParameters
        {
            ValidIssuer = "postal-identity",
            ValidAudience = "postal-api",
            IssuerSigningKey = _keys.PublicKey,
            LifetimeValidator = (_, _, _, _) => true, // test clock is fixed in time
        });
        validation.IsValid.Should().BeTrue();
        validation.Claims["role"].Should().Be(Roles.ShipmentOperator);
    }

    [Fact]
    public async Task Wrong_password_fails_and_locks_out_after_five_attempts()
    {
        var sut = Sut();
        for (var i = 0; i < User.MaxFailedAttempts; i++)
            (await sut.LoginAsync(new LoginRequest("operator", "wrong"), default)).Should().BeNull();

        (await sut.LoginAsync(new LoginRequest("operator", "Operator@12345!"), default))
            .Should().BeNull("the account is locked");

        _clock.Advance(User.LockoutDuration + TimeSpan.FromSeconds(1));
        (await sut.LoginAsync(new LoginRequest("operator", "Operator@12345!"), default)).Should().NotBeNull();
    }

    [Fact]
    public async Task Unknown_user_fails_the_same_way()
    {
        (await Sut().LoginAsync(new LoginRequest("nobody", "whatever"), default)).Should().BeNull();
    }

    [Fact]
    public async Task Refresh_rotates_the_token()
    {
        var sut = Sut();
        var login = await sut.LoginAsync(new LoginRequest("operator", "Operator@12345!"), default);
        var refreshed = await sut.RefreshAsync(login!.RefreshToken, default);

        refreshed.Should().NotBeNull();
        refreshed!.RefreshToken.Should().NotBe(login.RefreshToken);
        _tokens.Items.Count(t => t.IsActive(_clock.UtcNow)).Should().Be(1);
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_the_whole_family()
    {
        var sut = Sut();
        var login = await sut.LoginAsync(new LoginRequest("operator", "Operator@12345!"), default);
        var refreshed = await sut.RefreshAsync(login!.RefreshToken, default);

        (await sut.RefreshAsync(login.RefreshToken, default)).Should().BeNull("replay of a rotated token");
        (await sut.RefreshAsync(refreshed!.RefreshToken, default)).Should().BeNull("the family was revoked");
        _tokens.Items.Should().OnlyContain(t => t.RevokedAtUtc != null);
    }

    [Fact]
    public async Task Logout_revokes_the_session()
    {
        var sut = Sut();
        var login = await sut.LoginAsync(new LoginRequest("operator", "Operator@12345!"), default);
        await sut.LogoutAsync(login!.RefreshToken, default);
        (await sut.RefreshAsync(login.RefreshToken, default)).Should().BeNull();
    }

    [Fact]
    public void Jwks_exposes_only_public_key_material()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(_keys.Jwks);
        json.Should().Contain("\"n\"").And.Contain("\"e\"").And.NotContain("\"d\"").And.NotContain("\"p\"");
    }
}

public class RolePermissionTests
{
    [Fact]
    public void Viewer_is_read_only() =>
        RolePermissions.For(Roles.ShipmentViewer).Should().OnlyContain(p => p.EndsWith(".read"));

    [Fact]
    public void Only_administrators_can_correct_statuses_and_manage_users() =>
        Roles.All.Where(r => r != Roles.Administrator).Should().AllSatisfy(r =>
            RolePermissions.For(r).Should().NotContain(new[] { Permissions.ShipmentsCorrectStatus, Permissions.UsersManage }));
}
