using System.Text.RegularExpressions;
using PostalService.BuildingBlocks.Domain;

namespace PostalService.PostOffices.Domain;

public sealed record Address(string Line, string City, string State, string Country)
{
    public static Address Create(string? line, string? city, string? state, string? country) => new(
        Guard.NotBlank(line, "address", 200),
        Guard.NotBlank(city, "city", 100),
        Guard.NotBlank(state, "state", 100),
        Guard.NotBlank(country, "country", 100));
}

/// <summary>Aggregate root. The ZIP code is the business identity and is immutable once created.</summary>
public sealed partial class PostOffice : AggregateRoot<Guid>
{
    private PostOffice() { } // EF Core

    public ZipCode ZipCode { get; private set; } = null!;
    public string Name { get; private set; } = string.Empty;
    public Address Address { get; private set; } = null!;
    public string ContactNumber { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }

    /// <summary>Set while the delete saga checks dependencies; the office refuses new shipments meanwhile.</summary>
    public bool IsPendingDeletion { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Whether new shipments may be sent from or to this office.</summary>
    public bool CanAcceptShipments => IsActive && !IsPendingDeletion;

    public static PostOffice Create(ZipCode zip, string name, Address address, string contactNumber, DateTime utcNow)
    {
        var office = new PostOffice
        {
            Id = Guid.NewGuid(),
            ZipCode = zip,
            Name = Guard.NotBlank(name, "name", 150),
            Address = address,
            ContactNumber = ValidateContact(contactNumber),
            IsActive = true,
            CreatedAtUtc = utcNow,
            UpdatedAtUtc = utcNow,
            Version = 1,
        };
        office.Raise(PostOfficeChanged.From(office, PostOfficeChangeKind.Created));
        return office;
    }

    public void Update(string name, Address address, string contactNumber, DateTime utcNow)
    {
        EnsureNotPendingDeletion();
        Name = Guard.NotBlank(name, "name", 150);
        Address = address;
        ContactNumber = ValidateContact(contactNumber);
        Touch(utcNow, PostOfficeChangeKind.Updated);
    }

    public void Deactivate(DateTime utcNow)
    {
        EnsureNotPendingDeletion();
        if (!IsActive) return;
        IsActive = false;
        Touch(utcNow, PostOfficeChangeKind.Deactivated);
    }

    public void Activate(DateTime utcNow)
    {
        EnsureNotPendingDeletion();
        if (IsActive) return;
        IsActive = true;
        Touch(utcNow, PostOfficeChangeKind.Activated);
    }

    /// <summary>Saga step 1: stop accepting shipments before checking dependencies in another service.</summary>
    public void BeginDeletion(DateTime utcNow)
    {
        EnsureNotPendingDeletion();
        IsPendingDeletion = true;
        Touch(utcNow, PostOfficeChangeKind.Updated);
    }

    /// <summary>Saga compensation: dependencies were found (or could not be checked).</summary>
    public void CancelDeletion(DateTime utcNow)
    {
        if (!IsPendingDeletion) return;
        IsPendingDeletion = false;
        Touch(utcNow, PostOfficeChangeKind.Updated);
    }

    /// <summary>Saga step 3: dependencies confirmed absent.</summary>
    public void ConfirmDeletion(int activeShipments, DateTime utcNow)
    {
        if (!IsPendingDeletion)
            throw new DomainException("postoffice.deletion-not-started", "Deletion has not been started.");
        if (activeShipments > 0)
            throw new DomainException("postoffice.has-active-shipments",
                $"Post office {ZipCode} has {activeShipments} active shipment(s) and cannot be deleted.");
        Touch(utcNow, PostOfficeChangeKind.Deleted);
    }

    private void EnsureNotPendingDeletion()
    {
        if (IsPendingDeletion)
            throw new DomainException("postoffice.pending-deletion", "This post office is being deleted.");
    }

    private void Touch(DateTime utcNow, PostOfficeChangeKind kind)
    {
        UpdatedAtUtc = utcNow;
        IncrementVersion();
        Raise(PostOfficeChanged.From(this, kind));
    }

    private static string ValidateContact(string? contact)
    {
        var value = Guard.NotBlank(contact, "contactNumber", 20);
        if (!ContactPattern().IsMatch(value))
            throw new DomainException("contactNumber.invalid",
                "Contact number may contain digits, spaces, '+', '-' and parentheses only (6-20 characters).");
        return value;
    }

    [GeneratedRegex(@"^\+?[0-9 ()\-]{6,20}$")]
    private static partial Regex ContactPattern();
}

public enum PostOfficeChangeKind
{
    Created,
    Updated,
    Activated,
    Deactivated,
    Deleted,
}

/// <summary>One event type carrying a full snapshot keeps consumers simple and replay-safe.</summary>
public sealed record PostOfficeChanged(
    Guid PostOfficeId,
    PostOfficeChangeKind Kind,
    string ZipCode,
    string Name,
    string City,
    bool CanAcceptShipments,
    int Version) : DomainEvent
{
    public static PostOfficeChanged From(PostOffice office, PostOfficeChangeKind kind) => new(
        office.Id, kind, office.ZipCode.Value, office.Name, office.Address.City, office.CanAcceptShipments,
        office.Version);
}
