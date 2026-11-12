namespace PostalService.Shipments.Domain;

/// <summary>
/// The shipment lifecycle state machine. Single source of truth for permitted transitions
/// and for where each status may be recorded.
/// </summary>
public static class ShipmentStatusRules
{
    private static readonly IReadOnlyDictionary<ShipmentStatus, ShipmentStatus[]> Transitions =
        new Dictionary<ShipmentStatus, ShipmentStatus[]>
        {
            [ShipmentStatus.ReceivedAtOrigin] = new[] { ShipmentStatus.ProcessedAtOrigin, ShipmentStatus.Lost },
            [ShipmentStatus.ProcessedAtOrigin] = new[]
            {
                ShipmentStatus.InTransit,
                ShipmentStatus.OutForDelivery, // local delivery only, see IsAllowed
                ShipmentStatus.Lost,
            },
            [ShipmentStatus.InTransit] = new[] { ShipmentStatus.ReceivedAtDestination, ShipmentStatus.Lost },
            [ShipmentStatus.ReceivedAtDestination] = new[] { ShipmentStatus.ProcessedAtDestination, ShipmentStatus.Lost },
            [ShipmentStatus.ProcessedAtDestination] = new[] { ShipmentStatus.OutForDelivery, ShipmentStatus.Lost },
            [ShipmentStatus.OutForDelivery] = new[]
            {
                ShipmentStatus.Delivered, ShipmentStatus.FailedDeliveryAttempt, ShipmentStatus.Lost,
            },
            [ShipmentStatus.FailedDeliveryAttempt] = new[]
            {
                ShipmentStatus.OutForDelivery,        // re-attempt
                ShipmentStatus.ProcessedAtDestination, // returned to the centre for holding
                ShipmentStatus.Lost,
            },
            // Terminal: leaving these requires the authorised correction workflow.
            [ShipmentStatus.Delivered] = Array.Empty<ShipmentStatus>(),
            [ShipmentStatus.Lost] = Array.Empty<ShipmentStatus>(),
        };

    public static bool IsTerminal(ShipmentStatus status) =>
        status is ShipmentStatus.Delivered or ShipmentStatus.Lost;

    public static bool IsAllowed(ShipmentStatus from, ShipmentStatus to, bool isLocalDelivery)
    {
        if (!Transitions[from].Contains(to))
            return false;

        // Skipping the trip between centres only makes sense when origin == destination.
        if (from == ShipmentStatus.ProcessedAtOrigin && to == ShipmentStatus.OutForDelivery)
            return isLocalDelivery;

        return true;
    }

    public static IReadOnlyList<ShipmentStatus> NextStatuses(ShipmentStatus from, bool isLocalDelivery) =>
        Transitions[from].Where(to => IsAllowed(from, to, isLocalDelivery)).ToArray();

    public static StatusLocation RequiredLocation(ShipmentStatus status) => status switch
    {
        ShipmentStatus.ReceivedAtOrigin or ShipmentStatus.ProcessedAtOrigin or ShipmentStatus.InTransit
            => StatusLocation.Origin,
        ShipmentStatus.ReceivedAtDestination or ShipmentStatus.ProcessedAtDestination
            or ShipmentStatus.OutForDelivery or ShipmentStatus.Delivered or ShipmentStatus.FailedDeliveryAttempt
            => StatusLocation.Destination,
        _ => StatusLocation.Anywhere,
    };

    /// <summary>True for every status that still needs work (used for post office dependency checks).</summary>
    public static bool IsActive(ShipmentStatus status) => !IsTerminal(status);
}
