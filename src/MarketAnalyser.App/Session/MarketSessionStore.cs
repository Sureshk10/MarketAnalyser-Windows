using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketAnalyser.App.ViewModels;
using MarketAnalyser.Core.Market;

namespace MarketAnalyser.App.Session;

public sealed class MarketSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string rootDirectory;

    public MarketSessionStore()
    {
        rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketAnalyser",
            "sessions");
    }

    public async Task<int> CountRecordsAsync(string symbol, DateOnly date, CancellationToken cancellationToken)
    {
        var path = GetSessionPath(symbol, date);
        if (!File.Exists(path))
        {
            return 0;
        }

        var count = 0;
        await foreach (var _ in File.ReadLinesAsync(path, cancellationToken))
        {
            count++;
        }

        return count;
    }

    public async Task<MarketSessionBackfill> LoadBackfillAsync(
        string symbol,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var records = await LoadRecordsAsync(symbol, date, cancellationToken);

        if (records.Count == 0)
        {
            return MarketSessionBackfill.Empty(symbol);
        }

        var ordered = records
            .OrderBy(record => record.Timestamp)
            .ToArray();
        var priceSeries = ordered
            .Select(record => new ChartPoint(record.Timestamp, record.Spot))
            .ToArray();
        var oiChangeSeries = ordered
            .Select(record => new ChartPoint(record.Timestamp, record.Strikes.Sum(strike => strike.PutOpenInterestChange - strike.CallOpenInterestChange)))
            .ToArray();

        return new MarketSessionBackfill(
            symbol,
            ordered.Length,
            ordered.First().Timestamp,
            ordered.Last().Timestamp,
            DetectMissingRanges(ordered, date),
            priceSeries,
            oiChangeSeries);
    }

    public async Task<IReadOnlyList<MarketSessionRecord>> LoadRecordsAsync(
        string symbol,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        return await LoadRecordsAsync(symbol, date, null, cancellationToken);
    }

    public async Task<IReadOnlyList<MarketSessionRecord>> LoadRecordsAsync(
        string symbol,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        if (toDate < fromDate)
        {
            return [];
        }

        var allRecords = new List<MarketSessionRecord>();
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            var records = await LoadRecordsAsync(symbol, date, cancellationToken);
            allRecords.AddRange(records);
        }

        return allRecords
            .OrderBy(record => record.Timestamp)
            .ToArray();
    }

    public async Task<IReadOnlyList<MarketSessionRecord>> LoadRecordsAsync(
        string symbol,
        DateOnly date,
        DateTimeOffset? startFrom,
        CancellationToken cancellationToken)
    {
        var path = GetSessionPath(symbol, date);
        if (!File.Exists(path))
        {
            return [];
        }

        var records = new List<MarketSessionRecord>();
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<MarketSessionRecord>(line, JsonOptions);
                if (record is not null)
                {
                    if (startFrom is not null && record.Timestamp < startFrom.Value)
                    {
                        continue;
                    }

                    records.Add(record);
                }
            }
            catch (JsonException ex)
            {
                AppExceptionLogger.Log(ex);
            }
        }

        return records
            .OrderBy(record => record.Timestamp)
            .ToArray();
    }

    public async Task AppendAsync(
        MarketSnapshot snapshot,
        OptionStrikeSnapshot? selectedStrike,
        MarketSignalViewModel signal,
        CancellationToken cancellationToken)
    {
        var record = MarketSessionRecord.FromSnapshot(snapshot, selectedStrike, signal);
        var path = GetSessionPath(snapshot.Symbol, DateOnly.FromDateTime(snapshot.Timestamp.ToLocalTime().DateTime));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(record, JsonOptions);
        await File.AppendAllTextAsync(path, json + Environment.NewLine, cancellationToken);
    }

    public async Task SaveSnapshotsAsync(
        string symbol,
        DateOnly date,
        IReadOnlyList<MarketSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var path = GetSessionPath(symbol, date);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var records = snapshots
            .OrderBy(snapshot => snapshot.Timestamp)
            .Select(snapshot => MarketSessionRecord.FromSnapshot(snapshot, null, new MarketSignalViewModel("WAIT", "Historical backfill", System.Windows.Media.Brushes.LightSlateGray)))
            .ToArray();

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        foreach (var record in records)
        {
            var json = JsonSerializer.Serialize(record, JsonOptions);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        }
    }

    public string GetSessionPath(string symbol, DateOnly date)
    {
        var safeSymbol = string.Concat(symbol.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        var dateFolder = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Path.Combine(rootDirectory, dateFolder, $"{safeSymbol}.jsonl");
    }

    private static IReadOnlyList<MarketSessionMissingRange> DetectMissingRanges(
        IReadOnlyList<MarketSessionRecord> records,
        DateOnly date)
    {
        var ranges = new List<MarketSessionMissingRange>();
        var marketOpen = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
        var marketClose = new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 30)), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
        var now = DateTimeOffset.Now;
        var expectedEnd = now < marketClose ? now : marketClose;
        var maxAllowedGap = TimeSpan.FromMinutes(2);

        if (expectedEnd <= marketOpen)
        {
            return ranges;
        }

        var ordered = records.OrderBy(record => record.Timestamp).ToArray();
        if (ordered.Length == 0)
        {
            ranges.Add(new MarketSessionMissingRange(marketOpen, expectedEnd));
            return ranges;
        }

        if (ordered.First().Timestamp - marketOpen > maxAllowedGap)
        {
            ranges.Add(new MarketSessionMissingRange(marketOpen, ordered.First().Timestamp));
        }

        for (var i = 1; i < ordered.Length; i++)
        {
            var previous = ordered[i - 1].Timestamp;
            var current = ordered[i].Timestamp;
            if (current - previous > maxAllowedGap)
            {
                ranges.Add(new MarketSessionMissingRange(previous, current));
            }
        }

        if (expectedEnd - ordered.Last().Timestamp > maxAllowedGap)
        {
            ranges.Add(new MarketSessionMissingRange(ordered.Last().Timestamp, expectedEnd));
        }

        return ranges;
    }
}

