using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Domain;
using PostalService.Shipments.Domain;

namespace PostalService.Shipments.Application;

/// <summary>
/// Shipment use cases. Thin orchestration: validate input, load the aggregate, let the domain decide,
/// commit one transaction. Business rules live in <see cref="Shipment"/> and <see cref="ShipmentStatusRules"/>.
/// </summary>
public sealed class ShipmentService
{
    private const int MaxNumberGenerationAttempts = 5;

    private readonly IShipmentRepository _shipments;
    private readonly IShipmentQueries _queries;
    private readonly IPostOfficeDirectory _postOffices;
    private readonly IIdempotencyStore _idempotency;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _user;
    private readonly TimeProvider _clock;
    private readonly IValidator<CreateShipmentRequest> _createValidator;
    private readonly IValidator<UpdateShipmentRequest> _updateValidator;
    private readonly IValidator<ChangeStatusRequest> _statusValidator;
    private readonly IValidator<CorrectStatusRequest> _correctionValidator;
    private readonly IValidator<ShipmentSearchCriteria> _searchValidator;

    public ShipmentService(
        IShipmentRepository shipments,
        IShipmentQueries queries,
        IPostOfficeDirectory postOffices,
        IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork,
        ICurrentUser user,
        TimeProvider clock,
        IValidator<CreateShipmentRequest> createValidator,
        IValidator<UpdateShipmentRequest> updateValidator,
        IValidator<ChangeStatusRequest> statusValidator,
        IValidator<CorrectStatusRequest> correctionValidator,
        IValidator<ShipmentSearchCriteria> searchValidator)
    {
        _shipments = shipments;
        _queries = queries;
        _postOffices = postOffices;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _user = user;
        _clock = clock;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _statusValidator = statusValidator;
        _correctionValidator = correctionValidator;
        _searchValidator = searchValidator;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <returns>The shipment and whether it was newly created (false = idempotent replay).</returns>
    public async Task<(ShipmentDto Shipment, bool Created)> CreateAsync(CreateShipmentRequest request,
        string? idempotencyKey, CancellationToken ct)
    {
        await _createValidator.ValidateAndThrowAsync(request, ct);

        var requestHash = Hash(request);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var previous = await _idempotency.FindAsync(idempotencyKey, _user.UserId, ct);
            if (previous is not null)
            {
                if (previous.RequestHash != requestHash)
                    throw new ConflictException("idempotency.key-reused",
                        "This Idempotency-Key was already used with a different request body.");
                return (await GetAsync(previous.ShipmentId, ct), false);
            }
        }

        var origin = ZipCode.Create(request.OriginZip);
        var destination = ZipCode.Create(request.DestinationZip);

        // Strongly consistent check against the owning service (CP): never ship from/to an inactive office.
        await EnsureCanAcceptShipmentsAsync(origin, ct);
        if (destination != origin)
            await EnsureCanAcceptShipmentsAsync(destination, ct);

        var now = UtcNow;
        var number = await GenerateUniqueNumberAsync(now, ct);
        var shipment = Shipment.Create(number, request.Type, Weight.FromKilograms(request.WeightKg), origin,
            destination, _user.UserId, _user.UserName, now);

        _shipments.Add(shipment);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            _idempotency.Add(new IdempotencyRecord(idempotencyKey, _user.UserId, requestHash, shipment.Id, now));

        // The unique indexes on ShipmentNumber and (Key, UserId) are the final guard against races.
        await _unitOfWork.SaveChangesAsync(ct);
        return (await ToDtoAsync(shipment, ct), true);
    }

    public async Task<ShipmentDto> GetAsync(Guid id, CancellationToken ct)
    {
        var shipment = await LoadAsync(id, includeHistory: false, ct);
        return await ToDtoAsync(shipment, ct);
    }

