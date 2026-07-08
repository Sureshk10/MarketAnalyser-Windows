using MarketAnalyser.Core.Dhan;

namespace MarketAnalyser.Core.Market;

public static class MarketSnapshotUpdater
{
    public static bool TryApply(OptionInstrumentRef instrument, DhanFeedPacket packet, MarketSnapshot current, out MarketSnapshot snapshot)
    {
        if (instrument.IsUnderlying)
        {
            return TryApplyUnderlying(current, packet, out snapshot);
        }

        var updatedStrikes = current.Strikes
            .Select(strike => strike.Strike == instrument.Strike ? UpdateStrike(strike, instrument.Side, packet) : strike)
            .ToList();

        if (updatedStrikes.SequenceEqual(current.Strikes))
        {
            snapshot = current;
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var updatedStrike = updatedStrikes.First(strike => strike.Strike == instrument.Strike);
        var totalCallOiChange = updatedStrikes.Sum(strike => strike.Call.OpenInterestChange);
        var totalPutOiChange = updatedStrikes.Sum(strike => strike.Put.OpenInterestChange);

        snapshot = current with
        {
            Timestamp = now,
            Strikes = updatedStrikes,
            Breadth = EmbeddedMarketDataSource.CreateBreadth(updatedStrikes),
            OiChangeSeries = EmbeddedMarketDataSource.AppendPoint(current.OiChangeSeries, now, totalPutOiChange - totalCallOiChange),
            StrikeOiChangeSeries = UpsertStrikeOiHistory(current.StrikeOiChangeSeries, updatedStrike, now)
        };

        return true;
    }

    private static bool TryApplyUnderlying(MarketSnapshot current, DhanFeedPacket packet, out MarketSnapshot snapshot)
    {
        if (packet.LastPrice is null || packet.LastPrice <= 0 || packet.LastPrice == current.Spot)
        {
            snapshot = current;
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var spot = packet.LastPrice.Value;
        snapshot = current with
        {
            Spot = spot,
            SpotChange = GetSpotChange(current, spot),
            Timestamp = now,
            PriceSeries = EmbeddedMarketDataSource.AppendPoint(current.PriceSeries, now, spot)
        };

        return true;
    }

    private static OptionStrikeSnapshot UpdateStrike(OptionStrikeSnapshot strike, OptionSide side, DhanFeedPacket packet)
    {
        return side == OptionSide.Call
            ? strike with { Call = UpdateLeg(strike.Call, packet) }
            : strike with { Put = UpdateLeg(strike.Put, packet) };
    }

    private static OptionLegSnapshot UpdateLeg(OptionLegSnapshot leg, DhanFeedPacket packet)
    {
        var lastPrice = packet.LastPrice is > 0 ? packet.LastPrice.Value : leg.LastPrice;
        var openInterest = packet.OpenInterest ?? leg.OpenInterest;
        var previousOpenInterest = leg.OpenInterest - leg.OpenInterestChange;

        return leg with
        {
            LastPrice = lastPrice,
            Volume = packet.Volume ?? leg.Volume,
            OpenInterest = openInterest,
            OpenInterestChange = previousOpenInterest > 0 ? openInterest - previousOpenInterest : leg.OpenInterestChange
        };
    }

    private static decimal GetSpotChange(MarketSnapshot current, decimal spot)
    {
        if (current.PreviousClose is not > 0)
        {
            return 0;
        }

        return decimal.Round(spot - current.PreviousClose.Value, 2);
    }

    private static IReadOnlyList<StrikeOiChangeSeries> UpsertStrikeOiHistory(
        IReadOnlyList<StrikeOiChangeSeries> existing,
        OptionStrikeSnapshot strike,
        DateTimeOffset time)
    {
        var updated = new StrikeOiChangeSeries(
            strike.Strike,
            EmbeddedMarketDataSource.AppendPoint(existing.FirstOrDefault(item => item.Strike == strike.Strike)?.Call ?? [], time, strike.Call.OpenInterestChange),
            EmbeddedMarketDataSource.AppendPoint(existing.FirstOrDefault(item => item.Strike == strike.Strike)?.Put ?? [], time, strike.Put.OpenInterestChange),
            EmbeddedMarketDataSource.AppendPoint(existing.FirstOrDefault(item => item.Strike == strike.Strike)?.Difference ?? [], time, strike.Put.OpenInterestChange - strike.Call.OpenInterestChange));

        return existing
            .Where(item => item.Strike != strike.Strike)
            .Append(updated)
            .OrderBy(item => item.Strike)
            .ToList();
    }
}
