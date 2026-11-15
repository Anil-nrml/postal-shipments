using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Infrastructure.Persistence;
using PostalService.BuildingBlocks.Infrastructure.Web;
using PostalService.Identity.Application;
using PostalService.Identity.Domain;
using IPasswordHasher = PostalService.Identity.Application.IPasswordHasher;

namespace PostalService.Identity.Infrastructure;

public sealed class TokenOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "postal-identity";
    public string Audience { get; set; } = "postal-api";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>PEM file holding the RSA private key. Created on first start if missing (development).</summary>
    public string SigningKeyPath { get; set; } = "App_Data/keys/signing-key.pem";
}

/// <summary>
/// Holds the RSA signing key. Only the Identity service has the private key; other services fetch the
/// public key from /.well-known/jwks.json. In production load the key from Key Vault / an HSM instead.
/// </summary>
public sealed class SigningKeyProvider
{
    public SigningKeyProvider(IOptions<TokenOptions> options, IHostEnvironment env, ILogger<SigningKeyProvider> logger)
    {
        var path = Path.GetFullPath(options.Value.SigningKeyPath, env.ContentRootPath);
        var rsa = RSA.Create();
        if (File.Exists(path))
        {
            rsa.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            if (env.IsProduction())
                throw new InvalidOperationException($"Signing key not found at {path}. Provision it from your secret store.");
            rsa = RSA.Create(2048);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
            logger.LogWarning("Generated a new development signing key at {Path}", path);
        }

        var publicParameters = rsa.ExportParameters(includePrivateParameters: false);
        KeyId = Base64UrlEncoder.Encode(SHA256.HashData(publicParameters.Modulus!))[..16];
        SigningKey = new RsaSecurityKey(rsa) { KeyId = KeyId };
        PublicKey = new RsaSecurityKey(publicParameters) { KeyId = KeyId };
        PublicParameters = publicParameters;
    }

    public string KeyId { get; }
    public RsaSecurityKey SigningKey { get; }
    public RsaSecurityKey PublicKey { get; }
    public RSAParameters PublicParameters { get; }

    /// <summary>JWKS document containing only the public key.</summary>
    public object Jwks => new
    {
        keys = new[]
        {
            new
            {
                kty = "RSA",
                use = "sig",
                alg = SecurityAlgorithms.RsaSha256,
                kid = KeyId,
                n = Base64UrlEncoder.Encode(PublicParameters.Modulus),
                e = Base64UrlEncoder.Encode(PublicParameters.Exponent),
            },
        },
    };
}

public sealed class JwtTokenService : ITokenService
{
    private readonly TokenOptions _options;
    private readonly SigningKeyProvider _keys;
    private readonly TimeProvider _clock;
    private readonly JsonWebTokenHandler _handler = new();

    public JwtTokenService(IOptions<TokenOptions> options, SigningKeyProvider keys, TimeProvider clock)
    {
        _options = options.Value;
        _keys = keys;
        _clock = clock;
    }

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_options.RefreshTokenDays);

    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(User user, IReadOnlyList<string> permissions)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(PostalClaims.Subject, user.Id.ToString()),
            new(PostalClaims.UserName, user.UserName),
            new(PostalClaims.Role, user.Role),
            new("display_name", user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(permissions.Select(p => new Claim(PostalClaims.Permission, p)));

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(_keys.SigningKey, SecurityAlgorithms.RsaSha256),
        });
        return (token, expires);
    }

    public (string RawToken, string Hash) CreateRefreshToken()
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
        return (raw, HashRefreshToken(raw));
    }

    public string HashRefreshToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}

/// <summary>ASP.NET Core Identity's hasher: PBKDF2-HMAC-SHA512, 100k iterations, per-user salt, versioned format.</summary>
public sealed class AspNetPasswordHasher : IPasswordHasher
{
    private static readonly User Placeholder = User.Create("placeholder", "placeholder", Roles.ShipmentViewer, "x", DateTime.UnixEpoch);
    private readonly PasswordHasher<User> _inner = new();

    public string Hash(string password) => _inner.HashPassword(Placeholder, password);

    public PasswordCheck Verify(string hash, string password) => _inner.VerifyHashedPassword(Placeholder, hash, password) switch
    {
        PasswordVerificationResult.Success => PasswordCheck.Success,
        PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
        _ => PasswordCheck.Failed,
    };
}

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSqliteDbContext<IdentityDbContext>(
            configuration.GetConnectionString("Identity") ?? "Data Source=App_Data/identity.db");
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<IdentityDbContext>());
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.Configure<TokenOptions>(configuration.GetSection(TokenOptions.SectionName));
        services.AddSingleton<SigningKeyProvider>();
        services.AddSingleton<ITokenService, JwtTokenService>();

        // Identity validates its own tokens with the in-process public key (no self HTTP call).
        services.AddPostalAuthentication(configuration);
        services.AddOptions<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
                Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
            .Configure<SigningKeyProvider>((o, keys) =>
            {
                o.MetadataAddress = string.Empty;
                o.TokenValidationParameters.IssuerSigningKey = keys.PublicKey;
            });
        return services;
    }
}
