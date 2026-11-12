namespace PostalService.BuildingBlocks.Application;

/// <summary>
/// Commits all changes tracked in one business transaction. Implemented directly by each
/// service's DbContext (which already is a unit of work) — see ADR-0006.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>The authenticated caller, resolved from the JWT.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid UserId { get; }
    string UserName { get; }
    string Role { get; }
    IReadOnlyCollection<string> Permissions { get; }
    bool HasPermission(string permission);
}

public class NotFoundException : Exception
{
    public NotFoundException(string resource, object key)
        : base($"{resource} '{key}' was not found.") { }
}

/// <summary>State conflict: duplicate key, stale version, dependent data. Mapped to HTTP 409.</summary>
public class ConflictException : Exception
{
    public ConflictException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>A dependency needed for a strongly consistent decision is unreachable. Mapped to HTTP 503.</summary>
public class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalRecords, int PageNumber, int PageSize)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalRecords / (double)PageSize);
    public bool HasNextPage => PageNumber < TotalPages;
    public bool HasPreviousPage => PageNumber > 1;
}

public static class Paging
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public static (int PageNumber, int PageSize) Normalize(int? pageNumber, int? pageSize)
    {
        var number = pageNumber is null or < 1 ? 1 : pageNumber.Value;
        var size = pageSize is null or < 1 ? DefaultPageSize : Math.Min(pageSize.Value, MaxPageSize);
        return (number, size);
    }
}
