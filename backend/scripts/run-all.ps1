# Starts Identity (5101), PostOffices (5102), Shipments (5103) and the Gateway (5000) in separate windows.
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$env:ASPNETCORE_ENVIRONMENT = "Development"

dotnet build PostalService.sln -v q

$projects = @(
    @{ Path = "src/Services/Identity/PostalService.Identity.Api"; Profile = "Identity.Api" },
    @{ Path = "src/Services/PostOffices/PostalService.PostOffices.Api"; Profile = "PostOffices.Api" },
    @{ Path = "src/Services/Shipments/PostalService.Shipments.Api"; Profile = "Shipments.Api" },
    @{ Path = "src/ApiGateway/PostalService.Gateway"; Profile = "Gateway" }
)
foreach ($project in $projects) {
    $command = "dotnet run --no-build --project `"$($project.Path)`" --launch-profile $($project.Profile); Write-Host; Write-Host 'Process exited. Press Enter to close this window.' -ForegroundColor Yellow; Read-Host"
    Start-Process powershell.exe -WorkingDirectory (Get-Location) -ArgumentList '-NoLogo', '-NoExit', '-Command', $command
    if ($project.Path -like "*Identity*") { Start-Sleep -Seconds 4 }
}
Write-Host "Gateway on http://localhost:5000"
