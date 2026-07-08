param(
    [string]$Configuration = "Release",
    [string]$Output = "publish/api"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src/MarketAnalyser.Api/MarketAnalyser.Api.csproj"
$publishPath = Join-Path $repoRoot $Output

dotnet publish $project `
    --configuration $Configuration `
    --output $publishPath `
    --self-contained false

$productionTemplate = Join-Path $repoRoot "src/MarketAnalyser.Api/appsettings.Production.json"
Copy-Item -LiteralPath $productionTemplate -Destination (Join-Path $publishPath "appsettings.Production.json") -Force

Write-Host "API published to: $publishPath"
Write-Host "Edit appsettings.Production.json or set environment variables on the host before starting the API."
