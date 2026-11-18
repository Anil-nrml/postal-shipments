# ADR-0005: CAP trade-offs per operation

- **Status:** Accepted
- **Date:** 2026-11-10

## Context
The services communicate over a network that can partition. Under a partition we must choose, per operation, between consistency (refuse) and availability (answer, possibly stale).

## Problem
Which operations must be strongly consistent, and which can be eventually consistent?

## Options considered
1. Everything synchronous (CP everywhere): simple reasoning, but poor availability and tight coupling.
2. Everything event-driven (AP everywhere): maximally available, but it can register shipments to offices that were just deactivated, and it can lose updates.
3. **Per-operation choice.**

## Decision
| Operation | Choice | How |
|---|---|---|
| Register a shipment: origin/destination exist and accept shipments | CP | Synchronous call to the owner (PostOffices) through Polly (timeout, retry, breaker). It fails closed with 503 + Retry-After. The local replica is **not** used for this decision. |
| Unique shipment number, no duplicates on retry | CP | Unique index; `Idempotency-Key` stored in the same transaction. |
| Status transition and history | CP | One aggregate and one local transaction; `Version` optimistic concurrency → 409. |
| ZIP uniqueness | CP | Unique index in PostOffices. |
| Delete post office | CP | Saga with a pending state and compensation (ADR-0008). |
| Login, refresh rotation | CP | Local to Identity. |
| Lists, filters, statistics, tracking names | AP | Local data plus a replica of post office names updated by events; a missing name falls back to the ZIP. |

## Rationale
The cost of a wrong answer differs. A parcel accepted for a closed office is a physical-world error, whereas a list showing a post office's old name for two seconds is harmless.

## Consequences
- When PostOffices is down, counters cannot register new shipments (503), but scanning, tracking and searching keep working. Operations should alert on the PostOffices circuit breaker.
- JWT validation uses cached JWKS, so Identity being down does not block other services until the cache expires. Only login and refresh fail.

## Trade-offs
- Registration availability is coupled to PostOffices availability. An alternative would be to accept the shipment against the replica (AP) and run a compensating "post office closed" workflow. That was rejected because it adds operational complexity for a rare case.
