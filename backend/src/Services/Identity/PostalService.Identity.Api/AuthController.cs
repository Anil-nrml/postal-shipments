using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Infrastructure.Web;
using PostalService.Identity.Application;
using PostalService.Identity.Infrastructure;

namespace PostalService.Identity.Api;

public sealed class AuthCookieOptions
{
    public const string SectionName = "Auth";

    /// <summary>Secure cookies. Browsers treat http://localhost as a secure context, so this stays true locally.</summary>
    public bool CookieSecure { get; set; } = true;

    /// <summary>
    /// Development/Swagger convenience: also return the access token in the JSON body. Keep false in
    /// production so JavaScript never sees a token (HttpOnly cookies only).
    /// </summary>
    public bool ExposeAccessTokenInBody { get; set; }
}

public sealed record LoginResponse(UserDto User, DateTime AccessTokenExpiresAtUtc, string? AccessToken);

[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public sealed class AuthController : ControllerBase
{
    public const string LoginRateLimit = "login";
    private const string InvalidCredentials = "Invalid user name or password.";

    private readonly AuthService _auth;
    private readonly AuthCookieOptions _cookies;

    public AuthController(AuthService auth, IOptions<AuthCookieOptions> cookies)
    {
        _auth = auth;
        _cookies = cookies.Value;
    }

    /// <summary>Sets HttpOnly access/refresh cookies and a readable CSRF cookie.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginRateLimit)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(request, ct);
        if (result is null)
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Login failed", detail: InvalidCredentials);

        WriteSessionCookies(result);
        return Ok(ToResponse(result));
    }

    /// <summary>Rotates the refresh token (cookie) and issues a new access token.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginRateLimit)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var result = await _auth.RefreshAsync(Request.Cookies[AuthCookies.RefreshToken], ct);
        if (result is null)
        {
            ClearSessionCookies();
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Session expired",
                detail: "Please sign in again.");
        }

        WriteSessionCookies(result);
        return Ok(ToResponse(result));
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await _auth.LogoutAsync(Request.Cookies[AuthCookies.RefreshToken], ct);
        ClearSessionCookies();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me([FromServices] ICurrentUser currentUser, CancellationToken ct) =>
        await _auth.GetUserAsync(currentUser.UserId, ct) is { } user ? Ok(user) : Unauthorized();

    private LoginResponse ToResponse(AuthResult r) =>
        new(r.User, r.AccessTokenExpiresAtUtc, _cookies.ExposeAccessTokenInBody ? r.AccessToken : null);

    private void WriteSessionCookies(AuthResult result)
    {
        Response.Cookies.Append(AuthCookies.AccessToken, result.AccessToken,
            Cookie(httpOnly: true, path: "/api", expires: result.AccessTokenExpiresAtUtc));
        Response.Cookies.Append(AuthCookies.RefreshToken, result.RefreshToken,
            Cookie(httpOnly: true, path: "/api/auth", expires: result.RefreshTokenExpiresAtUtc));
        // Readable by the SPA, which echoes it in X-CSRF-Token (double-submit pattern).
        Response.Cookies.Append(CsrfMiddleware.CookieName, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Cookie(httpOnly: false, path: "/", expires: result.RefreshTokenExpiresAtUtc));
    }

    private void ClearSessionCookies()
    {
        Response.Cookies.Delete(AuthCookies.AccessToken, Cookie(true, "/api", null));
        Response.Cookies.Delete(AuthCookies.RefreshToken, Cookie(true, "/api/auth", null));
        Response.Cookies.Delete(CsrfMiddleware.CookieName, Cookie(false, "/", null));
    }

    private CookieOptions Cookie(bool httpOnly, string path, DateTime? expires) => new()
    {
        HttpOnly = httpOnly,
        Secure = _cookies.CookieSecure,
        SameSite = SameSiteMode.Strict,
        Path = path,
        Expires = expires,
        IsEssential = true,
    };
}

[ApiController]
[Route("api/users")]
[Authorize(Policy = Permissions.UsersManage)]
[Produces("application/json")]
public sealed class UsersController : ControllerBase
{
    private readonly UserService _users;

    public UsersController(UserService users) => _users = users;

    [HttpGet]
    public Task<IReadOnlyList<UserDto>> List(CancellationToken ct) => _users.ListAsync(ct);

    [HttpPost]
    [ProducesResponseType<UserDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var user = await _users.CreateAsync(request, ct);
        return Created($"/api/users/{user.Id}", user);
    }

    [HttpPut("{id:guid}")]
    public Task<UserDto> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct) =>
        _users.UpdateAsync(id, request, ct);

    [HttpPost("{id:guid}/password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ResetPassword(Guid id, [FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        await _users.ResetPasswordAsync(id, request, ct);
        return NoContent();
    }

    [HttpGet("roles")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Roles() =>
        BuildingBlocks.Application.Roles.All.ToDictionary(r => r, RolePermissions.For);
}

/// <summary>Minimal OIDC discovery so resource services can fetch the public signing key (JWKS).</summary>
[ApiController]
[AllowAnonymous]
[Route(".well-known")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class WellKnownController : ControllerBase
{
    [HttpGet("openid-configuration")]
    public IActionResult Discovery([FromServices] IOptions<TokenOptions> options)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(new Dictionary<string, object>
        {
            ["issuer"] = options.Value.Issuer,
            ["jwks_uri"] = $"{baseUrl}/.well-known/jwks.json",
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["token_endpoint"] = $"{baseUrl}/api/auth/login",
        });
    }

    [HttpGet("jwks.json")]
    [ResponseCache(Duration = 300)]
    public IActionResult Jwks([FromServices] SigningKeyProvider keys) => Ok(keys.Jwks);
}
