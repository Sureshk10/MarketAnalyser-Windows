using System.IO;
using System.Text.Json;
using MarketAnalyser.Core.Market;

namespace MarketAnalyser.App.Session;

public sealed class HistoricalSupportResistanceCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string rootDirectory;

    public HistoricalSupportResistanceCacheStore()
    {
        rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketAnalyser",
            "sr-cache");
    }

    public async Task<IReadOnlyList<MarketSnapshot>> LoadAsync(
        string symbol,
        string timeframe,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var path = GetPath(symbol, timeframe);
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var cached = await JsonSerializer.DeserializeAsync<IReadOnlyList<MarketSnapshot>>(stream, JsonOptions, cancellationToken);
        if (cached is null || cached.Count == 0)
        {
            return [];
        }

        return cached
            .Where(snapshot => snapshot.Timestamp >= from && snapshot.Timestamp <= to)
            .OrderBy(snapshot => snapshot.Timestamp)
            .ToArray();
    }

    public async Task SaveAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<MarketSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        if (snapshots.Count == 0)
        {
            return;
        }

        var path = GetPath(symbol, timeframe);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        IReadOnlyList<MarketSnapshot> merged;
        if (File.Exists(path))
        {
            await using var existingStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var existing = await JsonSerializer.DeserializeAsync<IReadOnlyList<MarketSnapshot>>(existingStream, JsonOptions, cancellationToken) ?? [];
            merged = existing
                .Concat(snapshots)
                .GroupBy(snapshot => snapshot.Timestamp)
                .Select(group => group.Last())
                .OrderBy(snapshot => snapshot.Timestamp)
                .ToArray();
        }
        else
        {
            merged = snapshots
                .OrderBy(snapshot => snapshot.Timestamp)
                .ToArray();
        }

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, merged, JsonOptions, cancellationToken);
        await SaveMetadataAsync(symbol, timeframe, new HistoricalCacheMetadata(DateTimeOffset.Now, merged.MaxBy(item => item.Timestamp)?.Timestamp), cancellationToken);
    }

    public async Task<DateTimeOffset?> GetLatestTimestampAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken)
    {
        var path = GetPath(symbol, timeframe);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var cached = await JsonSerializer.DeserializeAsync<IReadOnlyList<MarketSnapshot>>(stream, JsonOptions, cancellationToken);
        return cached is null || cached.Count == 0
            ? null
            : cached.MaxBy(snapshot => snapshot.Timestamp)?.Timestamp;
    }

    public async Task<HistoricalCacheMetadata?> GetMetadataAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken)
    {
        var path = GetMetadataPath(symbol, timeframe);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<HistoricalCacheMetadata>(stream, JsonOptions, cancellationToken);
    }

    public bool HasCache(string symbol, string timeframe)
    {
        return File.Exists(GetPath(symbol, timeframe));
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private string GetPath(string symbol, string timeframe)
    {
        var safeSymbol = string.Concat(symbol.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        var safeTimeframe = NormalizeTimeframeKey(timeframe);
        return Path.Combine(rootDirectory, safeSymbol, $"{safeTimeframe}.json");
    }

    private async Task SaveMetadataAsync(
        string symbol,
        string timeframe,
        HistoricalCacheMetadata metadata,
        CancellationToken cancellationToken)
    {
        var path = GetMetadataPath(symbol, timeframe);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken);
    }

    private string GetMetadataPath(string symbol, string timeframe)
    {
        var safeSymbol = string.Concat(symbol.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        var safeTimeframe = NormalizeTimeframeKey(timeframe);
        return Path.Combine(rootDirectory, safeSymbol, $"{safeTimeframe}.meta.json");
    }

    private static string NormalizeTimeframeKey(string timeframe)
    {
        var trimmed = timeframe.Trim();
        return trimmed switch
        {
            "1m" => "1m",
            "3m" => "3m",
            "5m" => "5m",
            "15m" => "15m",
            "1h" => "1h",
            "4h" => "4h",
            "1d" => "1d",
            "1w" => "1w",
            "1M" => "1M",
            _ => string.Concat(trimmed.Where(char.IsLetterOrDigit))
        };
    }
}

public sealed record HistoricalCacheMetadata(
    DateTimeOffset LastSyncAt,
    DateTimeOffset? LastSyncedThrough);
