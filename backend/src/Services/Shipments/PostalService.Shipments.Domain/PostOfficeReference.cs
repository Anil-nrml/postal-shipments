namespace PostalService.Shipments.Domain;

/// <summary>
/// Local, eventually consistent replica of post office data owned by the PostOffices service.
/// Used only for display names and filters; never for the decision whether a post office may
/// receive a shipment (that check goes to the owner synchronously — see ADR-0005).
/// </summary>
public sealed class PostOfficeReference
{
    private PostOfficeReference() { } // EF Core

    public string ZipCode { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public bool IsDeleted { get; private set; }

    /// <summary>Aggregate version from the owner; older events are ignored (idempotent, ordered).</summary>
    public int SourceVersion { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public static PostOfficeReference Create(string zip, string name, string city, bool isActive, int version,
        DateTime utcNow) => new()
    {
        ZipCode = zip,
        Name = name,
        City = city,
        IsActive = isActive,
        SourceVersion = version,
        UpdatedAtUtc = utcNow,
    };

    /// <returns>False when the incoming data is older than what we already have.</returns>
    public bool Apply(string name, string city, bool isActive, bool isDeleted, int version, DateTime utcNow)
    {
        if (version < SourceVersion)
            return false;

        Name = name;
        City = city;
        IsActive = isActive;
        IsDeleted = isDeleted;
        SourceVersion = version;
        UpdatedAtUtc = utcNow;
        return true;
    }
}
