# MarketAnalyser API Deployment

This API is the always-on collector and historical backfill source for the Windows app.

## What It Does

- Collects option-chain snapshots from Dhan.
- Stores snapshots in a local SQLite database file.
- Serves stored snapshots to the Windows app for missing-range backfill.
- Protects `/api/*` endpoints with an API key when configured.

`/health` remains public for hosting health checks.

## Publish

From the repository root:

```powershell
.\scripts\publish-api.ps1
```

The publish output is created at:

```text
publish\api
```

Upload the contents of that folder to the hosting site.

## Generate API Key

From the repository root:

```powershell
.\scripts\new-api-key.ps1
```

Use that generated value for both:

- API `Api__ApiKey`
- Windows app `DataSource:HistoricalApiKey`

## Required Production Settings

Configure these in `appsettings.Production.json` on the host, or as environment variables.

Recommended environment variables:

```text
ASPNETCORE_ENVIRONMENT=Production
Api__ApiKey=<long random key>
Dhan__ClientId=<your dhan client id>
Dhan__AccessToken=<your dhan access token>
Collector__IsEnabled=true
Collector__Symbols__0=NIFTY
```

Windows app must use the same API key:

```json
"DataSource": {
  "HistoricalRestBaseUrl": "https://your-api-host",
  "HistoricalApiKey": "<same long random key>"
}
```

## SQLite Database

Default production path:

```text
Data\collector.db
```

This is a normal file. No SQL Server installation is required.

Make sure the hosting app has write permission to the `Data` folder.

## Shared Hosting Note

Some shared hosting providers stop background services when there is no traffic. If that happens:

1. Set:

```json
"Collector": {
  "IsEnabled": false
}
```

2. Trigger collection from an external scheduler:

```http
POST https://your-api-host/api/collector/run-once?symbol=NIFTY
X-MarketAnalyser-Key: <api key>
```

## Useful Endpoints

```http
GET /health
GET /api/collector/status
POST /api/collector/run-once?symbol=NIFTY
GET /api/sessions/NIFTY?from=2026-07-01T09:15:00%2B05:30&to=2026-07-01T15:30:00%2B05:30
POST /api/collector/cleanup
```

All `/api/*` endpoints require:

```http
X-MarketAnalyser-Key: <api key>
```

when `Api:ApiKey` is set.

## After Upload Checklist

1. Set production environment:

```text
ASPNETCORE_ENVIRONMENT=Production
```

2. Configure secrets:

```text
Api__ApiKey=<generated key>
Dhan__ClientId=<your dhan client id>
Dhan__AccessToken=<your dhan access token>
```

3. Confirm the host can write to:

```text
Data\collector.db
```

4. Test the deployed API:

```powershell
.\scripts\test-api.ps1 -BaseUrl "https://your-api-host" -ApiKey "<generated key>"
```

5. Trigger one collection manually:

```powershell
.\scripts\test-api.ps1 -BaseUrl "https://your-api-host" -ApiKey "<generated key>" -RunOnce
```

6. Update Windows app:

```json
"DataSource": {
  "HistoricalRestBaseUrl": "https://your-api-host",
  "HistoricalApiKey": "<generated key>"
}
```
