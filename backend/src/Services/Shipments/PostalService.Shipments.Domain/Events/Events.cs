using PostalService.BuildingBlocks.Domain;

namespace PostalService.Shipments.Domain;

public sealed record ShipmentCreated(Guid ShipmentId, string ShipmentNumber, ShipmentType Type, decimal WeightKg,
    string OriginZip, string DestinationZip) : DomainEvent;

public sealed record ShipmentUpdated(Guid ShipmentId, string ShipmentNumber, ShipmentType Type, decimal WeightKg,
    string DestinationZip) : DomainEvent;

public sealed record ShipmentDeleted(Guid ShipmentId, string ShipmentNumber) : DomainEvent;

public sealed record ShipmentStatusChanged(Guid ShipmentId, string ShipmentNumber, ShipmentStatus PreviousStatus,
    ShipmentStatus NewStatus, string PostOfficeZip, bool IsCorrection, Guid PerformedByUserId) : DomainEvent;
