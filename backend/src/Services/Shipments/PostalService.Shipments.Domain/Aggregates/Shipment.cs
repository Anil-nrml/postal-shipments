using PostalService.BuildingBlocks.Domain;

namespace PostalService.Shipments.Domain;

/// <summary>
/// Aggregate root. Owns its status history so that "current status" and "history" are always
/// changed together in one transaction (strong consistency inside the aggregate).
/// </summary>
public sealed class Shipment : AggregateRoot<Guid>
{
    private readonly List<ShipmentStatusHistory> _history = new();

    private Shipment() { } // EF Core

    public ShipmentNumber Number { get; private set; } = null!;
    public ShipmentType Type { get; private set; }
    public Weight Weight { get; private set; } = null!;

    /// <summary>Denormalised from <see cref="Weight"/> so the filter can use an index.</summary>
    public WeightCategory WeightCategory { get; private set; }

    public ZipCode OriginZip { get; private set; } = null!;
    public ZipCode DestinationZip { get; private set; } = null!;
    public ZipCode CurrentZip { get; private set; } = null!;
    public ShipmentStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid CreatedByUserId { get; private set; }

    public IReadOnlyCollection<ShipmentStatusHistory> History => _history.AsReadOnly();

    public bool IsLocalDelivery => OriginZip == DestinationZip;

    public bool IsActive => ShipmentStatusRules.IsActive(Status);

    public static Shipment Create(
        ShipmentNumber number,
        ShipmentType type,
        Weight weight,
        ZipCode origin,
        ZipCode destination,
        Guid userId,
        string userName,
        DateTime utcNow)
    {
        if (!Enum.IsDefined(type))
            throw new DomainException("shipment.type-invalid", "Unknown shipment type.");

        var shipment = new Shipment
        {
            Id = Guid.NewGuid(),
            Number = number,
            Type = type,
            Weight = weight,
            WeightCategory = weight.Category,
            OriginZip = origin,
            DestinationZip = destination,
            CurrentZip = origin,
            Status = ShipmentStatus.ReceivedAtOrigin,
            CreatedAtUtc = utcNow,
            UpdatedAtUtc = utcNow,
            CreatedByUserId = userId,
            Version = 1,
        };

        shipment._history.Add(ShipmentStatusHistory.Record(
            shipment.Id, previous: null, ShipmentStatus.ReceivedAtOrigin, origin, userId, userName,
            remarks: "Shipment registered", isCorrection: false, utcNow));

        shipment.Raise(new ShipmentCreated(shipment.Id, number.Value, type, weight.Kilograms, origin.Value,
            destination.Value));
        return shipment;
    }

    /// <summary>Details are editable only before the shipment has been processed at origin.</summary>
    public void UpdateDetails(ShipmentType type, Weight weight, ZipCode destination, DateTime utcNow)
    {
        EnsureEditable();
        if (!Enum.IsDefined(type))
            throw new DomainException("shipment.type-invalid", "Unknown shipment type.");

        Type = type;
        Weight = weight;
        WeightCategory = weight.Category;
        DestinationZip = destination;
        Touch(utcNow);
        Raise(new ShipmentUpdated(Id, Number.Value, type, weight.Kilograms, destination.Value));
    }

    public bool CanBeEdited => Status == ShipmentStatus.ReceivedAtOrigin;

    public void EnsureEditable()
    {
        if (!CanBeEdited)
            throw new DomainException("shipment.not-editable",
                "Only shipments that have not yet been processed at origin can be changed or deleted.");
    }

    /// <summary>Call before removing the aggregate; raises the deletion event for subscribers.</summary>
    public void MarkDeleted(DateTime utcNow)
    {
        EnsureEditable();
        Touch(utcNow);
        Raise(new ShipmentDeleted(Id, Number.Value));
    }

