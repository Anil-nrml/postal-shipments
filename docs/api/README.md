# REST API reference

Base URL (through the gateway): `http://localhost:5000`. Interactive Swagger UI per service in Development: `:5101/swagger`, `:5102/swagger`, `:5103/swagger`.

## Conventions

| Topic | Rule |
|---|---|
| Auth | Browser: HttpOnly `access_token` cookie set by `/api/auth/login`. API clients and Swagger: `Authorization: Bearer <token>` (Development login returns `accessToken` in the body). |
| CSRF | Cookie-authenticated `POST/PUT/PATCH/DELETE` must send `X-CSRF-Token: <csrf_token cookie>`. Not needed with a Bearer header. |
| Authorization | Per-endpoint permission policies (see the table below). Default policy: authenticated. |
| Enums | Strings: `"Package"`, `"InTransit"`, `"Above5Kg"`. |
| Dates | ISO-8601 UTC with `Z`. |
| Concurrency | Mutable resources carry `version`. Send it back on update. A stale version gives **409**. |
| Errors | RFC 7807 `application/problem+json` with `code` and `correlationId` extensions. |
| Correlation | `X-Correlation-ID` request/response header, also present in logs and traces. |
| Versioning | Not used in v1: the API is consumed only by our own MFEs, deployed together with it. Integration *events* are versioned (`*.v1`). Introduce `Asp.Versioning` (URL segment) when the first external consumer appears. |

### Status codes

| Code | Meaning | Example `code` |
|---|---|---|
| 200 / 201 / 204 | OK / created (with `Location`) / no content | |
| 400 | Input validation failed (`errors` per field) | |
| 401 | Not signed in / token expired | |
| 403 | Missing permission or CSRF token | `forbidden` |
| 404 | Not found | `not-found` |
| 409 | Conflict: stale version, duplicate, dependent data, reused idempotency key | `concurrency`, `postoffice.zip-exists`, `postoffice.has-active-shipments`, `idempotency.key-reused` |
| 422 | Business rule violated | `status.transition-invalid`, `status.wrong-location`, `status.terminal`, `shipment.not-editable`, `postoffice.inactive`, `postoffice.unknown` |
| 429 | Rate limited | |
| 503 | A dependency needed for a consistent decision is down (with `Retry-After`) | `dependency-unavailable` |

### Role → permission matrix

| Permission | Administrator | PostOfficeManager | ShipmentOperator | ShipmentViewer |
|---|:-:|:-:|:-:|:-:|
| shipments.read | ✓ | ✓ | ✓ | ✓ |
| shipments.write (create/edit) | ✓ | ✓ | ✓ | |
| shipments.status | ✓ | ✓ | ✓ | |
| shipments.delete | ✓ | ✓ | | |
| shipments.correct | ✓ | | | |
| postoffices.read | ✓ | ✓ | ✓ | ✓ |
| postoffices.write | ✓ | ✓ | | |
| postoffices.delete | ✓ | | | |
| users.manage | ✓ | | | |

---

## Authentication — Identity service

### POST /api/auth/login
Anonymous. Rate limited to 10/min per IP; the account locks for 5 minutes after 5 failures.

```http
POST /api/auth/login
Content-Type: application/json

{ "userName": "operator", "password": "Operator@12345!" }
```
**200**: sets `access_token` (HttpOnly, path `/api`, 15 min), `refresh_token` (HttpOnly, path `/api/auth`, 7 days), `csrf_token` (readable). All are `SameSite=Strict; Secure`.
```json
{
  "user": { "id": "…", "userName": "operator", "displayName": "Counter Clerk", "role": "ShipmentOperator",
            "isActive": true, "permissions": ["shipments.read", "shipments.write", "shipments.status", "postoffices.read"],
            "lastLoginAtUtc": "2026-11-10T09:00:00Z" },
  "accessTokenExpiresAtUtc": "2026-11-10T09:15:00Z",
  "accessToken": "eyJ… (Development only)"
}
```
**401**: `{ "title": "Login failed", "detail": "Invalid user name or password." }`. The message is identical for an unknown user, a wrong password, a locked account or a disabled user.

### POST /api/auth/refresh
Uses the `refresh_token` cookie. It rotates the refresh token and issues a new access token. **200** same body as login. **401** clears the cookies. Replaying an already-rotated token revokes the whole token family.

### POST /api/auth/logout
**204**. Revokes the refresh-token family and clears the cookies.

### GET /api/auth/me
Authenticated. **200** `User`.

