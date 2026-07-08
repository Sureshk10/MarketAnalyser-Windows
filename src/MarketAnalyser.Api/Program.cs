using System.Text.Json;
using System.Text.Json.Serialization;
using MarketAnalyser.Core.Configuration;
using MarketAnalyser.Core.Dhan;
using MarketAnalyser.Core.Market;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var appOptions = AppOptions.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
var apiOptions = builder.Configuration.GetSection("Api").Get<ApiOptions>() ?? new ApiOptions();
var collectorOptions = (builder.Configuration.GetSection("Collector").Get<CollectorOptions>() ?? new CollectorOptions()).Normalize();

builder.Services.AddSingleton(appOptions);
builder.Services.AddSingleton(apiOptions);
builder.Services.AddSingleton(collectorOptions);
builder.Services.AddSingleton<CollectorStatus>();
builder.Services.AddSingleton<ICollectorSnapshotStore, SqliteCollectorSnapshotStore>();
builder.Services.AddSingleton<CollectorRunner>();
builder.Services.AddHostedService<MarketCollectorService>();
builder.Services.AddSingleton<InstrumentCatalog>(_ => new InstrumentCatalog(appOptions.Instruments));
builder.Services.AddSingleton<DhanClient>(services =>
{
    var options = services.GetRequiredService<AppOptions>();
    var httpClient = new HttpClient
    {
        BaseAddress = new Uri(options.Dhan.RestBaseUrl.TrimEnd('/') + "/")
    };

    return new DhanClient(httpClient, options.Dhan);
});
builder.Services.AddSingleton<IMarketDataSource>(services =>
{
    var options = services.GetRequiredService<AppOptions>();
    var catalog = services.GetRequiredService<InstrumentCatalog>();
    var dhanClient = services.GetRequiredService<DhanClient>();
    return new EmbeddedMarketDataSource(catalog, dhanClient, options.Dhan);
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        !string.IsNullOrWhiteSpace(apiOptions.ApiKey) &&
        !string.Equals(context.Request.Headers["X-MarketAnalyser-Key"], apiOptions.ApiKey, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Invalid or missing API key.");
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    time = DateTimeOffset.Now
}));

app.MapGet("/api/instruments", async (
    IMarketDataSource dataSource,
    CancellationToken cancellationToken) =>
{
    var instruments = await dataSource.GetInstrumentsAsync(cancellationToken);
    return Results.Ok(instruments);
});

app.MapGet("/api/sessions/{symbol}", async (
    string symbol,
    DateTimeOffset? from,
    DateTimeOffset? to,
    ICollectorSnapshotStore store,
    CancellationToken cancellationToken) =>
{
    var fromTime = from ?? DateTimeOffset.Now.Date;
    var toTime = to ?? DateTimeOffset.Now;
    var snapshots = await store.QueryAsync(symbol, fromTime, toTime, cancellationToken);
    return Results.Ok(snapshots);
});

app.MapGet("/api/collector/status", (
    CollectorOptions options,
    CollectorStatus status) => Results.Ok(new
{
    options.IsEnabled,
    options.Symbols,
    options.PollSeconds,
    options.MarketHoursOnly,
    options.MarketOpen,
    options.MarketClose,
    status.IsRunning,
    status.LastRunAt,
    status.LastSuccessAt,
    status.LastSymbol,
    status.LastError,
    status.TotalSnapshots,
    status.TotalFailures
}));

app.MapPost("/api/collector/run-once", async (
    string symbol,
    CollectorRunner runner,
    CancellationToken cancellationToken) =>
{
    var result = await runner.CollectSymbolAsync(symbol, cancellationToken);
    return Results.Ok(new
    {
        result.Symbol,
        result.Timestamp,
        result.Spot,
        result.Strikes,
        result.DeletedOldRows
    });
});

app.MapPost("/api/collector/cleanup", async (
    ICollectorSnapshotStore store,
    ApiOptions options,
    CancellationToken cancellationToken) =>
{
    var deleted = await store.DeleteOlderThanAsync(DateTimeOffset.Now.AddDays(-options.RetentionDays), cancellationToken);
    return Results.Ok(new { deletedOldRows = deleted });
});

app.Run();

public sealed class ApiOptions
{
    public string DatabasePath { get; init; } = Path.Combine("Data", "collector.db");

    public int RetentionDays { get; init; } = 7;

    public string ApiKey { get; init; } = string.Empty;

    public string EffectiveDatabasePath => string.IsNullOrWhiteSpace(DatabasePath)
        ? Path.Combine("Data", "collector.db")
        : DatabasePath;
}

public sealed record class CollectorOptions
{
    public bool IsEnabled { get; init; } = true;

    public string[] Symbols { get; init; } = ["NIFTY"];

    public int PollSeconds { get; init; } = 3;

    public bool MarketHoursOnly { get; init; } = true;

    public string MarketOpen { get; init; } = "09:15";

    public string MarketClose { get; init; } = "15:30";

    public CollectorOptions Normalize()
    {
        return this with
        {
            Symbols = Symbols
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            PollSeconds = Math.Max(1, PollSeconds)
        };
    }
}

public sealed class CollectorStatus
{
    private readonly object gate = new();

    public bool IsRunning { get; private set; }

    public DateTimeOffset? LastRunAt { get; private set; }

    public DateTimeOffset? LastSuccessAt { get; private set; }

    public string? LastSymbol { get; private set; }

    public string? LastError { get; private set; }

