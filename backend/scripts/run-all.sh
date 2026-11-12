#!/usr/bin/env bash
# Starts Identity (5101), PostOffices (5102), Shipments (5103) and the Gateway (5000).
# Identity starts first because the other services download its signing keys (JWKS).
set -euo pipefail
cd "$(dirname "$0")/.."
export ASPNETCORE_ENVIRONMENT=Development

dotnet build PostalService.sln -v q

pids=()
start() { dotnet run --no-build --project "$1" & pids+=($!); }
trap 'kill "${pids[@]}" 2>/dev/null' EXIT INT TERM

start src/Services/Identity/PostalService.Identity.Api
sleep 4
start src/Services/PostOffices/PostalService.PostOffices.Api
start src/Services/Shipments/PostalService.Shipments.Api
start src/ApiGateway/PostalService.Gateway

echo "Gateway on http://localhost:5000 — press Ctrl+C to stop everything."
wait
