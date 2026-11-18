# ADR-0001: Three services plus a gateway, not one service per noun

- **Status:** Accepted
- **Date:** 2026-11-10

## Context
The brief asks for microservices and suggests four: Authentication, Shipment Management, Post Office Management and Shipment Tracking. It also asks us to avoid unnecessary services.

## Problem
Which boundaries give independent ownership and scaling without introducing distributed transactions on hot paths?

## Options considered
1. **Modular monolith**: one deployable, one database, modules with enforced boundaries.
2. **Four services** as suggested.
3. **Three services**: Identity, PostOffices, and Shipments (which includes tracking).

## Decision
Option 3, with a YARP gateway in front.

## Rationale
- A shipment's current status and its history form one invariant ("they never disagree"). Putting tracking in another service would turn every scan into a cross-service write that needs an outbox plus eventual reconciliation. That buys nothing, because tracking has no separate owner or scaling profile.
- Post offices are master data with their own invariant (unique ZIP) and lifecycle. They are a real bounded context.
- Identity has a different security profile and change cadence.
- Tracking remains a separate *read API* (`/api/tracking`) and a separate MFE, so it can be split out later if a real need appears (for example a public tracking site with very different traffic).

## Consequences
- Two cross-service interactions remain (ZIP validation and the delete dependency check), and both are handled explicitly (ADR-0005, ADR-0008).
- Each service can be deployed, scaled and versioned independently.

## Trade-offs
- More moving parts than a modular monolith: three processes, three databases, a gateway. **For a single post office network of modest size, a modular monolith with the same internal layering would be the pragmatic production choice.** The codebase keeps that door open, because each service is already a set of Clean Architecture projects that could be hosted in one process.
