using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using PostalService.BuildingBlocks.Infrastructure.Persistence;
using PostalService.BuildingBlocks.Infrastructure.Web;
using PostalService.Identity.Api;
using PostalService.Identity.Application;
using PostalService.Identity.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("identity-api");
builder.Services.AddIdentityApplication();
builder.Services.AddIdentityInfrastructure(builder.Configuration);
builder.Services.Configure<AuthCookieOptions>(builder.Configuration.GetSection(AuthCookieOptions.SectionName));
builder.Services.AddHealthChecks().AddDatabaseHealthCheck<IdentityDbContext>();

// Brute-force protection on login, in addition to per-account lockout.
builder.Services.Configure<RateLimiterOptions>(o => o.AddPolicy(AuthController.LoginRateLimit, ctx =>
    RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) })));

var app = builder.Build();

app.UseServiceDefaults();
await app.InitializeDatabaseAsync<IdentityDbContext>(IdentitySeeder.SeedAsync);
await app.RunAsync();

public partial class Program;
