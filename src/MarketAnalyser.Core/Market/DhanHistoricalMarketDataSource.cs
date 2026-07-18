using System.Net;
using System.Net.Http;
using System.Diagnostics;
using MarketAnalyser.Core.Dhan;

namespace MarketAnalyser.Core.Market;

public sealed class DhanHistoricalMarketDataSource(
    InstrumentCatalog catalog,
    DhanClient dhanClient) : IHistoricalMarketDataSource
{
    private static readonly TimeOnly MarketOpenTime = new(9, 15);
    private static readonly TimeOnly MarketCloseTime = new(15, 30);

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
        var dayCursor = from.Date;
        var endDay = to.Date;
        while (dayCursor <= endDay)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tradingStart = new DateTimeOffset(dayCursor.Year, dayCursor.Month, dayCursor.Day, MarketOpenTime.Hour, MarketOpenTime.Minute, 0, from.Offset);
            var tradingEnd = new DateTimeOffset(dayCursor.Year, dayCursor.Month, dayCursor.Day, MarketCloseTime.Hour, MarketCloseTime.Minute, 0, to.Offset);
            var requestFrom = from > tradingStart ? from : tradingStart;
            var requestTo = to < tradingEnd ? to : tradingEnd;

            if (requestTo < requestFrom)
            {
                dayCursor = dayCursor.AddDays(1);
                continue;
            }

            DhanIntradayHistoricalResponse? response;
            try
            {
                response = await dhanClient.GetIntradayHistoricalAsync(
                    new DhanIntradayHistoricalRequest(
                        instrument.UnderlyingSecurityId,
                        instrument.UnderlyingSegment,
                        historicalInstrument,
                        1,
                        true,
                        requestFrom,
                        requestTo),
                    cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Trace.WriteLine($"Dhan historical rate limited for {symbol} on {dayCursor:yyyy-MM-dd}");
                return snapshots;
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase))
            {
                Trace.WriteLine($"Dhan historical rate limited for {symbol} on {dayCursor:yyyy-MM-dd}");
                return snapshots;
            }

            if (response is null || response.Close.Count == 0)
            {
                dayCursor = dayCursor.AddDays(1);
                continue;
            }

            var points = BuildSnapshots(symbol, instrument, response);
            snapshots.AddRange(points.Where(item => item.Timestamp >= from && item.Timestamp <= to));
            dayCursor = dayCursor.AddDays(1);
        }

        return snapshots
            .OrderBy(item => item.Timestamp)
            .ToArray();
    }

    private static IReadOnlyList<MarketSnapshot> BuildSnapshots(
        string symbol,
        InstrumentSummary instrument,
        DhanIntradayHistoricalResponse response)
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

            var timestamp = ConvertTimestamp(response, i);
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

    private static DateTimeOffset ConvertTimestamp(DhanHistoricalResponse response, int index)
    {
        if (response.Timestamp.Count > index)
        {
            var epochSeconds = response.Timestamp[index];
            if (epochSeconds > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToLocalTime();
            }
        }

        return DateTimeOffset.UnixEpoch.AddMinutes(index).ToLocalTime();
    }

    private static DateTimeOffset ConvertTimestamp(DhanIntradayHistoricalResponse response, int index)
    {
        if (response.Timestamp.Count > index)
        {
            var epochSeconds = response.Timestamp[index];
            if (epochSeconds > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToLocalTime();
            }
        }

        return DateTimeOffset.UnixEpoch.AddMinutes(index).ToLocalTime();
    }

    private static string? GetHistoricalInstrumentType(InstrumentSummary instrument)
    {
        return instrument.Segment switch
        {
            MarketSegment.Nifty or MarketSegment.BankNifty or MarketSegment.Sensex or MarketSegment.FinNifty => "INDEX",
            MarketSegment.Stock => "EQUITY",
            _ => null
        };
    }
}
