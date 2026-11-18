# ADR-0004: One ShipmentStatusHistory entity instead of History + Movement

- **Status:** Accepted
- **Date:** 2026-11-10

## Context
The brief proposes both `ShipmentStatusHistory` and `ShipmentMovement` and asks whether they should be separate.

## Problem
Movement means "the shipment was at post office X at time T". Every status change already happens at a post office.

## Options considered
1. Two tables written together on each scan.
2. **One append-only table**, where each entry records status, post office, actor, time, remarks and a correction flag.

## Decision
Option 2.

## Rationale
- No duplication: the movement trail is `SELECT PostOfficeZip, OccurredAtUtc FROM ShipmentStatusHistory`.
- No drift: two tables could disagree after a partial bug fix; one cannot.
- `Shipment.CurrentZip` and `Status` are denormalised onto the aggregate for fast filtering. Both change in the same transaction as the history insert.

## Consequences
- If future requirements add non-status movements (for example "scanned at hub without status change"), add a nullable `EventKind` column rather than a second table.

## Trade-offs
- None significant at this scale.