    public void ChangeStatus(ShipmentStatus newStatus, ZipCode atZip, Guid userId, string userName,
        string? remarks, DateTime utcNow)
    {
        if (!Enum.IsDefined(newStatus))
            throw new DomainException("status.invalid", "Unknown status.");

        if (ShipmentStatusRules.IsTerminal(Status))
            throw new DomainException("status.terminal",
                $"Shipment is {Status}; use the authorised correction workflow to change it.");

        if (!ShipmentStatusRules.IsAllowed(Status, newStatus, IsLocalDelivery))
            throw new DomainException("status.transition-invalid",
                $"Cannot move a shipment from {Status} to {newStatus}.");

        ApplyStatus(newStatus, atZip, userId, userName, remarks, isCorrection: false, utcNow);
    }

    /// <summary>
    /// Authorised correction (e.g. a parcel wrongly scanned as Delivered). Bypasses the transition
    /// table but not the location rule, and always requires a reason. Authorisation is enforced
    /// by the caller (permission <c>shipments.correct</c>).
    /// </summary>
    public void CorrectStatus(ShipmentStatus newStatus, ZipCode atZip, Guid userId, string userName,
        string reason, DateTime utcNow)
    {
        if (!Enum.IsDefined(newStatus))
            throw new DomainException("status.invalid", "Unknown status.");
        if (newStatus == Status)
            throw new DomainException("status.unchanged", $"Shipment is already {Status}.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            throw new DomainException("correction.reason-required",
                "A correction requires a reason of at least 10 characters.");

        ApplyStatus(newStatus, atZip, userId, userName, reason.Trim(), isCorrection: true, utcNow);
    }

    private void ApplyStatus(ShipmentStatus newStatus, ZipCode atZip, Guid userId, string userName,
        string? remarks, bool isCorrection, DateTime utcNow)
    {
        EnsureLocation(newStatus, atZip);

        var previous = Status;
        Status = newStatus;
        CurrentZip = atZip;
        Touch(utcNow);

        _history.Add(ShipmentStatusHistory.Record(Id, previous, newStatus, atZip, userId, userName,
            remarks, isCorrection, utcNow));

        Raise(new ShipmentStatusChanged(Id, Number.Value, previous, newStatus, atZip.Value, isCorrection, userId));
    }

    private void EnsureLocation(ShipmentStatus status, ZipCode atZip)
    {
        var required = ShipmentStatusRules.RequiredLocation(status);
        var ok = required switch
        {
            StatusLocation.Origin => atZip == OriginZip,
            StatusLocation.Destination => atZip == DestinationZip,
            _ => true,
        };

        if (!ok)
        {
            var expected = required == StatusLocation.Origin ? OriginZip : DestinationZip;
            throw new DomainException("status.wrong-location",
                $"{status} must be recorded at the {required.ToString().ToLowerInvariant()} post office ({expected}), not {atZip}.");
        }
    }

    private void Touch(DateTime utcNow)
    {
        UpdatedAtUtc = utcNow;
        IncrementVersion();
    }
}

/// <summary>
/// Append-only audit entry. It doubles as the movement log: every entry carries the post office
/// where it was recorded, so a separate ShipmentMovement entity would duplicate data (ADR-0004).
/// </summary>
public sealed class ShipmentStatusHistory
{
    private ShipmentStatusHistory() { } // EF Core

    public long Id { get; private set; }
    public Guid ShipmentId { get; private set; }
    public ShipmentStatus? PreviousStatus { get; private set; }
    public ShipmentStatus Status { get; private set; }
    public ZipCode PostOfficeZip { get; private set; } = null!;
    public Guid PerformedByUserId { get; private set; }
    public string PerformedBy { get; private set; } = string.Empty;
    public string? Remarks { get; private set; }
    public bool IsCorrection { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    internal static ShipmentStatusHistory Record(Guid shipmentId, ShipmentStatus? previous, ShipmentStatus status,
        ZipCode zip, Guid userId, string userName, string? remarks, bool isCorrection, DateTime utcNow) => new()
    {
        ShipmentId = shipmentId,
        PreviousStatus = previous,
        Status = status,
        PostOfficeZip = zip,
        PerformedByUserId = userId,
        PerformedBy = string.IsNullOrWhiteSpace(userName) ? "unknown" : userName,
        Remarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim(),
        IsCorrection = isCorrection,
        OccurredAtUtc = utcNow,
    };
}
