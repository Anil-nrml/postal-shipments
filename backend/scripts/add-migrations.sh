#!/usr/bin/env bash
# Generates the initial EF Core migration for each service's own database.
# Run once after cloning (or after changing a DbContext model, with a new migration name).
# Usage: ./scripts/add-migrations.sh [MigrationName]
set -euo pipefail
NAME="${1:-InitialCreate}"
cd "$(dirname "$0")/.."

dotnet tool restore >/dev/null 2>&1 || dotnet tool install --global dotnet-ef --version 8.0.11

for svc in Identity PostOffices Shipments; do
  echo "==> $svc: adding migration $NAME"
  dotnet ef migrations add "$NAME" \
    --project "src/Services/$svc/PostalService.$svc.Infrastructure" \
    --startup-project "src/Services/$svc/PostalService.$svc.Api" \
    --output-dir Persistence/Migrations
done
echo "Done. Migrations are applied automatically when each service starts."