### Users (permission `users.manage`)
| Method | Path | Body | Result |
|---|---|---|---|
| GET | /api/users | | 200 `User[]` |
| POST | /api/users | `{ userName, displayName, role, password }`. The password needs ≥10 chars, upper, lower, digit and symbol. | 201 `User` / 409 `user.exists` |
| PUT | /api/users/{id} | `{ displayName, role, isActive }` | 200 `User`. Changing the role or deactivating revokes sessions. 409 `user.self-demotion`. |
| POST | /api/users/{id}/password | `{ newPassword }` | 204, revokes sessions |
| GET | /api/users/roles | | 200 `{ role: permissions[] }` |

### GET /.well-known/openid-configuration, GET /.well-known/jwks.json
Anonymous discovery and the RSA public key used by the resource services. Not routed by the gateway.

---

## Post offices — PostOffices service

`PostOffice`:
```json
{ "id": "…", "zipCode": "500001", "name": "Hyderabad GPO", "address": "Abids Road", "city": "Hyderabad",
  "state": "Telangana", "country": "India", "contactNumber": "+91 40 2346 3000", "isActive": true,
  "canAcceptShipments": true, "createdAtUtc": "…", "updatedAtUtc": "…", "version": 3 }
```

| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | /api/postoffices?pageNumber=1&pageSize=20&search=hyd&isActive=true | postoffices.read | `search` matches a ZIP prefix, or a substring of name or city. Returns `PagedResult`. |
| GET | /api/postoffices/{id} | postoffices.read | 404 |
| GET | /api/postoffices/by-zip/{zip} | postoffices.read | Search by ZIP code. |
| POST | /api/postoffices | postoffices.write | 201 / 400 / 409 `postoffice.zip-exists` |
| PUT | /api/postoffices/{id} | postoffices.write | Body without `zipCode` (immutable) + `version`. 409 on a stale version. |
| POST | /api/postoffices/{id}/deactivate, /activate | postoffices.write | An inactive office cannot receive new shipments. |
| DELETE | /api/postoffices/{id} | postoffices.delete | 204 / 409 `postoffice.has-active-shipments` / 503 when Shipments is unreachable. The saga always restores the office on failure. |

Validation: ZIP `^[A-Za-z0-9]{3,10}$` (stored upper-case); name ≤150; address ≤200; city/state/country ≤100; contact `^\+?[0-9 ()-]{6,20}$`.

```http
POST /api/postoffices
{ "zipCode": "500081", "name": "Madhapur SO", "address": "Hitech City Road", "city": "Hyderabad",
  "state": "Telangana", "country": "India", "contactNumber": "+91 40 2311 1100" }
```

---

## Shipments & tracking — Shipments service

`Shipment`:
```json
{
  "id": "6c0f…", "shipmentNumber": "SHP-20261110-7K3QX9MZ", "type": "Package",
  "weightKg": 7.5, "weightCategory": "Above5Kg", "status": "InTransit",
  "origin": { "zipCode": "500001", "name": "Hyderabad GPO", "city": "Hyderabad" },
  "destination": { "zipCode": "560001", "name": "Bengaluru GPO", "city": "Bengaluru" },
  "current": { "zipCode": "500001", "name": "Hyderabad GPO", "city": "Hyderabad" },
  "isLocalDelivery": false, "canEdit": false,
  "allowedNextStatuses": ["ReceivedAtDestination", "Lost"],
  "createdAtUtc": "2026-11-10T09:00:00Z", "updatedAtUtc": "2026-11-10T11:00:00Z", "version": 3
}
```

### GET /api/shipments
Permission `shipments.read`. All filters combine with AND.

| Query | Values |
|---|---|
| pageNumber, pageSize | ≥1; pageSize 1–100 (default 20) |
| sortBy, sortDirection | `createdAt` (default), `updatedAt`, `shipmentNumber`, `weight`, `status`; `asc`/`desc` (default `desc`) |
| shipmentNumber | Case-insensitive prefix; `[A-Za-z0-9-]` |
| type | `Letter`, `Package` |
| status | Any `ShipmentStatus` |
| weightCategory | `LessThan1Kg` (<1), `Between1And5Kg` (1–5 inclusive), `Above5Kg` (>5) |
| originPostOffice, destinationPostOffice, currentPostOffice | ZIP |
| createdFrom, createdTo | ISO date-time |

```http
GET /api/shipments?pageNumber=1&pageSize=20&status=InTransit&weightCategory=Above5Kg&currentPostOffice=500001
```
```json
{ "items": [ { "…": "Shipment" } ], "totalRecords": 42, "pageNumber": 1, "pageSize": 20,
  "totalPages": 3, "hasNextPage": true, "hasPreviousPage": false }
```

