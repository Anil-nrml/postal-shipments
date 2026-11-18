# ADR-0007: Transactional outbox with a swappable transport

- **Status:** Accepted
- **Date:** 2026-11-10

## Context
Shipments needs post office names and cities for display and filters, and future consumers (notifications, analytics) need shipment lifecycle events. The solution must run locally with no extra infrastructure.

## Problem
How do we publish events reliably, without dual writes, and without requiring a broker to run the app?

## Options considered
1. Publish to a broker directly after `SaveChanges`. This is a dual write: an event can be lost or sent for a rolled-back change.
2. Require RabbitMQ or Azure Service Bus locally (Docker).
3. **Transactional outbox + background dispatcher + pluggable transport** (HTTP push locally).

## Decision
Option 3.
- `SaveChangesAsync` converts domain events into `OutboxMessages` rows in the same transaction (`IIntegrationEventMapper` decides what is public and its versioned name, e.g. `postoffice.changed.v1`).
- `OutboxDispatcher<TContext>` polls every 2 seconds and delivers in `OccurredAt` order. A failure stops the batch to preserve order, retries with exponential backoff, and dead-letters after 10 attempts.
- Locally, `HttpPushTransport` POSTs envelopes to the subscriber URLs from configuration, using the internal API key.
- Consumers are idempotent (an `InboxMessages` row in the same transaction as the effect) and order-tolerant (`SourceVersion` check).

## Rationale
At-least-once delivery with no lost or phantom events, and nothing to install.

## Consequences
- Production: implement `IIntegrationEventTransport` for Azure Service Bus (topic per publisher, session id = aggregate id for ordering, a subscription per consumer). Run the dispatcher as a separate worker deployment; with multiple instances, claim rows with a lease column or use the broker's outbox integration (e.g. MassTransit).
- `shipment.*` events are published, but there is no subscriber yet; they are an extension point.

## Trade-offs
- Polling adds up to ~2 s of latency to the replica. HTTP push couples the publisher to subscriber addresses, which is acceptable locally and removed by a broker in production.
