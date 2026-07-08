using System.Collections.Concurrent;
using MarketAnalyser.Core.Configuration;
using MarketAnalyser.Core.Dhan;

namespace MarketAnalyser.Core.Market;

public sealed class EmbeddedMarketDataSource : IMarketDataSource
{
    private readonly InstrumentCatalog catalog;
    private readonly DhanClient dhanClient;
    private readonly DhanOptions dhanOptions;
    private readonly ConcurrentDictionary<string, DateOnly> expiryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MarketSnapshot> snapshotCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, decimal> previousCloseCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LiveOptionInstrumentIndex instrumentIndex = new();
    private readonly Lazy<DhanWebSocketFeedClient> liveFeed;

    public EmbeddedMarketDataSource(
        InstrumentCatalog catalog,
        DhanClient dhanClient,
        DhanOptions dhanOptions)
    {
        this.catalog = catalog;
        this.dhanClient = dhanClient;
        this.dhanOptions = dhanOptions;
        liveFeed = new Lazy<DhanWebSocketFeedClient>(() =>
            new DhanWebSocketFeedClient(instrumentIndex, dhanOptions, ApplyLivePacket));
    }

    public string Name => dhanOptions.UseMockData
        ? "Embedded mock feed"
        : dhanOptions.UseWebSocket ? "Embedded Dhan REST + WebSocket feed" : "Embedded Dhan REST feed";

