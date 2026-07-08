param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [string]$ApiKey = "",

    [string]$Symbol = "NIFTY",

    [switch]$RunOnce
)

$ErrorActionPreference = "Stop"

$base = $BaseUrl.TrimEnd("/")
$headers = @{}
if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
    $headers["X-MarketAnalyser-Key"] = $ApiKey
}

Write-Host "Testing $base"

$health = $null
for ($attempt = 1; $attempt -le 12; $attempt++) {
    try {
        $health = Invoke-RestMethod "$base/health"
        break
    } catch {
        if ($attempt -eq 12) {
            throw
        }

        Start-Sleep -Seconds 2
    }
}

Write-Host "Health: $($health.status)"

$status = Invoke-RestMethod "$base/api/collector/status" -Headers $headers
Write-Host "Collector enabled: $($status.isEnabled)"
Write-Host "Collector running: $($status.isRunning)"
Write-Host "Collector symbols: $($status.symbols -join ',')"
if ($status.lastError) {
    Write-Host "Last collector error: $($status.lastError)"
}

if ($RunOnce) {
    $run = Invoke-RestMethod -Method Post "$base/api/collector/run-once?symbol=$([Uri]::EscapeDataString($Symbol))" -Headers $headers
    Write-Host "Run-once: $($run.symbol), strikes=$($run.strikes), spot=$($run.spot)"
}

$from = [Uri]::EscapeDataString((Get-Date).AddHours(-1).ToString("o"))
$to = [Uri]::EscapeDataString((Get-Date).AddHours(1).ToString("o"))
$sessions = Invoke-RestMethod "$base/api/sessions/$([Uri]::EscapeDataString($Symbol))?from=$from&to=$to" -Headers $headers
Write-Host "Returned snapshots: $(@($sessions).Count)"

Write-Host "API test completed."
