using Microsoft.EntityFrameworkCore;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Domain;
using PostalService.BuildingBlocks.Infrastructure.Messaging;
using PostalService.Shipments.Application;
using PostalService.Shipments.Domain;

namespace PostalService.Shipments.Infrastructure.Persistence;

public sealed class ShipmentRepository : IShipmentRepository
{
    private readonly ShipmentsDbContext _db;

    public ShipmentRepository(ShipmentsDbContext db) => _db = db;

    public Task<Shipment?> GetByIdAsync(Guid id, bool includeHistory, CancellationToken ct) =>
        Query(includeHistory).FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<Shipment?> GetByNumberAsync(ShipmentNumber number, bool includeHistory, CancellationToken ct) =>
        Query(includeHistory).FirstOrDefaultAsync(s => s.Number == number, ct);

    public Task<bool> NumberExistsAsync(ShipmentNumber number, CancellationToken ct) =>
        _db.Shipments.AnyAsync(s => s.Number == number, ct);

    public void Add(Shipment shipment) => _db.Shipments.Add(shipment);

    public void Remove(Shipment shipment) => _db.Shipments.Remove(shipment);

    private IQueryable<Shipment> Query(bool includeHistory) =>
        includeHistory ? _db.Shipments.Include(s => s.History) : _db.Shipments;
}

public sealed class PostOfficeReferenceRepository : IPostOfficeReferenceRepository
{
    private readonly ShipmentsDbContext _db;

    public PostOfficeReferenceRepository(ShipmentsDbContext db) => _db = db;

    public Task<PostOfficeReference?> FindAsync(string zip, CancellationToken ct) =>
        _db.PostOfficeReferences.FirstOrDefaultAsync(r => r.ZipCode == zip, ct);

    public void Add(PostOfficeReference reference) => _db.PostOfficeReferences.Add(reference);
}

public sealed class EfInbox : IInbox
{
    private readonly ShipmentsDbContext _db;

    public EfInbox(ShipmentsDbContext db) => _db = db;

    public Task<bool> HasProcessedAsync(Guid eventId, CancellationToken ct) =>
        _db.Set<InboxMessage>().AnyAsync(m => m.EventId == eventId, ct);

    public void MarkProcessed(Guid eventId, string eventType, DateTime utcNow) =>
        _db.Set<InboxMessage>().Add(new InboxMessage { EventId = eventId, EventType = eventType, ProcessedAtUtc = utcNow });
}

public sealed class EfIdempotencyStore : IIdempotencyStore
{
    private readonly ShipmentsDbContext _db;

    public EfIdempotencyStore(ShipmentsDbContext db) => _db = db;

    public async Task<IdempotencyRecord?> FindAsync(string key, Guid userId, CancellationToken ct)
    {
        var entry = await _db.IdempotencyKeys.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == key && e.UserId == userId, ct);
        return entry is null
            ? null
            : new IdempotencyRecord(entry.Key, entry.UserId, entry.RequestHash, entry.ShipmentId, entry.CreatedAtUtc);
    }

    public void Add(IdempotencyRecord record) => _db.IdempotencyKeys.Add(new IdempotencyEntry
    {
        Key = record.Key,
        UserId = record.UserId,
        RequestHash = record.RequestHash,
        ShipmentId = record.ShipmentId,
        CreatedAtUtc = record.CreatedAtUtc,
    });
}

/// <summary>Read side. Offset paging over indexed columns; projections without change tracking.</summary>
public sealed class ShipmentQueries : IShipmentQueries
{
    private readonly ShipmentsDbContext _db;

    public ShipmentQueries(ShipmentsDbContext db) => _db = db;

