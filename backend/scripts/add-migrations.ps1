# Generates the initial EF Core migration for each service's own database.
# Usage: ./scripts/add-migrations.ps1 [-Name InitialCreate]
param([string]$Name = "InitialCreate")
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

if (-not (Get-Command dotnet-ef -ErrorAction SilentlyContinue)) {
    dotnet tool install --global dotnet-ef --version 8.0.11
}

foreach ($svc in "Identity", "PostOffices", "Shipments") {
    Write-Host "==> ${svc}: adding migration $Name"
    dotnet ef migrations add $Name `
        --project "src/Services/$svc/PostalService.$svc.Infrastructure" `
        --startup-project "src/Services/$svc/PostalService.$svc.Api" `
        --output-dir Persistence/Migrations
}
Write-Host "Done. Migrations are applied automatically when each service starts."
