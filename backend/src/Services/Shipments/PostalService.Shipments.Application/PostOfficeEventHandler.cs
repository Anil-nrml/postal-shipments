using PostalService.BuildingBlocks.Application;
using PostalService.Shipments.Domain;

namespace PostalService.Shipments.Application;

/// <summary>
/// Keeps the local post office replica up to date. Idempotent (inbox) and order-tolerant
/// (stale versions are ignored), so at-least-once delivery is safe.
/// </summary>
public sealed class PostOfficeEventHandler
{
    private readonly IPostOfficeReferenceRepository _references;
    private readonly IInbox _inbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _clock;

    public PostOfficeEventHandler(IPostOfficeReferenceRepository references, IInbox inbox, IUnitOfWork unitOfWork,
        TimeProvider clock)
    {
        _references = references;
        _inbox = inbox;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    /// <returns>False if the event had already been processed.</returns>
    public async Task<bool> HandleAsync(Guid eventId, PostOfficeChangedV1 evt, CancellationToken ct)
    {
        if (await _inbox.HasProcessedAsync(eventId, ct))
            return false;

        var now = _clock.GetUtcNow().UtcDateTime;
        var zip = evt.ZipCode.Trim().ToUpperInvariant();
        var deleted = string.Equals(evt.Kind, "Deleted", StringComparison.OrdinalIgnoreCase);
        var existing = await _references.FindAsync(zip, ct);

        if (existing is null)
            _references.Add(PostOfficeReference.Create(zip, evt.Name, evt.City, evt.CanAcceptShipments && !deleted,
                evt.Version, now));
        else
            existing.Apply(evt.Name, evt.City, evt.CanAcceptShipments && !deleted, deleted, evt.Version, now);

        _inbox.MarkProcessed(eventId, PostOfficeChangedV1.EventType, now);
        await _unitOfWork.SaveChangesAsync(ct); // replica change + inbox row in one transaction
        return true;
    }
}
