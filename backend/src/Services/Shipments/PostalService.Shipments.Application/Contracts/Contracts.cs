using PostalService.Shipments.Domain;

namespace PostalService.Shipments.Application;

// ---------- Requests ----------

public sealed record CreateShipmentRequest(ShipmentType Type, decimal WeightKg, string OriginZip, string DestinationZip);

public sealed record UpdateShipmentRequest(ShipmentType Type, decimal WeightKg, string DestinationZip, int Version);

public sealed record ChangeStatusRequest(ShipmentStatus Status, string PostOfficeZip, string? Remarks, int Version);

public sealed record CorrectStatusRequest(ShipmentStatus Status, string PostOfficeZip, string Reason, int Version);

/// <summary>Filters for GET /api/shipments. All filters combine with AND.</summary>
public sealed record ShipmentSearchCriteria
{
    public int? PageNumber { get; init; }
    public int? PageSize { get; init; }

    /// <summary>createdAt | updatedAt | shipmentNumber | weight | status</summary>
    public string? SortBy { get; init; }

    /// <summary>asc | desc</summary>
    public string? SortDirection { get; init; }

    /// <summary>Prefix match on the shipment number (case-insensitive).</summary>
    public string? ShipmentNumber { get; init; }

    public ShipmentType? Type { get; init; }
    public ShipmentStatus? Status { get; init; }
    public string? OriginPostOffice { get; init; }
    public string? DestinationPostOffice { get; init; }
    public string? CurrentPostOffice { get; init; }
    public WeightCategory? WeightCategory { get; init; }
    public DateTime? CreatedFrom { get; init; }
    public DateTime? CreatedTo { get; init; }
}

// ---------- Responses ----------

public sealed record PostOfficeSummary(string ZipCode, string? Name, string? City);

public sealed record ShipmentDto(
    Guid Id,
    string ShipmentNumber,
    ShipmentType Type,
    decimal WeightKg,
    WeightCategory WeightCategory,
    ShipmentStatus Status,
    PostOfficeSummary Origin,
    PostOfficeSummary Destination,
    PostOfficeSummary Current,
    bool IsLocalDelivery,
    bool CanEdit,
    IReadOnlyList<ShipmentStatus> AllowedNextStatuses,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    int Version);

public sealed record ShipmentHistoryDto(
    long Id,
    ShipmentStatus? PreviousStatus,
    ShipmentStatus Status,
    PostOfficeSummary PostOffice,
    string PerformedBy,
    string? Remarks,
    bool IsCorrection,
    DateTime OccurredAtUtc);

public sealed record TrackingDto(
    Guid ShipmentId,
    string ShipmentNumber,
    ShipmentType Type,
    ShipmentStatus Status,
    PostOfficeSummary Origin,
    PostOfficeSummary Destination,
    PostOfficeSummary Current,
    DateTime UpdatedAtUtc,
    IReadOnlyList<ShipmentHistoryDto> History);

public sealed record ShipmentStatistics(
    int Total,
    int InTransit,
    int Delivered,
    int FailedDeliveries,
    int Lost,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyDictionary<string, int> ByWeightCategory);

/// <summary>Answer to the PostOffices service during its delete saga.</summary>
public sealed record PostOfficeDependencies(string ZipCode, int ActiveShipments, int TotalShipments);

/// <summary>Authoritative post office state returned synchronously by the owning service.</summary>
public sealed record PostOfficeInfo(string ZipCode, string Name, string City, bool CanAcceptShipments, int Version);

/// <summary>Integration event published by the PostOffices service (consumer-side contract).</summary>
public sealed record PostOfficeChangedV1(
    Guid PostOfficeId,
    string Kind,
    string ZipCode,
    string Name,
    string City,
    bool CanAcceptShipments,
    int Version)
{
    public const string EventType = "postoffice.changed.v1";
}
