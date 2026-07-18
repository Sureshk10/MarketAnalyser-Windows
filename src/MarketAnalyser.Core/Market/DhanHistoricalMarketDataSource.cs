using MarketAnalyser.Core.Dhan;

namespace MarketAnalyser.Core.Market;

public sealed class DhanHistoricalMarketDataSource(
    InstrumentCatalog catalog,
    DhanClient dhanClient) : IHistoricalMarketDataSource
{
    public async Task<IReadOnlyList<MarketSnapshot>> GetSnapshotsAsync(
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var instrument = catalog.Find(symbol);
        if (instrument is null || !dhanClient.IsConfigured)
        {
            return [];
        }

        var historicalInstrument = GetHistoricalInstrumentType(instrument);
        if (historicalInstrument is null)
        {
            return [];
        }

        if (to < from)
        {
            return [];
        }

        var snapshots = new List<MarketSnapshot>();
        for (var day = DateOnly.FromDateTime(from.LocalDateTime); day <= DateOnly.FromDateTime(to.LocalDateTime); day = day.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dayFrom = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 15)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(9, 15))));
            var dayTo = new DateTimeOffset(day.ToDateTime(new TimeOnly(15, 30)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(15, 30))));

            var response = await dhanClient.GetIntradayHistoricalAsync(
                new DhanIntradayHistoricalRequest(
                    instrument.UnderlyingSecurityId,
                    instrument.UnderlyingSegment,
                    historicalInstrument,
                    1,
                    true,
                    dayFrom,
                    dayTo),
                cancellationToken);

            if (response is null || response.Close.Count == 0)
            {
                continue;
            }

            var points = BuildDaySnapshots(symbol, instrument, response, day);
            snapshots.AddRange(points.Where(item => item.Timestamp >= from && item.Timestamp <= to));
        }

        return snapshots
            .OrderBy(item => item.Timestamp)
            .ToArray();
    }

    private static IReadOnlyList<MarketSnapshot> BuildDaySnapshots(
        string symbol,
        InstrumentSummary instrument,
        DhanIntradayHistoricalResponse response,
        DateOnly day)
    {
        var snapshots = new List<MarketSnapshot>();
        decimal? previousClose = null;

        var count = new[]
        {
            response.Open.Count,
            response.High.Count,
            response.Low.Count,
            response.Close.Count,
            response.Timestamp.Count
        }.Min();

        for (var i = 0; i < count; i++)
        {
            var close = response.Close[i];
            if (close <= 0)
            {
                continue;
            }

            var timestamp = ConvertTimestamp(response, day, i);
            var spotChange = previousClose is null ? 0 : close - previousClose.Value;
            var chartPoint = new ChartPoint(timestamp, close);
            var openInterest = response.OpenInterest.Count > i ? response.OpenInterest[i] : 0;

            snapshots.Add(new MarketSnapshot(
                symbol,
                close,
                spotChange,
                timestamp,
                [],
                new MarketBreadth(0, 0, 0, 0, openInterest, 0, 0, 0),
                [chartPoint],
                [new ChartPoint(timestamp, response.Volume.Count > i ? response.Volume[i] : 0)],
                [chartPoint],
                [],
                null,
                "Dhan historical close",
                null));

            previousClose = close;
        }

        return snapshots;
    }

    private static DateTimeOffset ConvertTimestamp(DhanHistoricalResponse response, DateOnly day, int index)
    {
        if (response.Timestamp.Count > index)
        {
            var epochSeconds = response.Timestamp[index];
            if (epochSeconds > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToLocalTime();
            }
        }

        var fallback = day.ToDateTime(new TimeOnly(9, 15)).AddMinutes(index);
        return new DateTimeOffset(fallback, TimeZoneInfo.Local.GetUtcOffset(fallback));
    }

    private static DateTimeOffset ConvertTimestamp(DhanIntradayHistoricalResponse response, DateOnly day, int index)
    {
        if (response.Timestamp.Count > index)
        {
            var epochSeconds = response.Timestamp[index];
            if (epochSeconds > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToLocalTime();
            }
        }

        var fallback = day.ToDateTime(new TimeOnly(9, 15)).AddMinutes(index);
        return new DateTimeOffset(fallback, TimeZoneInfo.Local.GetUtcOffset(fallback));
    }

    private static string? GetHistoricalInstrumentType(InstrumentSummary instrument)
    {
        return instrument.Segment is MarketSegment.Nifty or MarketSegment.BankNifty or MarketSegment.Sensex or MarketSegment.FinNifty
            ? "INDEX"
            : null;
    }
}
