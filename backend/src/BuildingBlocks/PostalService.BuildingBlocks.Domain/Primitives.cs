namespace PostalService.BuildingBlocks.Domain;

/// <summary>Something that happened inside an aggregate that other parts of the system may care about.</summary>
public interface IDomainEvent
{
    Guid EventId { get; }
    DateTime OccurredAtUtc { get; }
}

public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

public abstract class Entity<TId>
    where TId : notnull
{
    public TId Id { get; protected set; } = default!;
}

/// <summary>
/// Consistency boundary. <see cref="Version"/> is an optimistic-concurrency token that the
/// domain increments on every state change; persistence maps it as a concurrency token.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>, IHasDomainEvents
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = new();

    public int Version { get; protected set; }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    protected void IncrementVersion() => Version++;

    public void ClearDomainEvents() => _domainEvents.Clear();
}

public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }
    void ClearDomainEvents();
}

/// <summary>A business rule was violated. Mapped to HTTP 422 with a stable error code.</summary>
public class DomainException : Exception
{
    public DomainException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

public static class Guard
{
    public static string NotBlank(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"{field}.required", $"{field} is required.");
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new DomainException($"{field}.too-long", $"{field} must be at most {maxLength} characters.");
        return trimmed;
    }
}
