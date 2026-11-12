using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Domain;
using PostalService.BuildingBlocks.Infrastructure.Messaging;
using PostalService.Shipments.Application;

namespace PostalService.Shipments.IntegrationTests;

/// <summary>
/// Hosts the real Shipments API with a throw-away SQLite file per factory instance. The only
/// replaced pieces are external: authentication (test scheme) and the PostOffices service (fake).
/// </summary>
public sealed class ShipmentsApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"shipments-it-{Guid.NewGuid():N}.db");

    public FakePostOfficeDirectory PostOffices { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shipments"] = $"Data Source={_dbPath}",
            ["Outbox:Enabled"] = "false",
            ["Internal:ApiKey"] = "test-internal-key",
            ["Jwt:MetadataAddress"] = "http://localhost/unused",
        }));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPostOfficeDirectory>();
            services.AddSingleton<IPostOfficeDirectory>(PostOffices);

            services.AddAuthentication(TestAuthHandler.Scheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = TestAuthHandler.Scheme;
                o.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                o.DefaultChallengeScheme = TestAuthHandler.Scheme;
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(file); } catch (IOException) { /* best effort */ }
    }

    public HttpClient ClientWith(params string[] permissions)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, string.Join(',', permissions));
        return client;
    }

    public HttpClient Operator() => ClientWith(Permissions.ShipmentsRead, Permissions.ShipmentsWrite,
        Permissions.ShipmentsUpdateStatus, Permissions.ShipmentsDelete);

    public HttpClient Admin() => ClientWith(Permissions.All.ToArray());
}

/// <summary>Authenticates every request that carries the X-Test-Permissions header.</summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string Scheme = "Test";
    public const string PermissionsHeader = "X-Test-Permissions";

    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(PermissionsHeader, out var header))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>
        {
            new(PostalClaims.Subject, "22222222-2222-2222-2222-222222222222"),
            new(PostalClaims.UserName, "test-user"),
            new(PostalClaims.Role, "Test"),
        };
        claims.AddRange(header.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => new Claim(PostalClaims.Permission, p.Trim())));

        var identity = new ClaimsIdentity(claims, Scheme, PostalClaims.UserName, PostalClaims.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}

public sealed class FakePostOfficeDirectory : IPostOfficeDirectory
{
    private readonly Dictionary<string, PostOfficeInfo> _offices = new()
    {
        ["500001"] = new("500001", "Hyderabad GPO", "Hyderabad", true, 1),
        ["560001"] = new("560001", "Bengaluru GPO", "Bengaluru", true, 1),
        ["110001"] = new("110001", "New Delhi GPO", "New Delhi", false, 2),
    };

    public bool Unavailable { get; set; }

    public Task<PostOfficeInfo?> FindAsync(ZipCode zip, CancellationToken ct)
    {
        if (Unavailable) throw new ServiceUnavailableException("PostOffices is down (test).");
        return Task.FromResult(_offices.GetValueOrDefault(zip.Value));
    }
}
