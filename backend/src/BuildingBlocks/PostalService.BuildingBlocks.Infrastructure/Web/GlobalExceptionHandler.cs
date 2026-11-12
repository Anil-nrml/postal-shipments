using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Domain;

namespace PostalService.BuildingBlocks.Infrastructure.Web;

/// <summary>Maps exceptions to RFC 7807 ProblemDetails. Internal details never leak to clients.</summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IProblemDetailsService _problemDetails;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IProblemDetailsService problemDetails)
    {
        _logger = logger;
        _problemDetails = problemDetails;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        var problem = Map(exception);

        if (problem.Status >= 500)
            _logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method,
                httpContext.Request.Path);
        else
            _logger.LogInformation("Request failed with {Status} ({Type}): {Message}", problem.Status,
                exception.GetType().Name, exception.Message);

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        if (problem.Status == StatusCodes.Status503ServiceUnavailable)
            httpContext.Response.Headers.RetryAfter = "5";

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    internal static ProblemDetails Map(Exception exception) => exception switch
    {
        ValidationException v => new ValidationProblemDetails(
            v.Errors.GroupBy(e => ToCamel(e.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()))
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        },
        DomainException d => Problem(StatusCodes.Status422UnprocessableEntity, "Business rule violated", d.Message, d.Code),
        NotFoundException n => Problem(StatusCodes.Status404NotFound, "Resource not found", n.Message, "not-found"),
        ConflictException c => Problem(StatusCodes.Status409Conflict, "Conflict", c.Message, c.Code),
        DbUpdateConcurrencyException => Problem(StatusCodes.Status409Conflict, "Concurrent update",
            "The resource was modified by someone else. Reload it and try again.", "concurrency"),
        DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 19 } } =>
            Problem(StatusCodes.Status409Conflict, "Conflict", "A record with the same unique value already exists.",
                "duplicate"),
        ServiceUnavailableException s => Problem(StatusCodes.Status503ServiceUnavailable, "Dependency unavailable",
            s.Message, "dependency-unavailable"),
        BrokenCircuitException or TimeoutRejectedException or HttpRequestException =>
            Problem(StatusCodes.Status503ServiceUnavailable, "Dependency unavailable",
                "A required service is temporarily unavailable. Please retry shortly.", "dependency-unavailable"),
        UnauthorizedAccessException => Problem(StatusCodes.Status403Forbidden, "Forbidden", "You do not have permission.",
            "forbidden"),
        BadHttpRequestException b => Problem(b.StatusCode, "Bad request", b.Message, "bad-request"),
        _ => Problem(StatusCodes.Status500InternalServerError, "Unexpected error",
            "An unexpected error occurred. Quote the correlation id when reporting it.", "internal"),
    };

    private static ProblemDetails Problem(int status, string title, string detail, string code)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["code"] = code;
        return problem;
    }

    private static string ToCamel(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
