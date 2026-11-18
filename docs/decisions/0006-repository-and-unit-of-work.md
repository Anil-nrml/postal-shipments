# ADR-0006: Aggregate repositories; DbContext is the Unit of Work

- **Status:** Accepted
- **Date:** 2026-11-10

## Context
The brief asks for Repository and Unit of Work on EF Core, but warns against unnecessary abstraction.

## Problem
`DbContext` already implements both patterns: `DbSet<T>` is the repository and `SaveChanges` is the unit of work. What should we add?

## Options considered
1. Generic `IRepository<T>` + a separate `UnitOfWork` class wrapping `DbContext`.
2. Use `DbContext` directly in application services.
3. **Aggregate-specific repository interfaces in Application, plus `IUnitOfWork` implemented directly by the DbContext.**

## Decision
Option 3.
- `IShipmentRepository`, `IPostOfficeRepository`, `IUserRepository` and similar expose domain operations (`GetByNumberAsync`, `NumberExistsAsync`, `Add`, `Remove`), not `IQueryable`.
- Read models go through query services (`IShipmentQueries`) that project straight to DTOs with `AsNoTracking`. This is a light CQRS split.
- `IUnitOfWork.SaveChangesAsync` is implemented by each service's DbContext. No wrapper class exists.

## Rationale
- The Application layer stays free of EF Core, which the architecture test enforces, and is unit-testable with fakes or Moq.
- A generic repository would either leak `IQueryable` or need a large specification layer. Neither is justified here.
- A separate UnitOfWork class would only forward to `DbContext.SaveChangesAsync`.

## Consequences
- `SaveChangesAsync` is also where domain events become outbox rows, so state and events commit atomically.

## Trade-offs
- Swapping EF Core for another ORM would need new repository implementations. That is acceptable, because it is exactly what the interfaces are for.
