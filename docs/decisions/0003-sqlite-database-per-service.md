# ADR-0003: SQLite, one database file per service

- **Status:** Accepted for local and initial deployment; superseded in production by a managed RDBMS per service
- **Date:** 2026-11-10

## Context
SQLite is mandatory for the initial implementation. Each microservice must own its data.

## Problem
How do we honour database-per-service on SQLite, and what are the limits?

## Options considered
1. One shared SQLite file for all services.
2. **One SQLite file per service**, accessed only by that service.
3. SQL Server or PostgreSQL from day one.

## Decision
Option 2: `identity.db`, `postoffices.db` and `shipments.db`, each under its service's `App_Data/`, accessed through EF Core only.

## Rationale and SQLite limitations
| Topic | Reality | What we do |
|---|---|---|
| Single writer | SQLite serialises writes with a database-level lock. | WAL mode (readers never block the writer); `Default Timeout=30` so writers queue instead of failing with SQLITE_BUSY; short transactions. |
| File-level access | The database is a local file. It cannot be safely shared over network file systems (NFS/SMB locking is unreliable). | Each file belongs to one service process with a local volume. |
| Multiple instances | Two instances of a service cannot share one file safely, so **no horizontal scaling and no HA**. | One instance per service. Documented as the main production blocker. |
| Concurrency | WAL gives concurrent readers plus one writer. | Optimistic concurrency (`Version` token) for business conflicts. |
| Backup and recovery | A live file copy can be torn. | Use `sqlite3 .backup` / `VACUUM INTO` or Litestream for continuous replication to blob storage. Keep WAL files with the DB. |
| Persistence | The container filesystem is ephemeral. | Mount a persistent volume at `App_Data`. |
| Types | No native `decimal`/`DateTimeOffset` ordering. | Weight stored as integer grams; `DateTime` stored as UTC with a converter. |

SQLite alone **does not** provide distributed high availability, and this design does not claim that it does.

## Migration path to PostgreSQL / SQL Server
1. Swap `UseSqlite` for `UseNpgsql`/`UseSqlServer` in `SqliteSetup` (one place per service). Repositories, the Application layer and the Domain do not change.
2. Regenerate migrations for the new provider (`scripts/add-migrations`).
3. Replace the raw `LIKE` fragments in the two search queries (standard SQL, works unchanged). Optionally use `rowversion`/`xmin` instead of the `Version` int.
4. Provision one database per service (or one schema per service with separate credentials). Enable zone redundancy.
5. Run multiple instances behind the gateway, and move the outbox dispatcher to a separate worker deployment (or a broker, ADR-0007).
6. Migrate data with a one-off copy per service (EF bulk copy or `pgloader`).

## Consequences
- Zero infrastructure is needed to run locally.
- Cross-service queries are impossible by construction, which is good for boundaries.

## Trade-offs
- Not production-grade for HA. This is accepted for the initial phase, and the swap is designed in.