    public long TotalSnapshots { get; private set; }

    public long TotalFailures { get; private set; }

    public void MarkRunning(bool isRunning)
    {
        lock (gate)
        {
            IsRunning = isRunning;
        }
    }

    public void MarkSuccess(string symbol, DateTimeOffset timestamp)
    {
        lock (gate)
        {
            LastRunAt = DateTimeOffset.Now;
            LastSuccessAt = timestamp;
            LastSymbol = symbol;
            LastError = null;
            TotalSnapshots++;
        }
    }

    public void MarkFailure(string symbol, Exception exception)
    {
        lock (gate)
        {
            LastRunAt = DateTimeOffset.Now;
            LastSymbol = symbol;
            LastError = exception.Message;
            TotalFailures++;
        }
    }
}

public sealed record CollectorRunResult(
    string Symbol,
    DateTimeOffset Timestamp,
    decimal Spot,
    int Strikes,
    int DeletedOldRows);

public sealed class CollectorRunner(
    IMarketDataSource dataSource,
    ICollectorSnapshotStore store,
    ApiOptions apiOptions,
    CollectorStatus status)
{
    public async Task<CollectorRunResult> CollectSymbolAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await dataSource.GetSnapshotAsync(symbol, cancellationToken);
            await store.AppendAsync(snapshot, cancellationToken);
            var deleted = await store.DeleteOlderThanAsync(
                DateTimeOffset.Now.AddDays(-apiOptions.RetentionDays),
                cancellationToken);

            status.MarkSuccess(snapshot.Symbol, snapshot.Timestamp);
            return new CollectorRunResult(
                snapshot.Symbol,
                snapshot.Timestamp,
                snapshot.Spot,
                snapshot.Strikes.Count,
                deleted);
        }
        catch (Exception ex)
        {
            status.MarkFailure(symbol, ex);
            throw;
        }
    }
}

public sealed class MarketCollectorService(
    CollectorOptions options,
    CollectorRunner runner,
    CollectorStatus status,
    ILogger<MarketCollectorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsEnabled)
        {
            logger.LogInformation("Market collector background service is disabled.");
            return;
        }

        status.MarkRunning(true);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (IsWithinMarketWindow())
                {
                    foreach (var symbol in options.Symbols.Where(item => !string.IsNullOrWhiteSpace(item)))
                    {
                        try
                        {
                            await runner.CollectSymbolAsync(symbol.Trim().ToUpperInvariant(), stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Collector failed for {Symbol}", symbol);
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), stoppingToken);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                }
            }
        }
        finally
        {
            status.MarkRunning(false);
        }
    }

    private bool IsWithinMarketWindow()
    {
        if (!options.MarketHoursOnly)
        {
            return true;
        }

        var now = TimeOnly.FromDateTime(DateTime.Now);
        var open = TimeOnly.TryParse(options.MarketOpen, out var parsedOpen)
            ? parsedOpen
            : new TimeOnly(9, 15);
        var close = TimeOnly.TryParse(options.MarketClose, out var parsedClose)
            ? parsedClose
            : new TimeOnly(15, 30);

        return now >= open && now <= close;
    }
}

public interface ICollectorSnapshotStore
{
    Task AppendAsync(MarketSnapshot snapshot, CancellationToken cancellationToken);

    Task<IReadOnlyList<MarketSnapshot>> QueryAsync(
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}

public sealed class SqliteCollectorSnapshotStore(ApiOptions options) : ICollectorSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim initGate = new(1, 1);
    private bool initialized;

    public async Task AppendAsync(MarketSnapshot snapshot, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO snapshots
                (symbol, timestamp_utc, trading_date, spot, payload)
            VALUES
                ($symbol, $timestampUtc, $tradingDate, $spot, $payload);
            """;
        command.Parameters.AddWithValue("$symbol", snapshot.Symbol.ToUpperInvariant());
        command.Parameters.AddWithValue("$timestampUtc", snapshot.Timestamp.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$tradingDate", snapshot.Timestamp.ToLocalTime().ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$spot", snapshot.Spot.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$payload", json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MarketSnapshot>> QueryAsync(
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var snapshots = new List<MarketSnapshot>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload
            FROM snapshots
            WHERE symbol = $symbol
              AND timestamp_utc >= $fromUtc
              AND timestamp_utc <= $toUtc
            ORDER BY timestamp_utc;
            """;
        command.Parameters.AddWithValue("$symbol", symbol.ToUpperInvariant());
        command.Parameters.AddWithValue("$fromUtc", from.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$toUtc", to.UtcDateTime.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = reader.GetString(0);
            var snapshot = JsonSerializer.Deserialize<MarketSnapshot>(payload, JsonOptions);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshots WHERE timestamp_utc < $cutoffUtc;";
        command.Parameters.AddWithValue("$cutoffUtc", cutoff.UtcDateTime.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized)
        {
            return;
        }

        await initGate.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(options.EffectiveDatabasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    symbol TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    trading_date TEXT NOT NULL,
                    spot TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    UNIQUE(symbol, timestamp_utc)
                );
                CREATE INDEX IF NOT EXISTS ix_snapshots_symbol_time
                    ON snapshots(symbol, timestamp_utc);
                CREATE INDEX IF NOT EXISTS ix_snapshots_trading_date
                    ON snapshots(trading_date);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            initialized = true;
        }
        finally
        {
            initGate.Release();
        }
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection($"Data Source={options.EffectiveDatabasePath};Cache=Shared");
    }
}