public sealed record MarketSessionBackfill(
    string Symbol,
    int RecordCount,
    DateTimeOffset? FirstTimestamp,
    DateTimeOffset? LastTimestamp,
    IReadOnlyList<MarketSessionMissingRange> MissingRanges,
    IReadOnlyList<ChartPoint> PriceSeries,
    IReadOnlyList<ChartPoint> OiChangeSeries)
{
    public static MarketSessionBackfill Empty(string symbol)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return new MarketSessionBackfill(symbol, 0, null, null, DetectEmptyMissingRange(today), [], []);
    }

    private static IReadOnlyList<MarketSessionMissingRange> DetectEmptyMissingRange(DateOnly date)
    {
        var marketOpen = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
        var marketClose = new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 30)), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
        var now = DateTimeOffset.Now;
        var expectedEnd = now < marketClose ? now : marketClose;

        return expectedEnd > marketOpen
            ? [new MarketSessionMissingRange(marketOpen, expectedEnd)]
            : [];
    }
}

public sealed record MarketSessionMissingRange(DateTimeOffset From, DateTimeOffset To)
{
    public TimeSpan Duration => To - From;
}

public sealed record MarketSessionRecord(
    string Symbol,
    DateTimeOffset Timestamp,
    decimal Spot,
    decimal SpotChange,
    decimal PutCallRatioOi,
    decimal PutCallRatioVolume,
    decimal CeVolumeShare,
    decimal PeVolumeShare,
    long TotalCallOi,
    long TotalPutOi,
    long TotalCallVolume,
    long TotalPutVolume,
    decimal? SelectedStrike,
    string Signal,
    string SignalDetail,
    IReadOnlyList<MarketSessionStrikeRecord> Strikes,
    MarketSessionDepthRecord? Depth = null)
{
    public static MarketSessionRecord FromSnapshot(
        MarketSnapshot snapshot,
        OptionStrikeSnapshot? selectedStrike,
        MarketSignalViewModel signal)
    {
        return new MarketSessionRecord(
            snapshot.Symbol,
            snapshot.Timestamp,
            snapshot.Spot,
            snapshot.SpotChange,
            snapshot.Breadth.PutCallRatioOi,
            snapshot.Breadth.PutCallRatioVolume,
            snapshot.Breadth.CeVolumeShare,
            snapshot.Breadth.PeVolumeShare,
            snapshot.Breadth.TotalCallOi,
            snapshot.Breadth.TotalPutOi,
            snapshot.Breadth.TotalCallVolume,
            snapshot.Breadth.TotalPutVolume,
            selectedStrike?.Strike,
            signal.Label,
            signal.Detail,
            snapshot.Strikes.Select(MarketSessionStrikeRecord.FromStrike).ToArray(),
            MarketSessionDepthRecord.FromDepth(snapshot.Depth));
    }
}

