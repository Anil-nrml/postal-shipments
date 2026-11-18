# Architecture

All diagrams are Mermaid and render on GitHub, Azure DevOps and in VS Code (Markdown Preview Mermaid Support).

## Contents

1. [System context](#1-system-context)
2. [High-level architecture](#2-high-level-architecture)
3. [Backend microservices](#3-backend-microservices)
4. [Frontend micro-frontends](#4-frontend-micro-frontends)
5. [Clean Architecture layers](#5-clean-architecture-layers)
6. [Database ER diagrams](#6-database-entity-relationship-diagrams)
7. [Shipment creation sequence](#7-shipment-creation-sequence)
8. [Shipment status update sequence](#8-shipment-status-update-sequence)
9. [Shipment tracking sequence](#9-shipment-tracking-sequence)
10. [Status state machine](#10-shipment-status-state-machine)
11. [Deployment](#11-deployment-architecture)
12. [CAP theorem and failure behaviour](#12-cap-theorem-and-failure-behaviour)

---

## Service boundaries — the decision

| Requirement candidate | Decision | Why |
|---|---|---|
| Authentication service | **Identity service** | Different change cadence, security-sensitive data, its own store. |
| Post office management | **PostOffices service** | Clear master-data context with its own invariant (unique ZIP). |
| Shipment management | **Shipments service** | Core domain. |
| Shipment tracking | **Merged into Shipments** | Current status and history must change in one transaction. Splitting them creates a distributed transaction for every scan, the hottest path in the system. Tracking stays a separate *read API* (`/api/tracking`) and a separate MFE. |

Three services plus a gateway: enough to show real database-per-service and cross-service consistency, without a service per noun (ADR-0001).

---

## 1. System context

```mermaid
flowchart LR
    clerk([Counter clerk / sorter<br/>ShipmentOperator])
    manager([Post office manager])
    admin([Administrator])
    viewer([Auditor<br/>ShipmentViewer])

    subgraph psms[Post Office Shipment Management System]
        app[Web application + APIs]
    end

    otel[(Observability backend<br/>App Insights / Grafana)]
    idp[(Corporate IdP<br/>Entra ID - future)]

    clerk -- register, scan --> app
    manager -- manage offices --> app
    admin -- users, corrections --> app
    viewer -- read only --> app
    app -- OTLP traces/metrics --> otel
    app -. federation in production .-> idp
```

Staff use one web application. Telemetry is exported over OTLP. In production the Identity service would federate to the corporate identity provider rather than store passwords.

## 2. High-level architecture

```mermaid
flowchart TB
    user([Browser])

    subgraph fe[Frontend - Module Federation]
        shell[Shell host :5173<br/>layout, routing, auth guard]
        mAuth[auth MFE :5174<br/>login, users]
        mShip[shipments MFE :5175<br/>list, form, details, dashboard]
        mPo[post-offices MFE :5176]
        mTrack[tracking MFE :5177]
        shell -. remoteEntry.js .-> mAuth & mShip & mPo & mTrack
    end

    gw[API Gateway - YARP :5000<br/>routing, rate limit, CORS,<br/>correlation id, active health checks]

    subgraph be[Backend services]
        id[Identity API :5101<br/>JWT RS256 issuer, JWKS]
        po[PostOffices API :5102]
        sh[Shipments API :5103<br/>shipments + tracking]
    end

    idDb[(identity.db)]
    poDb[(postoffices.db)]
    shDb[(shipments.db)]
    otel[[OpenTelemetry → OTLP]]

    user --> shell
    shell -- "/api/* same-origin, HttpOnly cookies" --> gw
    gw --> id & po & sh
    id --> idDb
    po --> poDb
    sh --> shDb
    po -- "sync: GET /internal/postoffices/{zip}/dependencies" --> sh
    sh -- "sync: GET /internal/postoffices/{zip}" --> po
    po == "async: outbox → POST /internal/events<br/>postoffice.changed.v1" ==> sh
    po & sh -. "JWKS (public key)" .-> id
    id & po & sh & gw -.-> otel
```

**Request flow.** The browser calls `/api/...` on its own origin. The Vite proxy (dev) or the hosting reverse proxy (prod) forwards to the gateway. The gateway routes by path prefix. Each service validates the JWT itself (from the HttpOnly cookie or a bearer header), authorises by permission claim, executes, and returns JSON or RFC 7807 ProblemDetails. The correlation id created at the gateway is echoed back and logged everywhere.

| Component | Responsibility |
|---|---|
| Shell | Owns the layout, top-level routes, the auth guard and the singleton QueryClient. Loads remotes lazily and isolates each behind an error boundary. |
| Remotes | One per bounded context. They expose route-level components only. They never import each other, and they communicate through URLs and window events. |
| Gateway | The single public entry point. It has no business logic and is not trusted for authentication (zero trust). |
| Identity | Login, refresh-token rotation, user/role admin, RSA signing key, JWKS endpoint. |
| PostOffices | Post office master data, unique ZIP, activation, the delete saga. Publishes `postoffice.changed.v1`. |
| Shipments | Shipment aggregate, lifecycle state machine, history, search/filter/paging, statistics, tracking view. Consumes post office events into a local replica. |

## 3. Backend microservices

```mermaid
flowchart LR
    subgraph PO[PostOffices service]
        poApi[API / controllers] --> poApp[PostOfficeService<br/>delete saga] --> poDom[PostOffice aggregate]
        poApp --> poRepo[EF repository]
        poRepo --> poDb[(postoffices.db<br/>PostOffices, Outbox)]
        poDisp[OutboxDispatcher] --> poDb
    end

    subgraph SH[Shipments service]
        shApi[API / controllers] --> shApp[ShipmentService] --> shDom[Shipment aggregate<br/>+ status rules]
        shApp --> shRepo[EF repositories + queries]
        shRepo --> shDb[(shipments.db<br/>Shipments, History,<br/>PostOfficeReferences,<br/>Idempotency, Outbox, Inbox)]
        shIn[Internal: /events, /dependencies] --> shApp
        shDisp[OutboxDispatcher] --> shDb
    end

    subgraph ID[Identity service]
        idApi[Auth / Users / .well-known] --> idApp[AuthService, UserService] --> idDom[User, RefreshToken]
        idApp --> idDb[(identity.db)]
    end

    shApp -- "IPostOfficeDirectory<br/>HTTP + Polly: timeout, retry, breaker" --> poApi
    poApp -- "IShipmentDependencyChecker<br/>HTTP + Polly" --> shIn
    poDisp == "HTTP push, at-least-once, ordered" ==> shIn
    shDisp -. "shipment.* events<br/>(no subscriber yet)" .-> future[[Notifications /<br/>analytics - future]]
```

**Communication patterns**

| Interaction | Style | Why |
|---|---|---|
| Shipments → PostOffices: may this ZIP accept shipments? | Synchronous, resilient HTTP | Needs the owner's *current* truth (CP). Fail closed → 503. |
| PostOffices → Shipments: active dependencies for ZIP? | Synchronous, resilient HTTP | Part of the delete saga; must be current. |
| PostOffices → Shipments: names/cities for display and filters | Asynchronous events (outbox) | Staleness is harmless (AP); no runtime coupling. |
| Shipments → others: lifecycle events | Asynchronous events (outbox) | Extension point for notifications or analytics without changing Shipments. |

The transport is HTTP push behind `IIntegrationEventTransport` so the system runs locally without a broker. In production you swap in Azure Service Bus or RabbitMQ (ADR-0007).

## 4. Frontend micro-frontends

```mermaid
flowchart TB
    subgraph host[Shell - host]
        router[BrowserRouter]
        qc[QueryClient - singleton]
        theme[MUI theme]
        guard[RequireAuth / Guard by permission]
        layout[Layout: AppBar + nav filtered by permissions]
        rb[RemoteBoundary = Suspense + ErrorBoundary]
    end

    subgraph shared[Federation shared singletons]
        r[react / react-dom]
        rr[react-router-dom]
        rq["@tanstack/react-query"]
        mui["@mui/material + emotion"]
    end

    subgraph lib["@postal/shared (bundled into each MFE)"]
        api[api client: cookies, CSRF header,<br/>single-flight refresh, ProblemDetails]
        auth[useSession / useLogin / RequirePermission]
        ui[StatusChip, ErrorBoundary, states]
        ev[window events: session-expired]
    end

    host -->|lazy import| A[auth/Login, auth/Users]
    host -->|lazy import| S[shipments/App, shipments/Dashboard]
    host -->|lazy import| P[postOffices/App]
    host -->|lazy import| T[tracking/App]
    A & S & P & T --> lib
    host & A & S & P & T --- shared
```

| Concern | Approach |
|---|---|
| Integration | Module Federation (`@originjs/vite-plugin-federation`). Remote URLs come from env vars (`VITE_REMOTE_*`), so each MFE deploys and versions independently. |
| Shared components | `@postal/shared` is a workspace library compiled into each MFE (no runtime coupling). Only stateful libraries are federation singletons. |
| Authentication | HttpOnly `access_token` and `refresh_token` cookies. JS never sees a token, so localStorage has nothing to steal. MFEs share session *state* through the singleton QueryClient (`['session']` → `/api/auth/me`). |
| CSRF | SameSite=Strict cookies plus a double-submit token (`csrf_token` cookie echoed as `X-CSRF-Token`) on every write. |
| Routing | The shell owns top-level paths (`/shipments/*`, ...). Each remote owns relative sub-routes. |
| State | Server state lives in React Query (cache, retries, `keepPreviousData` for paging). Filter state lives in the URL. Form state lives in react-hook-form with zod validation. |
| Errors | One error boundary per remote, ProblemDetails mapped to messages and to field errors, 401 → silent refresh → `session-expired` event → login. |
| Lazy loading | `React.lazy` per remote and per route. |
| Version compatibility | Exposed modules are prop-less route components, a tiny contract. Shared deps are pinned at the workspace root. Breaking changes ship as a new exposed module (`./AppV2`). |
| Isolation | No cross-MFE imports (enforced by structure), window events for cross-cutting signals, and a failing remote renders a fallback while the shell keeps working. |

## 5. Clean Architecture layers

```mermaid
flowchart TB
    api[API - Presentation<br/>controllers, ProblemDetails, auth policies, Program.cs]
    infra[Infrastructure<br/>EF Core DbContext, repositories, HTTP clients,<br/>outbox dispatcher, JWT/RSA, password hasher]
    app[Application<br/>use-case services, DTOs, FluentValidation,<br/>ports: IShipmentRepository, IPostOfficeDirectory, IUnitOfWork]
    dom[Domain<br/>aggregates, value objects, domain events,<br/>state machine, invariants]
    kernel[BuildingBlocks.Domain<br/>AggregateRoot, ZipCode, DomainException]

    api --> infra
    api --> app
    infra --> app
    app --> dom
    dom --> kernel
```

Dependencies point inward only. The Domain has no package references at all, and the Application layer has no EF Core. `tests/PostalService.ArchitectureTests` fails the build if any service breaks this rule or references another service's assemblies.

## 6. Database entity relationship diagrams

Each service owns one SQLite file. No foreign keys cross databases; cross-service references are by business key (ZIP code).

```mermaid
erDiagram
    POST_OFFICES {
        guid Id PK
        string ZipCode UK "3-10 alnum, upper-case"
        string Name
        string AddressLine
        string City
        string State
        string Country
        string ContactNumber
        bool IsActive
        bool IsPendingDeletion "delete saga flag"
        datetime CreatedAtUtc
        datetime UpdatedAtUtc
        int Version "concurrency token"
    }
    PO_OUTBOX_MESSAGES {
        guid Id PK
        string EventType
        string Payload
        datetime OccurredAtUtc
        datetime ProcessedAtUtc
        int Attempts
        bool IsDeadLettered
    }
```

```mermaid
erDiagram
    SHIPMENTS ||--|{ SHIPMENT_STATUS_HISTORY : "has (cascade)"
    SHIPMENTS }o..|| POST_OFFICE_REFERENCES : "origin/destination/current ZIP (logical)"
    SHIPMENTS {
        guid Id PK
        string ShipmentNumber UK "SHP-YYYYMMDD-XXXXXXXX"
        string Type "Letter | Package"
        long WeightGrams "exact; sortable"
        string WeightCategory "indexed"
        string OriginZip "indexed"
        string DestinationZip "indexed"
        string CurrentZip "index (CurrentZip, Status)"
        string Status "indexed"
        datetime CreatedAtUtc "indexed"
        datetime UpdatedAtUtc
        guid CreatedByUserId
        int Version "concurrency token"
    }
    SHIPMENT_STATUS_HISTORY {
        long Id PK
        guid ShipmentId FK
        string PreviousStatus
        string Status
        string PostOfficeZip "where it was recorded"
        guid PerformedByUserId
        string PerformedBy
        string Remarks
        bool IsCorrection
        datetime OccurredAtUtc
    }
    POST_OFFICE_REFERENCES {
        string ZipCode PK
        string Name
        string City
        bool IsActive
        bool IsDeleted
        int SourceVersion "ignore stale events"
    }
    IDEMPOTENCY_KEYS {
        string Key PK
        guid UserId PK
        string RequestHash
        guid ShipmentId
    }
    INBOX_MESSAGES {
        guid EventId PK
        string EventType
        datetime ProcessedAtUtc
    }
```

```mermaid
erDiagram
    USERS ||--o{ REFRESH_TOKENS : owns
    USERS {
        guid Id PK
        string UserName
        string NormalizedUserName UK
        string DisplayName
        string PasswordHash "PBKDF2-SHA512 100k"
        string Role
        bool IsActive
        int FailedLoginCount
        datetime LockoutEndUtc
        int Version
    }
    REFRESH_TOKENS {
        guid Id PK
        guid UserId FK
        guid FamilyId "rotation chain"
        string TokenHash UK "SHA-256, never the raw token"
        datetime ExpiresAtUtc
        datetime RevokedAtUtc
        guid ReplacedByTokenId
    }
```

**History vs movement.** There is one entity, `ShipmentStatusHistory`. Every entry records *where* it happened (`PostOfficeZip`), so the movement trail is a projection of the history, and a separate `ShipmentMovement` table would duplicate data and could drift (ADR-0004).

**Roles.** A role is a single column, and permissions derive from the role in code (`RolePermissions`). `Roles` and `UserRoles` tables would add joins without a requirement for user-defined roles; they are a listed future enhancement.

## 7. Shipment creation sequence

```mermaid
sequenceDiagram
    autonumber
    actor C as Clerk
    participant UI as shipments MFE
    participant GW as Gateway
    participant SH as Shipments API
    participant PO as PostOffices API
    participant DB as shipments.db

    C->>UI: Fill form, Register
    UI->>GW: POST /api/shipments<br/>Idempotency-Key, X-CSRF-Token, cookie
    GW->>SH: forward (+X-Correlation-ID)
    SH->>SH: JWT + permission shipments.write, CSRF, FluentValidation
    SH->>DB: idempotency key seen? (key, user)
    alt replay
        DB-->>SH: existing ShipmentId
        SH-->>UI: 200 original shipment
    else new
        SH->>PO: GET /internal/postoffices/{origin}
        SH->>PO: GET /internal/postoffices/{destination}
        alt PostOffices down / breaker open
            SH-->>UI: 503 + Retry-After (nothing saved)
        else office inactive or unknown
            SH-->>UI: 422 postoffice.inactive / unknown
        else ok
            SH->>SH: Shipment.Create → history[0], ShipmentCreated
            SH->>DB: one transaction: Shipments + History + IdempotencyKeys + Outbox
            SH-->>UI: 201 Created + Location
        end
    end
```

## 8. Shipment status update sequence

```mermaid
sequenceDiagram
    autonumber
    actor S as Sorter
    participant UI as shipments MFE
    participant SH as Shipments API
    participant DB as shipments.db
    participant OB as OutboxDispatcher

    S->>UI: Update status (version = 4)
    UI->>SH: PATCH /api/shipments/{id}/status {status, zip, version: 4}
    SH->>DB: load Shipment (version 4)
    SH->>SH: version matches? (else 409)
    SH->>SH: ShipmentStatusRules: transition allowed? location correct? (else 422)
    SH->>DB: BEGIN<br/>UPDATE Shipments SET Status, CurrentZip, Version=5 WHERE Id=@id AND Version=4<br/>INSERT History<br/>INSERT Outbox(shipment.status-changed.v1)<br/>COMMIT
    alt concurrent scan won the race
        DB-->>SH: 0 rows → DbUpdateConcurrencyException
        SH-->>UI: 409 "changed by someone else"
    else
        SH-->>UI: 200 shipment (version 5, allowedNextStatuses)
    end
    OB->>DB: poll unprocessed outbox (ordered)
    OB-->>OB: publish to subscribers, mark processed / back off
```

## 9. Shipment tracking sequence

```mermaid
sequenceDiagram
    autonumber
    actor U as User
    participant T as tracking MFE
    participant SH as Shipments API
    participant DB as shipments.db

    U->>T: Enter SHP-20261110-7K3QX9MZ
    T->>T: validate format client-side
    T->>SH: GET /api/tracking/SHP-20261110-7K3QX9MZ
    SH->>DB: Shipment + History (unique index on ShipmentNumber)
    SH->>DB: PostOfficeReferences for names (local replica)
    SH-->>T: 200 TrackingDto (history oldest→newest)
    T-->>U: summary + timeline (newest first), link to details
    Note over SH,DB: If the replica lacks a name (event not yet arrived),<br/>the ZIP is shown instead: eventual consistency, never an error.
```

## 10. Shipment status state machine

```mermaid
stateDiagram-v2
    [*] --> ReceivedAtOrigin : register (at origin)
    ReceivedAtOrigin --> ProcessedAtOrigin : at origin
    ProcessedAtOrigin --> InTransit : at origin
    ProcessedAtOrigin --> OutForDelivery : local delivery only
    InTransit --> ReceivedAtDestination : at destination
    ReceivedAtDestination --> ProcessedAtDestination : at destination
    ProcessedAtDestination --> OutForDelivery : at destination
    OutForDelivery --> Delivered : at destination
    OutForDelivery --> FailedDeliveryAttempt : at destination
    FailedDeliveryAttempt --> OutForDelivery : re-attempt
    FailedDeliveryAttempt --> ProcessedAtDestination : held at centre
    ReceivedAtOrigin --> Lost
    ProcessedAtOrigin --> Lost
    InTransit --> Lost
    ReceivedAtDestination --> Lost
    ProcessedAtDestination --> Lost
    OutForDelivery --> Lost
    FailedDeliveryAttempt --> Lost
    Delivered --> [*]
    Lost --> [*]
    note right of Delivered
        Terminal. Leaving Delivered/Lost requires the
        correction workflow: permission shipments.correct
        (Administrator), reason >= 10 chars, flagged in history.
    end note
```

Editing and deleting are allowed only in `ReceivedAtOrigin`.

## 11. Deployment architecture

```mermaid
flowchart TB
    subgraph local[Local - one machine]
        vite[Vite: shell :5173 + 4 remote previews]
        g1[Gateway :5000]
        s1[Identity :5101] --- f1[(App_Data/identity.db)]
        s2[PostOffices :5102] --- f2[(App_Data/postoffices.db)]
        s3[Shipments :5103] --- f3[(App_Data/shipments.db)]
        vite --> g1 --> s1 & s2 & s3
    end

    subgraph prod[Production target - Azure example]
        cdn[Azure Front Door + Storage/CDN<br/>shell + versioned remoteEntry.js]
        apim[Gateway: YARP on Container Apps, or APIM]
        subgraph aca[Container Apps / AKS - min 2 replicas each]
            pi[Identity] ; pp[PostOffices] ; ps[Shipments API] ; pw[Shipments outbox worker]
        end
        sql1[(Azure SQL / PostgreSQL<br/>one database per service,<br/>zone-redundant)]
        sb[[Azure Service Bus<br/>topics + sessions]]
        kv[Key Vault: signing key,<br/>internal secrets]
        ai[Application Insights / Grafana]
        cdn --> apim --> pi & pp & ps
        pi & pp & ps & pw --> sql1
        pp & pw --> sb --> ps
        pi --> kv
        aca -.-> ai
    end
```

SQLite pins each service to **one instance** with a local volume. That is fine for development and small single-node deployments, but it is not highly available. The production column replaces each SQLite file with a managed database per service, which is the change that allows horizontal scaling (ADR-0003).

## 12. CAP theorem and failure behaviour

When the network partitions, each operation must choose consistency (refuse) or availability (answer, possibly stale). The choice is made per operation, not per system.

```mermaid
flowchart LR
    subgraph CP[Consistency first - refuse when unsure]
        c1[Create shipment<br/>origin/destination must be active]
        c2[Status change<br/>valid transition, no lost update]
        c3[ZIP uniqueness]
        c4[Delete post office<br/>no active shipments]
        c5[Login / refresh rotation]
    end
    subgraph AP[Availability first - answer from local data]
        a1[Search / list / filter]
        a2[Dashboard statistics]
        a3[Tracking view & names]
    end
    p{{Partition:<br/>PostOffices ↮ Shipments}}
    p -->|c1| r1[503 + Retry-After, nothing saved]
    p -->|c4| r2[saga compensates → 503/409, office stays]
    p -->|c2, c3, c5| r3[unaffected: single-service transaction]
    p -->|a1, a2, a3| r4[served; names may be stale<br/>→ ZIP shown as fallback]
```

| Operation | Choice | Mechanism | During a partition |
|---|---|---|---|
| Create shipment | **CP** | Synchronous check with the owner + Polly (800 ms attempt, 2 retries, circuit breaker). Unique index on number, idempotency key. | 503 with Retry-After. The client retries safely with the same Idempotency-Key. |
| Status change | **CP** | One aggregate, one local transaction; `Version` optimistic concurrency token. | Unaffected (no remote call). Concurrent scans: one wins, the other gets 409. |
| Current status vs history | **CP** | Same aggregate, same transaction. | Cannot diverge. |
| ZIP uniqueness | **CP** | Unique index in the owning DB. | Unaffected. |
| Inactive office refuses shipments | **CP** | Shipments asks PostOffices synchronously (not the replica). | Fail closed (503). |
| Delete post office | **CP** | Saga: `PendingDeletion` (commit) → dependency check → delete, or compensate. | Compensates and returns 503. |
| Search, statistics, tracking names | **AP** | Local DB plus a replica updated by idempotent, version-checked events. | Served. Names may lag by seconds. |
| Integration events | at-least-once | Transactional outbox, ordered dispatcher, inbox dedupe, DLQ after 10 attempts. | Queued in the outbox and delivered when the link heals. |

**SQLite and P.** Within one SQLite file there is no partition, because it is a single node. The CAP trade-offs above apply between services. When moving to replicated databases, keep the CP operations on the primary or with synchronous replicas, and serve AP reads from read replicas (ADR-0005).
