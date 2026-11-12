using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PostalService.BuildingBlocks.Infrastructure.Web;

/// <summary>Accepts or creates X-Correlation-ID, echoes it, and adds it to every log scope.</summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsSafe(incoming) ? incoming : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = correlationId;
        context.Items[HeaderName] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
            await _next(context);
    }

    // Prevent log injection through the header.
    private static bool IsSafe(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>Propagates the caller's correlation id on outgoing service-to-service calls.</summary>
public sealed class CorrelationIdDelegatingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _accessor;

    public CorrelationIdDelegatingHandler(IHttpContextAccessor accessor) => _accessor = accessor;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (_accessor.HttpContext?.Items[CorrelationIdMiddleware.HeaderName] is string id &&
            !request.Headers.Contains(CorrelationIdMiddleware.HeaderName))
            request.Headers.Add(CorrelationIdMiddleware.HeaderName, id);
        return base.SendAsync(request, ct);
    }
}

/// <summary>
/// Double-submit CSRF protection for cookie-authenticated, state-changing requests: the
/// X-CSRF-Token header must equal the (non-HttpOnly) csrf_token cookie. Requests carrying an
/// Authorization header are not cookie-authenticated and are exempt, as are internal calls.
/// </summary>
public sealed class CsrfMiddleware
{
    public const string CookieName = "csrf_token";
    public const string HeaderName = "X-CSRF-Token";
    private static readonly string[] ExemptPrefixes = { "/api/auth/login", "/internal/", "/health" };
    private readonly RequestDelegate _next;

    public CsrfMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresCheck(context) && !TokensMatch(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = 403,
                Title = "CSRF validation failed",
                Detail = $"Send the '{CookieName}' cookie value in the '{HeaderName}' header.",
            });
            return;
        }

        await _next(context);
    }

    private static bool RequiresCheck(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) ||
            HttpMethods.IsOptions(context.Request.Method))
            return false;
        if (context.Request.Headers.Authorization.Count > 0)
            return false;
        var path = context.Request.Path.Value ?? string.Empty;
        if (ExemptPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return false;
        // Only cookie sessions are vulnerable to CSRF.
        return context.Request.Cookies.ContainsKey(AuthCookies.AccessToken) ||
               context.Request.Cookies.ContainsKey(AuthCookies.RefreshToken);
    }

    private static bool TokensMatch(HttpContext context)
    {
        var cookie = context.Request.Cookies[CookieName];
        var header = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(header)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(header));
    }
}

public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        // APIs return JSON only; Swagger UI is served in Development and needs inline assets.
        if (!(context.Request.Path.StartsWithSegments("/swagger")))
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
        return _next(context);
    }
}

public static class AuthCookies
{
    public const string AccessToken = "access_token";
    public const string RefreshToken = "refresh_token";
}

/// <summary>Protects /internal endpoints that only other services may call.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class InternalApiKeyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public const string HeaderName = "X-Internal-Api-Key";

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var expected = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Internal:ApiKey"];
        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        var ok = !string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(provided) &&
                 CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided));
        if (!ok)
            context.Result = new UnauthorizedResult();
        return Task.CompletedTask;
    }
}

/// <summary>Adds the internal API key to service-to-service calls.</summary>
public sealed class InternalApiKeyHandler : DelegatingHandler
{
    private readonly IConfiguration _configuration;

    public InternalApiKeyHandler(IConfiguration configuration) => _configuration = configuration;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Remove(InternalApiKeyAttribute.HeaderName);
        request.Headers.Add(InternalApiKeyAttribute.HeaderName, _configuration["Internal:ApiKey"]);
        return base.SendAsync(request, ct);
    }
}