public sealed record MarketSessionDepthRecord(
    IReadOnlyList<MarketSessionDepthLevelRecord> Bids,
    IReadOnlyList<MarketSessionDepthLevelRecord> Asks)
{
    public static MarketSessionDepthRecord? FromDepth(MarketDepthSnapshot? depth)
    {
        if (depth is null)
        {
            return null;
        }

        return new MarketSessionDepthRecord(
            depth.FiveLevelBids.Select(MarketSessionDepthLevelRecord.FromLevel).ToArray(),
            depth.FiveLevelAsks.Select(MarketSessionDepthLevelRecord.FromLevel).ToArray());
    }

    public MarketDepthSnapshot ToDepth()
    {
        return new MarketDepthSnapshot(
            Bids.Select(level => level.ToLevel()).ToArray(),
            Asks.Select(level => level.ToLevel()).ToArray());
    }
}

public sealed record MarketSessionDepthLevelRecord(
    decimal Price,
    long Quantity,
    int Orders = 0)
{
    public static MarketSessionDepthLevelRecord FromLevel(MarketDepthLevelSnapshot level)
    {
        return new MarketSessionDepthLevelRecord(level.Price, level.Quantity, level.Orders);
    }

    public MarketDepthLevelSnapshot ToLevel()
    {
        return new MarketDepthLevelSnapshot(Price, Quantity, Orders);
    }
}

public sealed record MarketSessionStrikeRecord(
    decimal Strike,
    decimal CallLastPrice,
    long CallOpenInterest,
    long CallOpenInterestChange,
    decimal CallImpliedVolatility,
    decimal CallDelta,
    decimal PutLastPrice,
    long PutOpenInterest,
    long PutOpenInterestChange,
    decimal PutImpliedVolatility,
    decimal PutDelta,
    decimal Support,
    decimal Resistance,
    decimal CallTopBidPrice = 0,
    long CallTopBidQuantity = 0,
    decimal CallTopAskPrice = 0,
    long CallTopAskQuantity = 0,
    decimal PutTopBidPrice = 0,
    long PutTopBidQuantity = 0,
    decimal PutTopAskPrice = 0,
    long PutTopAskQuantity = 0)
{
    public static MarketSessionStrikeRecord FromStrike(OptionStrikeSnapshot strike)
    {
        return new MarketSessionStrikeRecord(
            strike.Strike,
            strike.Call.LastPrice,
            strike.Call.OpenInterest,
            strike.Call.OpenInterestChange,
            strike.Call.ImpliedVolatility,
            strike.Call.Delta,
            strike.Put.LastPrice,
            strike.Put.OpenInterest,
            strike.Put.OpenInterestChange,
            strike.Put.ImpliedVolatility,
            strike.Put.Delta,
            strike.Support,
            strike.Resistance,
            strike.Call.TopBidPrice,
            strike.Call.TopBidQuantity,
            strike.Call.TopAskPrice,
            strike.Call.TopAskQuantity,
            strike.Put.TopBidPrice,
            strike.Put.TopBidQuantity,
            strike.Put.TopAskPrice,
            strike.Put.TopAskQuantity);
    }

    public long CallDepthImbalance => CallTopBidQuantity - CallTopAskQuantity;

    public long PutDepthImbalance => PutTopBidQuantity - PutTopAskQuantity;

    public long NetDepthImbalance => CallDepthImbalance - PutDepthImbalance;
}