    public async Task<PagedResult<ShipmentDto>> SearchAsync(ShipmentSearchCriteria c, CancellationToken ct)
    {
        var (pageNumber, pageSize) = Paging.Normalize(c.PageNumber, c.PageSize);
        IQueryable<Shipment> query;
        if (!string.IsNullOrWhiteSpace(c.ShipmentNumber))
        {
            // EF cannot translate member access on a value-converted property, so the prefix search starts
            // from parameterised SQL and the remaining filters compose on top. The validator restricts the
            // input to [A-Z0-9-], so no LIKE wildcards can be injected; the value is a parameter either way.
            var pattern = c.ShipmentNumber.Trim().ToUpperInvariant() + "%";
            query = _db.Shipments.FromSqlInterpolated($"SELECT * FROM Shipments WHERE ShipmentNumber LIKE {pattern}");
        }
        else
        {
            query = _db.Shipments;
        }

        query = query.AsNoTracking();

        if (c.Type.HasValue) query = query.Where(s => s.Type == c.Type);
        if (c.Status.HasValue) query = query.Where(s => s.Status == c.Status);
        if (c.WeightCategory.HasValue) query = query.Where(s => s.WeightCategory == c.WeightCategory);
        if (Zip(c.OriginPostOffice) is { } origin) query = query.Where(s => s.OriginZip == origin);
        if (Zip(c.DestinationPostOffice) is { } destination) query = query.Where(s => s.DestinationZip == destination);
        if (Zip(c.CurrentPostOffice) is { } current) query = query.Where(s => s.CurrentZip == current);
        if (c.CreatedFrom.HasValue) query = query.Where(s => s.CreatedAtUtc >= c.CreatedFrom.Value.ToUniversalTime());
        if (c.CreatedTo.HasValue) query = query.Where(s => s.CreatedAtUtc <= c.CreatedTo.Value.ToUniversalTime());

        var total = await query.CountAsync(ct);

        var descending = !string.Equals(c.SortDirection, "asc", StringComparison.OrdinalIgnoreCase);
        IOrderedQueryable<Shipment> ordered = (c.SortBy?.ToLowerInvariant(), descending) switch
        {
            ("updatedat", true) => query.OrderByDescending(s => s.UpdatedAtUtc),
            ("updatedat", false) => query.OrderBy(s => s.UpdatedAtUtc),
            ("shipmentnumber", true) => query.OrderByDescending(s => s.Number),
            ("shipmentnumber", false) => query.OrderBy(s => s.Number),
            ("weight", true) => query.OrderByDescending(s => s.Weight),
            ("weight", false) => query.OrderBy(s => s.Weight),
            ("status", true) => query.OrderByDescending(s => s.Status),
            ("status", false) => query.OrderBy(s => s.Status),
            (_, false) => query.OrderBy(s => s.CreatedAtUtc),
            _ => query.OrderByDescending(s => s.CreatedAtUtc),
        };

        var page = await ordered
            .ThenBy(s => s.Id) // deterministic paging
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var names = await GetPostOfficeSummariesAsync(
            page.SelectMany(s => new[] { s.OriginZip.Value, s.DestinationZip.Value, s.CurrentZip.Value }), ct);

        return new PagedResult<ShipmentDto>(page.Select(s => ShipmentMapper.ToDto(s, names)).ToList(), total,
            pageNumber, pageSize);
    }

    public async Task<ShipmentStatistics> GetStatisticsAsync(CancellationToken ct)
    {
        var byStatus = await _db.Shipments.AsNoTracking()
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var byWeight = await _db.Shipments.AsNoTracking()
            .GroupBy(s => s.WeightCategory)
            .Select(g => new { Category = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Count(ShipmentStatus status) => byStatus.FirstOrDefault(x => x.Status == status)?.Count ?? 0;

        return new ShipmentStatistics(
            Total: byStatus.Sum(x => x.Count),
            InTransit: Count(ShipmentStatus.InTransit),
            Delivered: Count(ShipmentStatus.Delivered),
            FailedDeliveries: Count(ShipmentStatus.FailedDeliveryAttempt),
            Lost: Count(ShipmentStatus.Lost),
            ByStatus: Enum.GetValues<ShipmentStatus>().ToDictionary(s => s.ToString(), Count),
            ByWeightCategory: Enum.GetValues<WeightCategory>().ToDictionary(w => w.ToString(),
                w => byWeight.FirstOrDefault(x => x.Category == w)?.Count ?? 0));
    }

    public async Task<PostOfficeDependencies> GetDependenciesAsync(ZipCode zip, CancellationToken ct)
    {
        var related = _db.Shipments.AsNoTracking()
            .Where(s => s.OriginZip == zip || s.DestinationZip == zip || s.CurrentZip == zip);
        var total = await related.CountAsync(ct);
        var active = await related.CountAsync(s => s.Status != ShipmentStatus.Delivered && s.Status != ShipmentStatus.Lost, ct);
        return new PostOfficeDependencies(zip.Value, active, total);
    }

    public async Task<IReadOnlyDictionary<string, PostOfficeSummary>> GetPostOfficeSummariesAsync(
        IEnumerable<string> zips, CancellationToken ct)
    {
        var keys = zips.Distinct().ToList();
        if (keys.Count == 0) return new Dictionary<string, PostOfficeSummary>();
        return await _db.PostOfficeReferences.AsNoTracking()
            .Where(r => keys.Contains(r.ZipCode))
            .ToDictionaryAsync(r => r.ZipCode, r => new PostOfficeSummary(r.ZipCode, r.Name, r.City), ct);
    }

    private static ZipCode? Zip(string? value) => string.IsNullOrWhiteSpace(value) ? null : ZipCode.Create(value);
}