    public async Task<ShipmentDto> UpdateAsync(Guid id, UpdateShipmentRequest request, CancellationToken ct)
    {
        await _updateValidator.ValidateAndThrowAsync(request, ct);
        var shipment = await LoadAsync(id, includeHistory: false, ct);
        EnsureVersion(shipment, request.Version);
        shipment.EnsureEditable();

        var destination = ZipCode.Create(request.DestinationZip);
        if (destination != shipment.DestinationZip && destination != shipment.OriginZip)
            await EnsureCanAcceptShipmentsAsync(destination, ct);

        shipment.UpdateDetails(request.Type, Weight.FromKilograms(request.WeightKg), destination, UtcNow);
        await _unitOfWork.SaveChangesAsync(ct);
        return await ToDtoAsync(shipment, ct);
    }

    public async Task DeleteAsync(Guid id, int? expectedVersion, CancellationToken ct)
    {
        var shipment = await LoadAsync(id, includeHistory: true, ct);
        if (expectedVersion.HasValue) EnsureVersion(shipment, expectedVersion.Value);
        shipment.MarkDeleted(UtcNow);
        _shipments.Remove(shipment);
        await _unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<ShipmentDto> ChangeStatusAsync(Guid id, ChangeStatusRequest request, CancellationToken ct)
    {
        await _statusValidator.ValidateAndThrowAsync(request, ct);
        var shipment = await LoadAsync(id, includeHistory: false, ct);
        EnsureVersion(shipment, request.Version);

        shipment.ChangeStatus(request.Status, ZipCode.Create(request.PostOfficeZip), _user.UserId, _user.UserName,
            request.Remarks, UtcNow);

        // Current status + new history row commit together; the Version concurrency token rejects
        // a second clerk who scanned the same parcel at the same moment (409).
        await _unitOfWork.SaveChangesAsync(ct);
        return await ToDtoAsync(shipment, ct);
    }

    public async Task<ShipmentDto> CorrectStatusAsync(Guid id, CorrectStatusRequest request, CancellationToken ct)
    {
        await _correctionValidator.ValidateAndThrowAsync(request, ct);
        if (!_user.HasPermission(Permissions.ShipmentsCorrectStatus))
            throw new UnauthorizedAccessException();

        var shipment = await LoadAsync(id, includeHistory: false, ct);
        EnsureVersion(shipment, request.Version);
        shipment.CorrectStatus(request.Status, ZipCode.Create(request.PostOfficeZip), _user.UserId, _user.UserName,
            request.Reason, UtcNow);
        await _unitOfWork.SaveChangesAsync(ct);
        return await ToDtoAsync(shipment, ct);
    }

    public async Task<IReadOnlyList<ShipmentHistoryDto>> GetHistoryAsync(Guid id, CancellationToken ct)
    {
        var shipment = await LoadAsync(id, includeHistory: true, ct);
        return await ToHistoryAsync(shipment, ct);
    }

    public async Task<TrackingDto> TrackAsync(string shipmentNumber, CancellationToken ct)
    {
        var number = ShipmentNumber.Create(shipmentNumber);
        var shipment = await _shipments.GetByNumberAsync(number, includeHistory: true, ct)
                       ?? throw new NotFoundException("Shipment", number.Value);
        var names = await NamesAsync(shipment, ct);
        var history = await ToHistoryAsync(shipment, ct);
        return new TrackingDto(shipment.Id, shipment.Number.Value, shipment.Type, shipment.Status,
            Summary(shipment.OriginZip, names), Summary(shipment.DestinationZip, names),
            Summary(shipment.CurrentZip, names), shipment.UpdatedAtUtc, history);
    }

    public async Task<PagedResult<ShipmentDto>> SearchAsync(ShipmentSearchCriteria criteria, CancellationToken ct)
    {
        await _searchValidator.ValidateAndThrowAsync(criteria, ct);
        return await _queries.SearchAsync(criteria, ct);
    }

    public Task<ShipmentStatistics> GetStatisticsAsync(CancellationToken ct) => _queries.GetStatisticsAsync(ct);

    public Task<PostOfficeDependencies> GetDependenciesAsync(string zip, CancellationToken ct) =>
        _queries.GetDependenciesAsync(ZipCode.Create(zip), ct);

    // ---------- helpers ----------

    private async Task<Shipment> LoadAsync(Guid id, bool includeHistory, CancellationToken ct) =>
        await _shipments.GetByIdAsync(id, includeHistory, ct) ?? throw new NotFoundException("Shipment", id);

    private static void EnsureVersion(Shipment shipment, int expectedVersion)
    {
        if (shipment.Version != expectedVersion)
            throw new ConflictException("concurrency",
                $"Shipment {shipment.Number} was changed by someone else (version {shipment.Version}, you had {expectedVersion}). Reload and retry.");
    }

    private async Task EnsureCanAcceptShipmentsAsync(ZipCode zip, CancellationToken ct)
    {
        var office = await _postOffices.FindAsync(zip, ct)
                     ?? throw new DomainException("postoffice.unknown", $"Post office {zip} does not exist.");
        if (!office.CanAcceptShipments)
            throw new DomainException("postoffice.inactive", $"Post office {zip} is not accepting shipments.");
    }

    private async Task<ShipmentNumber> GenerateUniqueNumberAsync(DateTime now, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxNumberGenerationAttempts; attempt++)
        {
            var candidate = ShipmentNumber.Generate(now);
            if (!await _shipments.NumberExistsAsync(candidate, ct))
                return candidate;
        }

        throw new ConflictException("shipment-number.exhausted", "Could not allocate a unique shipment number.");
    }

