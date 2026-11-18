# ADR-0008: Consistency strategy across and within services

- **Status:** Accepted
- **Date:** 2026-11-10

## Context
Business rules span aggregates and services: inactive offices receive no shipments, offices with active shipments cannot be deleted, no duplicate shipments, no lost status updates.

## Problem
How do we enforce these rules without distributed transactions?

## Decision
| Scope | Tool |
|---|---|
| Inside an aggregate | Invariants in the domain model (`Shipment.ChangeStatus`, `PostOffice.ConfirmDeletion`). Nothing reaches the database in an invalid state. |
| One service, one transaction | EF Core `SaveChanges` (SQLite transaction): aggregate + child rows + outbox + inbox + idempotency key together. |
| Concurrent edits | Optimistic concurrency: `Version` is incremented by the domain, mapped as a concurrency token, and also compared with the client's `version` up front → 409. |
| Duplicate requests | Unique indexes plus an `Idempotency-Key` (key, user) for shipment creation. Retrying the same key returns the original; a different body is a 409. |
| Cross-service read that must be current | Synchronous call to the owner with a resilience pipeline, failing closed. |
| Cross-service business transaction | **Saga** with compensation: the delete post office flow below. |
| Cross-service data that can lag | Integration events → idempotent, version-checked replica. |

### Delete-post-office saga
1. `PostOffice.BeginDeletion()` → `IsPendingDeletion = true`, commit. `CanAcceptShipments` is now false, and Shipments' synchronous check rejects new shipments to this ZIP from this moment.
2. Ask Shipments for active dependencies (sync).
3. If there are none, `ConfirmDeletion()` and remove, then commit (publishes `Deleted`).
4. If some exist, or Shipments is unreachable, `CancelDeletion()` (compensation, committed even if the request was cancelled), then answer 409 or 503.

Because step 1 commits before step 2, there is no window in which a shipment can be registered against an office that is about to disappear.

## Rationale
Each mechanism is the simplest one that gives the guarantee needed, and no two-phase commit is required.

## Consequences
- If the PostOffices process crashes between steps 1 and 4, an office can be left `PendingDeletion`, which is safe (it refuses shipments). A periodic sweeper or an admin "cancel deletion" action resolves it; this is listed under future enhancements.

## Trade-offs
- The saga is orchestrated in-process rather than through a durable saga store. That is acceptable for a two-step saga; use a workflow engine (Durable Functions, MassTransit state machine) if sagas grow.
