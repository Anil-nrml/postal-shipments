using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PostalService.BuildingBlocks.Domain;

namespace PostalService.BuildingBlocks.Infrastructure.Messaging;

/// <summary>Row written in the same transaction as the business change (transactional outbox).</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string? AggregateId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public string? LastError { get; set; }
    public bool IsDeadLettered { get; set; }
}

/// <summary>Records consumed event ids so redelivered events are ignored (idempotent consumer).</summary>
public sealed class InboxMessage
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTime ProcessedAtUtc { get; set; }
}

/// <summary>Wire format for integration events between services.</summary>
public sealed record IntegrationEventEnvelope(
    Guid EventId,
    string EventType,
    DateTime OccurredAtUtc,
    string Source,
    JsonElement Payload);

/// <summary>
/// Translates internal domain events into public integration events. Returning null keeps an event
/// internal. Keeping this explicit avoids leaking domain types into another service's contract.
/// </summary>
public interface IIntegrationEventMapper
{
    (string EventType, object Payload, string? AggregateId)? Map(IDomainEvent domainEvent);
}

public static class OutboxJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

public static class OutboxModelBuilderExtensions
{
    public static ModelBuilder ApplyOutboxAndInbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("OutboxMessages");
            b.HasKey(x => x.Id);
            b.Property(x => x.EventType).HasMaxLength(200).IsRequired();
            b.Property(x => x.Payload).IsRequired();
            b.Property(x => x.AggregateId).HasMaxLength(100);
            b.HasIndex(x => new { x.ProcessedAtUtc, x.IsDeadLettered, x.OccurredAtUtc });
        });

        modelBuilder.Entity<InboxMessage>(b =>
        {
            b.ToTable("InboxMessages");
            b.HasKey(x => x.EventId);
            b.Property(x => x.EventType).HasMaxLength(200).IsRequired();
        });

        return modelBuilder;
    }
}

public static class OutboxWriter
{
    /// <summary>
    /// Moves domain events from tracked aggregates into the outbox. Call from SaveChangesAsync before
    /// base.SaveChangesAsync so the events commit atomically with the state change.
    /// </summary>
    public static void CaptureDomainEvents(DbContext context, IIntegrationEventMapper mapper, DateTime utcNow)
    {
        var aggregates = context.ChangeTracker.Entries<IHasDomainEvents>()
            .Select(e => e.Entity)
            .Where(e => e.DomainEvents.Count > 0)
            .ToList();

        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                var mapped = mapper.Map(domainEvent);
                if (mapped is null) continue;

                context.Set<OutboxMessage>().Add(new OutboxMessage
                {
                    Id = domainEvent.EventId,
                    EventType = mapped.Value.EventType,
                    Payload = JsonSerializer.Serialize(mapped.Value.Payload, mapped.Value.Payload.GetType(),
                        OutboxJson.Options),
                    AggregateId = mapped.Value.AggregateId,
                    OccurredAtUtc = domainEvent.OccurredAtUtc == default ? utcNow : domainEvent.OccurredAtUtc,
                    NextAttemptAtUtc = utcNow,
                });
            }

            aggregate.ClearDomainEvents();
        }
    }
}