    private async Task<IReadOnlyDictionary<string, PostOfficeSummary>> NamesAsync(Shipment s, CancellationToken ct) =>
        await _queries.GetPostOfficeSummariesAsync(
            new[] { s.OriginZip.Value, s.DestinationZip.Value, s.CurrentZip.Value }
                .Concat(s.History.Select(h => h.PostOfficeZip.Value)).Distinct(), ct);

    private async Task<ShipmentDto> ToDtoAsync(Shipment shipment, CancellationToken ct) =>
        ShipmentMapper.ToDto(shipment, await NamesAsync(shipment, ct));

    private async Task<IReadOnlyList<ShipmentHistoryDto>> ToHistoryAsync(Shipment shipment, CancellationToken ct)
    {
        var names = await NamesAsync(shipment, ct);
        return shipment.History
            .OrderBy(h => h.OccurredAtUtc).ThenBy(h => h.Id)
            .Select(h => new ShipmentHistoryDto(h.Id, h.PreviousStatus, h.Status, Summary(h.PostOfficeZip, names),
                h.PerformedBy, h.Remarks, h.IsCorrection, h.OccurredAtUtc))
            .ToList();
    }

    private static PostOfficeSummary Summary(ZipCode zip, IReadOnlyDictionary<string, PostOfficeSummary> names) =>
        ShipmentMapper.Summary(zip.Value, names);

    private static string Hash(CreateShipmentRequest request)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            request.Type,
            request.WeightKg,
            Origin = request.OriginZip.Trim().ToUpperInvariant(),
            Destination = request.DestinationZip.Trim().ToUpperInvariant(),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public static class ShipmentMapper
{
    public static ShipmentDto ToDto(Shipment s, IReadOnlyDictionary<string, PostOfficeSummary> names) => new(
        s.Id,
        s.Number.Value,
        s.Type,
        s.Weight.Kilograms,
        s.WeightCategory,
        s.Status,
        Summary(s.OriginZip.Value, names),
        Summary(s.DestinationZip.Value, names),
        Summary(s.CurrentZip.Value, names),
        s.IsLocalDelivery,
        s.CanBeEdited,
        ShipmentStatusRules.IsTerminal(s.Status)
            ? Array.Empty<ShipmentStatus>()
            : ShipmentStatusRules.NextStatuses(s.Status, s.IsLocalDelivery),
        s.CreatedAtUtc,
        s.UpdatedAtUtc,
        s.Version);

    public static PostOfficeSummary Summary(string zip, IReadOnlyDictionary<string, PostOfficeSummary> names) =>
        names.TryGetValue(zip, out var summary) ? summary : new PostOfficeSummary(zip, null, null);
}
