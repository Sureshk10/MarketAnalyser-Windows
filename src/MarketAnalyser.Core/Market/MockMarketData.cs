namespace MarketAnalyser.Core.Market;

public static class MockMarketData
{
    private static readonly Random Random = new();

    public static MarketSnapshot CreateSnapshot(InstrumentSummary instrument, MarketSnapshot? previous)
    {
        var now = DateTimeOffset.UtcNow;
        var baseSpot = instrument.Symbol switch
        {
            "SENSEX" => 79_200m,
            "BANKNIFTY" => 52_800m,
            "FINNIFTY" => 23_650m,
            _ => 24_200m
        };
        var spot = previous is null
            ? baseSpot
            : Math.Max(instrument.StrikeInterval, previous.Spot + Random.Next(-18, 19));

        var atm = Math.Round(spot / instrument.StrikeInterval) * instrument.StrikeInterval;
        var strikes = Enumerable.Range(-10, 21)
            .Select(offset => CreateStrike(atm + offset * instrument.StrikeInterval, spot, instrument.StrikeInterval))
            .ToList();

        var totalCallOiChange = strikes.Sum(strike => strike.Call.OpenInterestChange);
        var totalPutOiChange = strikes.Sum(strike => strike.Put.OpenInterestChange);

        return new MarketSnapshot(
            instrument.Symbol,
            spot,
            previous is null ? 0 : decimal.Round(spot - previous.Spot, 2),
            now,
            strikes,
            EmbeddedMarketDataSource.CreateBreadth(strikes),
            EmbeddedMarketDataSource.AppendPoint(previous?.PriceSeries ?? [], now, spot),
            EmbeddedMarketDataSource.AppendPoint(previous?.OiChangeSeries ?? [], now, totalPutOiChange - totalCallOiChange),
            EmbeddedMarketDataSource.AppendStrikeOiHistory(previous?.StrikeOiChangeSeries ?? [], strikes, now));
    }

    private static OptionStrikeSnapshot CreateStrike(decimal strike, decimal spot, decimal strikeInterval)
    {
        var distance = Math.Abs(strike - spot) / strikeInterval;
        var callBias = strike < spot ? 1.25m : 0.78m;
        var putBias = strike > spot ? 1.25m : 0.78m;

        return new OptionStrikeSnapshot(
            strike,
            CreateLeg(strike, spot, distance, callBias, OptionSide.Call),
            CreateLeg(strike, spot, distance, putBias, OptionSide.Put),
            strike - strikeInterval * 0.8m,
            strike + strikeInterval * 0.8m);
    }

    private static OptionLegSnapshot CreateLeg(
        decimal strike,
        decimal spot,
        decimal distance,
        decimal bias,
        OptionSide side)
    {
        var intrinsic = side == OptionSide.Call
            ? Math.Max(0, spot - strike)
            : Math.Max(0, strike - spot);
        var timeValue = Math.Max(8, 140 - distance * 12);
        var lastPrice = decimal.Round(intrinsic + timeValue + Random.Next(-6, 7), 2);
        var openInterest = (long)((90_000 + Random.Next(0, 280_000)) * bias);
        var volume = (long)((12_000 + Random.Next(0, 90_000)) * bias);
        var oiChange = (long)((Random.Next(-35_000, 75_000)) * bias);

        return new OptionLegSnapshot(
            lastPrice,
            Random.Next(-25, 26),
            volume,
            openInterest,
            oiChange,
            decimal.Round(10 + distance * 0.9m + Random.Next(0, 60) / 10m, 2),
            side == OptionSide.Call ? decimal.Round(0.55m - distance * 0.035m, 4) : decimal.Round(-0.55m + distance * 0.035m, 4),
            decimal.Round(0.001m + Random.Next(0, 20) / 10000m, 5),
            decimal.Round(-8 - distance * 0.8m, 4),
            decimal.Round(3 + distance * 0.2m, 4));
    }
}
