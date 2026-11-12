namespace PostalService.Shipments.Domain;

public enum ShipmentType
{
    Letter = 1,
    Package = 2,
}

public enum WeightCategory
{
    /// <summary>weight &lt; 1 kg</summary>
    LessThan1Kg = 1,

    /// <summary>1 kg &lt;= weight &lt;= 5 kg</summary>
    Between1And5Kg = 2,

    /// <summary>weight &gt; 5 kg</summary>
    Above5Kg = 3,
}

public enum ShipmentStatus
{
    ReceivedAtOrigin = 1,
    ProcessedAtOrigin = 2,
    InTransit = 3,
    ReceivedAtDestination = 4,
    ProcessedAtDestination = 5,
    OutForDelivery = 6,
    Delivered = 7,
    FailedDeliveryAttempt = 8,
    Lost = 9,
}

/// <summary>Where a status may be recorded, relative to the shipment's route.</summary>
public enum StatusLocation
{
    Origin,
    Destination,
    Anywhere,
}