### GET /api/shipments/{id} · GET /api/shipments/{id}/history · GET /api/shipments/statistics
`history` returns entries oldest first: `{ id, previousStatus, status, postOffice, performedBy, remarks, isCorrection, occurredAtUtc }`.
`statistics` returns `{ total, inTransit, delivered, failedDeliveries, lost, byStatus: {…}, byWeightCategory: {…} }`.

### POST /api/shipments
Permission `shipments.write`. The `Idempotency-Key` header is recommended; one value per user action.
```http
POST /api/shipments
Idempotency-Key: 0b8e8f7e-6a1c-4f0e-9f47-2a0b2a8f3c11

{ "type": "Package", "weightKg": 7.5, "originZip": "500001", "destinationZip": "560001" }
```
**201** `Shipment` with a `Location` header · **200** same shipment when the key is replayed · **400** validation (`weightKg` >0, ≤1000, max 3 decimals) · **409** `idempotency.key-reused` (same key, different body) · **422** `postoffice.inactive` / `postoffice.unknown` · **503** PostOffices unavailable (nothing is saved).

Origin may equal destination (local delivery). The shipment starts as `ReceivedAtOrigin` at the origin office.

### PUT /api/shipments/{id}
Permission `shipments.write`. Allowed only while `ReceivedAtOrigin` (otherwise **422** `shipment.not-editable`).
```json
{ "type": "Letter", "weightKg": 0.25, "destinationZip": "110001", "version": 1 }
```

### DELETE /api/shipments/{id}?version=1
Permission `shipments.delete`. Allowed only while `ReceivedAtOrigin`. **204**.

### PATCH /api/shipments/{id}/status
Permission `shipments.status`.
```json
{ "status": "ProcessedAtOrigin", "postOfficeZip": "500001", "remarks": "Sorted, bag 14", "version": 1 }
```
**200** updated `Shipment` · **409** `concurrency` · **422** `status.transition-invalid` | `status.wrong-location` | `status.terminal`.

Location rules: origin statuses (`ReceivedAtOrigin`, `ProcessedAtOrigin`, `InTransit`) must be recorded at the origin ZIP. Destination statuses (`ReceivedAtDestination`, `ProcessedAtDestination`, `OutForDelivery`, `Delivered`, `FailedDeliveryAttempt`) must be recorded at the destination ZIP. `Lost` can be recorded anywhere.

### POST /api/shipments/{id}/status/corrections
Permission `shipments.correct` (Administrator). It may leave `Delivered`/`Lost`. The location rule still applies, and the entry is flagged `isCorrection`.
```json
{ "status": "OutForDelivery", "postOfficeZip": "560001", "reason": "Scanned the wrong parcel as delivered", "version": 7 }
```

### GET /api/tracking/{shipmentNumber} · GET /api/tracking/{shipmentNumber}/history
Permission `shipments.read`. Returns `{ shipmentId, shipmentNumber, type, status, origin, destination, current, updatedAtUtc, history[] }`. **404** if unknown, **422** if the number is malformed.

---

## Internal endpoints (service-to-service)

Not routed by the gateway. They require `X-Internal-Api-Key`.

| Service | Endpoint | Used by |
|---|---|---|
| PostOffices | `GET /internal/postoffices/{zip}` → `{ zipCode, name, city, canAcceptShipments, version }` or 404 | Shipments, when creating or redirecting a shipment |
| Shipments | `GET /internal/postoffices/{zip}/dependencies` → `{ zipCode, activeShipments, totalShipments }` | PostOffices delete saga |
| Shipments | `POST /internal/events` (envelope `{ eventId, eventType, occurredAtUtc, source, payload }`) → 202 | PostOffices outbox (idempotent) |

### Integration events

| Event | Publisher | Payload |
|---|---|---|
| `postoffice.changed.v1` | PostOffices | `{ postOfficeId, kind: Created\|Updated\|Activated\|Deactivated\|Deleted, zipCode, name, city, canAcceptShipments, version }` |
| `shipment.created.v1` | Shipments | `{ shipmentId, shipmentNumber, type, weightKg, originZip, destinationZip }` |
| `shipment.updated.v1` | Shipments | `{ shipmentId, shipmentNumber, type, weightKg, destinationZip }` |
| `shipment.status-changed.v1` | Shipments | `{ shipmentId, shipmentNumber, previousStatus, newStatus, postOfficeZip, isCorrection, performedByUserId }` |
| `shipment.deleted.v1` | Shipments | `{ shipmentId, shipmentNumber }` |
