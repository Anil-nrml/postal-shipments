using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// The gateway is deliberately thin: routing, rate limiting, CORS, correlation ids and active health
// checks. Authentication and authorisation stay in each service (zero trust, no "trusted" inner network).
var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Postal API Gateway", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Paste an access token issued by the Identity API.",
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
        }] = Array.Empty<string>(),
    });
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("per-client", ctx => RateLimitPartition.GetTokenBucketLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 200,
            TokensPerPeriod = 100,
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            QueueLimit = 0            
        }));
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()
    .WithExposedHeaders("X-Correlation-ID", "X-Trace-ID")));

var otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("api-gateway"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlp)) t.AddOtlpExporter();
    });

builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.Use(async (ctx, next) =>
{
    const string correlationHeader = "X-Correlation-ID";
    const string traceHeader = "X-Trace-ID";
    var incoming = ctx.Request.Headers[correlationHeader].ToString();
    var correlationId = incoming.Length is > 0 and <= 64 &&
                        incoming.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
        ? incoming
        : Guid.NewGuid().ToString("N");
    ctx.Request.Headers[correlationHeader] = correlationId;
    ctx.TraceIdentifier = correlationId;
    var traceId = Activity.Current?.TraceId.ToString() ?? ActivityTraceId.CreateRandom().ToString();

    ctx.Response.OnStarting(() =>
    {
        ctx.Response.Headers[correlationHeader] = correlationId;
        ctx.Response.Headers[traceHeader] = traceId;
        return Task.CompletedTask;
    });
    using (app.Logger.BeginScope(new Dictionary<string, object>
           {
               ["CorrelationId"] = correlationId,
               ["TraceId"] = traceId,
           }))
    {
        var stopwatch = Stopwatch.StartNew();
        app.Logger.LogInformation("Gateway request started {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
        try
        {
            await next();
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Gateway request failed {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            throw;
        }
        finally
        {
            var statusCode = ctx.Response.StatusCode;
            var level = statusCode >= StatusCodes.Status500InternalServerError
                ? LogLevel.Error
                : statusCode >= StatusCodes.Status400BadRequest ? LogLevel.Warning : LogLevel.Information;
            app.Logger.Log(level, "Gateway request completed {Method} {Path} with {StatusCode} in {DurationMs}ms",
                ctx.Request.Method, ctx.Request.Path, statusCode, stopwatch.Elapsed.TotalMilliseconds);
        }
    }
});

app.UseCors();
app.UseRateLimiter();
app.MapHealthChecks("/health/live");
app.MapReverseProxy().RequireRateLimiting("per-client");

app.Run();
