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

    public bool HasCache(string symbol, string timeframe)
    {
        return File.Exists(GetPath(symbol, timeframe));
    }

    private string GetPath(string symbol, string timeframe)
    {
        var safeSymbol = string.Concat(symbol.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        var safeTimeframe = string.Concat(timeframe.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        return Path.Combine(rootDirectory, safeSymbol, $"{safeTimeframe}.json");
    }
}