    public Task<IReadOnlyList<InstrumentSummary>> GetInstrumentsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(catalog.GetAll());
    }

    public async Task<MarketSnapshot> GetSnapshotAsync(string symbol, CancellationToken cancellationToken)
    {
        var instrument = catalog.Find(symbol) ?? throw new InvalidOperationException($"Unknown symbol '{symbol}'.");

        if (dhanOptions.UseMockData)
        {
            var previous = snapshotCache.TryGetValue(instrument.Symbol, out var cached) ? cached : null;
            var mock = MockMarketData.CreateSnapshot(instrument, previous);
            snapshotCache[instrument.Symbol] = mock;
            return mock;
        }

        if (dhanOptions.UseWebSocket &&
            snapshotCache.TryGetValue(instrument.Symbol, out var cachedLive) &&
            DateTimeOffset.UtcNow - cachedLive.Timestamp < TimeSpan.FromSeconds(15))
        {
            liveFeed.Value.Start();
            return cachedLive;
        }

        if (!dhanClient.IsConfigured)
        {
            throw new InvalidOperationException("Dhan ClientId and AccessToken are required when UseMockData is false.");
        }

        var expiry = await GetNearestExpiryAsync(instrument, cancellationToken);
        var response = await dhanClient.GetOptionChainAsync(
            new DhanOptionChainRequest(instrument.UnderlyingSecurityId, instrument.UnderlyingSegment, expiry),
            cancellationToken);

        if (response is null || !string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Dhan option-chain request for {instrument.Symbol} did not return success.");
        }

        snapshotCache.TryGetValue(instrument.Symbol, out var previousSnapshot);
        var previousClose = await GetPreviousCloseAsync(instrument, cancellationToken);
        instrumentIndex.ReplaceSymbol(instrument.Symbol, CreateInstrumentRefs(instrument, response));
        liveFeed.Value.Start();
        var snapshot = Normalize(instrument, response, previousSnapshot, previousClose);
        snapshotCache[instrument.Symbol] = snapshot;
        return snapshot;
    }

    private void ApplyLivePacket(OptionInstrumentRef instrument, DhanFeedPacket packet)
    {
        if (!snapshotCache.TryGetValue(instrument.Symbol, out var current))
        {
            return;
        }

        if (MarketSnapshotUpdater.TryApply(instrument, packet, current, out var updated))
        {
            snapshotCache[instrument.Symbol] = updated;
        }
    }

    private async Task<DateOnly> GetNearestExpiryAsync(InstrumentSummary instrument, CancellationToken cancellationToken)
    {
        if (expiryCache.TryGetValue(instrument.Symbol, out var cached) && cached >= DateOnly.FromDateTime(DateTime.Today))
        {
            return cached;
        }

        var response = await dhanClient.GetExpiryListAsync(
            instrument.UnderlyingSecurityId,
            instrument.UnderlyingSegment,
            cancellationToken);

        if (response is null || !string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase) || response.Data.Count == 0)
        {
            throw new InvalidOperationException($"No active Dhan expiries returned for {instrument.Symbol}.");
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var expiry = response.Data
            .Where(date => date >= today)
            .Order()
            .FirstOrDefault();

        if (expiry == default)
        {
            expiry = response.Data.OrderDescending().First();
        }

        expiryCache[instrument.Symbol] = expiry;
        return expiry;
    }

    private async Task<PreviousCloseResult> GetPreviousCloseAsync(
        InstrumentSummary instrument,
        CancellationToken cancellationToken)
    {
        var historicalInstrument = GetHistoricalInstrumentType(instrument);
        if (historicalInstrument is null)
        {
            return new PreviousCloseResult(null, $"No historical mapping for {instrument.Segment}");
        }

        if (previousCloseCache.TryGetValue(instrument.Symbol, out var cached))
        {
            return new PreviousCloseResult(cached, $"Cached prev close {cached:N2}");
        }

        try
        {
            var today = GetMarketToday();
            var response = await dhanClient.GetDailyHistoricalAsync(
                new DhanHistoricalRequest(
                    instrument.UnderlyingSecurityId,
                    instrument.UnderlyingSegment,
                    historicalInstrument,
                    today.AddDays(-10),
                    today.AddDays(1)),
                cancellationToken);

            var close = GetPreviousTradingClose(response) ?? 0;
            if (close > 0)
            {
                previousCloseCache[instrument.Symbol] = close;
                return new PreviousCloseResult(close, $"Dhan daily prev close {close:N2}");
            }

            var closeCount = response?.Close.Count ?? 0;
            var timestampCount = response?.Timestamp.Count ?? 0;
            return new PreviousCloseResult(null, $"Dhan daily returned no previous close; close={closeCount}, ts={timestampCount}");
        }
        catch (Exception ex)
        {
            return new PreviousCloseResult(null, $"Dhan daily failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? GetHistoricalInstrumentType(InstrumentSummary instrument)
    {
        return instrument.Segment is MarketSegment.Nifty or MarketSegment.BankNifty or MarketSegment.Sensex or MarketSegment.FinNifty
            ? "INDEX"
            : null;
    }

    private static decimal? GetPreviousTradingClose(DhanHistoricalResponse? response)
    {
        if (response is null || response.Close.Count == 0)
        {
            return null;
        }

        if (response.Timestamp.Count == response.Close.Count)
        {
            var sessions = response.Timestamp
                .Zip(response.Close)
                .Where(item => item.Second > 0)
                .OrderBy(item => item.First)
                .ToList();

            return sessions.Count == 0 ? null : sessions[^1].Second;
        }

        var closes = response.Close.Where(value => value > 0).ToList();
        return closes.Count == 0 ? null : closes[^1];
    }

    private static DateOnly GetMarketToday()
    {
        try
        {
            var indiaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, indiaTimeZone).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(DateTime.Today);
        }
    }

    private static MarketSnapshot Normalize(
        InstrumentSummary instrument,
        DhanOptionChainResponse response,
        MarketSnapshot? previous,
        PreviousCloseResult previousClose)
    {
        var now = DateTimeOffset.UtcNow;
        var spot = response.Data.LastPrice;

        var strikes = response.Data.OptionChain
            .Select(item => CreateStrike(item.Key, item.Value, instrument.StrikeInterval))
            .Where(strike => strike is not null)
            .Select(strike => strike!)
            .OrderBy(strike => strike.Strike)
            .ToList();

        var atm = Math.Round(spot / instrument.StrikeInterval) * instrument.StrikeInterval;
        var displayStrikes = strikes
            .Where(strike => Math.Abs(strike.Strike - atm) <= instrument.StrikeInterval * 10)
            .ToList();

        var totalCallOiChange = strikes.Sum(strike => strike.Call.OpenInterestChange);
        var totalPutOiChange = strikes.Sum(strike => strike.Put.OpenInterestChange);

        return new MarketSnapshot(
            instrument.Symbol,
            spot,
            GetSpotChange(spot, previous, previousClose),
            now,
            displayStrikes,
            CreateBreadth(strikes),
            AppendPoint(previous?.PriceSeries ?? [], now, spot),
            AppendPoint(previous?.OiChangeSeries ?? [], now, totalPutOiChange - totalCallOiChange),
            AppendStrikeOiHistory(previous?.StrikeOiChangeSeries ?? [], displayStrikes, now),
            GetPreviousClose(previous, previousClose.Close),
            previousClose.Status);
    }

    private static decimal GetSpotChange(decimal spot, MarketSnapshot? previous, PreviousCloseResult previousClose)
    {
        var baseline = GetPreviousClose(previous, previousClose.Close);
        return baseline is > 0 ? decimal.Round(spot - baseline.Value, 2) : 0;
    }

    private static decimal? GetPreviousClose(MarketSnapshot? previous, decimal? previousClose)
    {
        if (previousClose is > 0)
        {
            return previousClose.Value;
        }

        if (previous?.PreviousClose is > 0)
        {
            return previous.PreviousClose.Value;
        }

        if (previous is null)
        {
            return null;
        }

        var inferredPreviousClose = previous.Spot - previous.SpotChange;
        return inferredPreviousClose > 0 ? inferredPreviousClose : null;
    }

    private static OptionStrikeSnapshot? CreateStrike(string strikeText, DhanOptionStrike optionStrike, decimal strikeInterval)
    {
        if (!decimal.TryParse(strikeText, out var strike) || optionStrike.Call is null || optionStrike.Put is null)
        {
            return null;
        }

        return new OptionStrikeSnapshot(
            strike,
            ToSnapshot(optionStrike.Call),
            ToSnapshot(optionStrike.Put),
            Support: strike - strikeInterval * 0.8m,
            Resistance: strike + strikeInterval * 0.8m);
    }

    private static IEnumerable<OptionInstrumentRef> CreateInstrumentRefs(
        InstrumentSummary instrument,
        DhanOptionChainResponse response)
    {
        yield return new OptionInstrumentRef(
            instrument.Symbol,
            0,
            OptionSide.Call,
            instrument.UnderlyingSegment,
            instrument.UnderlyingSecurityId,
            IsUnderlying: true);

        foreach (var item in response.Data.OptionChain)
        {
            if (!decimal.TryParse(item.Key, out var strike))
            {
                continue;
            }

            if (item.Value.Call is not null)
            {
                yield return new OptionInstrumentRef(
                    instrument.Symbol,
                    strike,
                    OptionSide.Call,
                    instrument.DerivativeSegment,
                    item.Value.Call.SecurityId);
            }

            if (item.Value.Put is not null)
            {
                yield return new OptionInstrumentRef(
                    instrument.Symbol,
                    strike,
                    OptionSide.Put,
                    instrument.DerivativeSegment,
                    item.Value.Put.SecurityId);
            }
        }
    }

    private static OptionLegSnapshot ToSnapshot(DhanOptionLeg leg)
    {
        var change = leg.PreviousClosePrice == 0 ? 0 : leg.LastPrice - leg.PreviousClosePrice;

        return new OptionLegSnapshot(
            LastPrice: leg.LastPrice,
            Change: decimal.Round(change, 2),
            Volume: leg.Volume,
            OpenInterest: leg.OpenInterest,
            OpenInterestChange: leg.OpenInterest - leg.PreviousOpenInterest,
            ImpliedVolatility: decimal.Round(leg.ImpliedVolatility, 2),
            Delta: decimal.Round(leg.Greeks?.Delta ?? 0, 4),
            Gamma: decimal.Round(leg.Greeks?.Gamma ?? 0, 5),
            Theta: decimal.Round(leg.Greeks?.Theta ?? 0, 4),
            Vega: decimal.Round(leg.Greeks?.Vega ?? 0, 4));
    }

    internal static MarketBreadth CreateBreadth(IReadOnlyList<OptionStrikeSnapshot> strikes)
    {
        var totalCallOi = strikes.Sum(strike => strike.Call.OpenInterest);
        var totalPutOi = strikes.Sum(strike => strike.Put.OpenInterest);
        var totalCallVolume = strikes.Sum(strike => strike.Call.Volume);
        var totalPutVolume = strikes.Sum(strike => strike.Put.Volume);

        return new MarketBreadth(
            PutCallRatioOi: Ratio(totalPutOi, totalCallOi),
            PutCallRatioVolume: Ratio(totalPutVolume, totalCallVolume),
            CeVolumeShare: Ratio(totalCallVolume, totalCallVolume + totalPutVolume) * 100,
            PeVolumeShare: Ratio(totalPutVolume, totalCallVolume + totalPutVolume) * 100,
            TotalCallOi: totalCallOi,
            TotalPutOi: totalPutOi,
            TotalCallVolume: totalCallVolume,
            TotalPutVolume: totalPutVolume);
    }

    internal static IReadOnlyList<ChartPoint> AppendPoint(IReadOnlyList<ChartPoint> existing, DateTimeOffset time, decimal value)
    {
        var point = new ChartPoint(time, value);
        var last = existing.LastOrDefault();

        return (last is not null && last.Time.ToUnixTimeSeconds() == time.ToUnixTimeSeconds()
                ? existing.SkipLast(1).Append(point)
                : existing.Append(point))
            .TakeLast(720)
            .ToList();
    }

    internal static IReadOnlyList<StrikeOiChangeSeries> AppendStrikeOiHistory(
        IReadOnlyList<StrikeOiChangeSeries> existing,
        IReadOnlyList<OptionStrikeSnapshot> strikes,
        DateTimeOffset time)
    {
        var existingByStrike = existing.ToDictionary(item => item.Strike);

        return strikes
            .Select(strike =>
            {
                existingByStrike.TryGetValue(strike.Strike, out var history);
                return new StrikeOiChangeSeries(
                    strike.Strike,
                    AppendPoint(history?.Call ?? [], time, strike.Call.OpenInterestChange),
                    AppendPoint(history?.Put ?? [], time, strike.Put.OpenInterestChange),
                    AppendPoint(history?.Difference ?? [], time, strike.Put.OpenInterestChange - strike.Call.OpenInterestChange));
            })
            .ToList();
    }

    private static decimal Ratio(long numerator, long denominator)
    {
        return denominator == 0 ? 0 : decimal.Round((decimal)numerator / denominator, 2);
    }
}

internal sealed record PreviousCloseResult(decimal? Close, string Status);
