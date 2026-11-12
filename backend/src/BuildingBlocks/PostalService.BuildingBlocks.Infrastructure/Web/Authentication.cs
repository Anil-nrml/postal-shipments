using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using PostalService.BuildingBlocks.Application;

namespace PostalService.BuildingBlocks.Infrastructure.Web;

public sealed class JwtSettings
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "postal-identity";
    public string Audience { get; set; } = "postal-api";

    /// <summary>
    /// OIDC discovery document of the Identity service. Resource services download the RSA public keys
    /// (JWKS) from here, so only Identity holds a signing key (ADR-0005).
    /// </summary>
    public string? MetadataAddress { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;
}

public static class AuthenticationExtensions
{
    /// <summary>
    /// JWT bearer validation for resource services. The token is read from the Authorization header
    /// (API clients, Swagger, tests) or, for the browser, from the HttpOnly access_token cookie.
    /// Each permission becomes a policy with the same name.
    /// </summary>
    public static IServiceCollection AddPostalAuthentication(this IServiceCollection services,
        IConfiguration configuration, Action<JwtBearerOptions>? configure = null)
    {
        var settings = configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.MetadataAddress = settings.MetadataAddress!;
                options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = PostalClaims.UserName,
                    RoleClaimType = PostalClaims.Role,
                    ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (string.IsNullOrEmpty(ctx.Token) &&
                            ctx.Request.Cookies.TryGetValue(AuthCookies.AccessToken, out var cookie))
                            ctx.Token = cookie;
                        return Task.CompletedTask;
                    },
                };
                configure?.Invoke(options);
            });

        services.AddPermissionPolicies();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        return services;
    }

    public static IServiceCollection AddPermissionPolicies(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            foreach (var permission in Permissions.All)
                options.AddPolicy(permission, p => p.RequireAuthenticatedUser().RequireClaim(PostalClaims.Permission, permission));

            // Deny by default: every endpoint needs an authenticated user unless marked [AllowAnonymous].
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });
        return services;
    }

    public static bool IsDevelopmentLike(this IHostEnvironment env) =>
        env.IsDevelopment() || env.IsEnvironment("Testing");
}

public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public HttpCurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid UserId => Guid.TryParse(Principal?.FindFirst(PostalClaims.Subject)?.Value, out var id) ? id : Guid.Empty;

    public string UserName => Principal?.FindFirst(PostalClaims.UserName)?.Value ?? "anonymous";

    public string Role => Principal?.FindFirst(PostalClaims.Role)?.Value ?? string.Empty;

    public IReadOnlyCollection<string> Permissions =>
        Principal?.FindAll(PostalClaims.Permission).Select(c => c.Value).ToArray() ?? Array.Empty<string>();

    public bool HasPermission(string permission) => Permissions.Contains(permission);
}
