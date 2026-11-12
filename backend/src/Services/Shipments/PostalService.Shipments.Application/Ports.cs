using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Domain;
using PostalService.Shipments.Domain;

namespace PostalService.Shipments.Application;

/// <summary>Write-side repository: loads and stores whole aggregates.</summary>
public interface IShipmentRepository
{
    Task<Shipment?> GetByIdAsync(Guid id, bool includeHistory, CancellationToken ct);
    Task<Shipment?> GetByNumberAsync(ShipmentNumber number, bool includeHistory, CancellationToken ct);
    Task<bool> NumberExistsAsync(ShipmentNumber number, CancellationToken ct);
    void Add(Shipment shipment);
    void Remove(Shipment shipment);
}

/// <summary>Read side: projections straight to DTOs, no change tracking.</summary>
public interface IShipmentQueries
{
    Task<PagedResult<ShipmentDto>> SearchAsync(ShipmentSearchCriteria criteria, CancellationToken ct);
    Task<ShipmentStatistics> GetStatisticsAsync(CancellationToken ct);
    Task<PostOfficeDependencies> GetDependenciesAsync(ZipCode zip, CancellationToken ct);
    Task<IReadOnlyDictionary<string, PostOfficeSummary>> GetPostOfficeSummariesAsync(IEnumerable<string> zips,
        CancellationToken ct);
}

/// <summary>
/// Synchronous, authoritative lookup in the PostOffices service. Throws
/// <see cref="ServiceUnavailableException"/> when the owner cannot be reached: we refuse the write
/// rather than accept a possibly inactive post office (CP choice).
/// </summary>
public interface IPostOfficeDirectory
{
    Task<PostOfficeInfo?> FindAsync(ZipCode zip, CancellationToken ct);
}

public interface IPostOfficeReferenceRepository
{
    Task<PostOfficeReference?> FindAsync(string zip, CancellationToken ct);
    void Add(PostOfficeReference reference);
}

public interface IInbox
{
    Task<bool> HasProcessedAsync(Guid eventId, CancellationToken ct);
    void MarkProcessed(Guid eventId, string eventType, DateTime utcNow);
}

public sealed record IdempotencyRecord(string Key, Guid UserId, string RequestHash, Guid ShipmentId, DateTime CreatedAtUtc);

public interface IIdempotencyStore
{
    Task<IdempotencyRecord?> FindAsync(string key, Guid userId, CancellationToken ct);
    void Add(IdempotencyRecord record);
}
