using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace PostalService.BuildingBlocks.Infrastructure.Web;

public static class ServiceDefaults
{
    public const string StandardRateLimit = "standard";

    /// <summary>Logging, tracing, metrics, ProblemDetails, health checks, rate limiting, controllers, Swagger.</summary>
    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        // Structured JSON logs with scopes (CorrelationId, TraceId) — ship to any log store.
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
            o.UseUtcTimestamp = true;
        });

        // OpenTelemetry: traces + metrics. Exported via OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set
        // (Aspire dashboard, Jaeger, Grafana Tempo, Azure Monitor collector...).
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation();
                if (!string.IsNullOrWhiteSpace(otlpEndpoint)) t.AddOtlpExporter();
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (!string.IsNullOrWhiteSpace(otlpEndpoint)) m.AddOtlpExporter();
            });

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            ctx.ProblemDetails.Extensions["correlationId"] = ctx.HttpContext.TraceIdentifier;
            ctx.ProblemDetails.Instance ??= ctx.HttpContext.Request.Path;
        });
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

        builder.Services.AddControllers()
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(StandardRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));
        });

        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" });

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = $"Postal {serviceName}", Version = "v1" });
            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Paste an access token (Development login returns one in the body).",
            });
            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
                }] = Array.Empty<string>(),
            });
        });

        builder.Services.AddTransient<CorrelationIdDelegatingHandler>();
        builder.Services.AddTransient<InternalApiKeyHandler>();
        return builder;
    }

    public static IHealthChecksBuilder AddDatabaseHealthCheck<TContext>(this IHealthChecksBuilder builder)
        where TContext : DbContext =>
        // Activated inside the per-run scope created by the health check service, so the scoped DbContext resolves.
        builder.AddTypeActivatedCheck<DbContextHealthCheck<TContext>>("database", failureStatus: null, tags: new[] { "ready" });

    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        // Trust X-Forwarded-* from the gateway (loopback by default; add KnownProxies/Networks when deployed)
        // so rate limiting and logs see the real client IP.
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                               Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
        });
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseMiddleware<SecurityHeadersMiddleware>();

        if (app.Environment.IsDevelopmentLike())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseMiddleware<CsrfMiddleware>();
        app.UseAuthorization();

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") })
            .AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") })
            .AllowAnonymous();
        app.MapControllers().RequireRateLimiting(StandardRateLimit);
        return app;
    }
}

public sealed class DbContextHealthCheck<TContext> : IHealthCheck where TContext : DbContext
{
    private readonly TContext _db;

    public DbContextHealthCheck(TContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await _db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Cannot connect to the database.");
}
