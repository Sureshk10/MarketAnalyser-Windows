namespace MarketAnalyser.Core.Market;

public enum MarketSegment
{
    Nifty,
    BankNifty,
    Sensex,
    FinNifty,
    Stock,
    Commodity
}

public enum OptionSide
{
    Call,
    Put
}

public sealed record InstrumentSummary(
    string Symbol,
    string DisplayName,
    MarketSegment Segment,
    int UnderlyingSecurityId,
    string UnderlyingSegment,
    string DerivativeSegment,
    decimal StrikeInterval,
    bool IsFavorite);

public sealed record OptionLegSnapshot(
    decimal LastPrice,
    decimal Change,
    long Volume,
    long OpenInterest,
    long OpenInterestChange,
    decimal ImpliedVolatility,
    decimal Delta,
    decimal Gamma,
    decimal Theta,
    decimal Vega,
    decimal TopBidPrice = 0,
    long TopBidQuantity = 0,
    decimal TopAskPrice = 0,
    long TopAskQuantity = 0)
{
    public long DepthImbalance => TopBidQuantity - TopAskQuantity;

    public decimal MidPrice => TopBidPrice > 0 && TopAskPrice > 0
        ? decimal.Round((TopBidPrice + TopAskPrice) / 2m, 2)
        : LastPrice;

    public decimal Spread => TopBidPrice > 0 && TopAskPrice > 0
        ? decimal.Round(TopAskPrice - TopBidPrice, 2)
        : 0;
}

public sealed record OptionStrikeSnapshot(
    decimal Strike,
    OptionLegSnapshot Call,
    OptionLegSnapshot Put,
    decimal Support,
    decimal Resistance);

public sealed record MarketBreadth(
    decimal PutCallRatioOi,
    decimal PutCallRatioVolume,
    decimal CeVolumeShare,
    decimal PeVolumeShare,
    long TotalCallOi,
    long TotalPutOi,
    long TotalCallVolume,
    long TotalPutVolume);

public sealed record ChartPoint(DateTimeOffset Time, decimal Value);

public sealed record StrikeOiChangeSeries(
    decimal Strike,
    IReadOnlyList<ChartPoint> Call,
    IReadOnlyList<ChartPoint> Put,
    IReadOnlyList<ChartPoint> Difference);

public sealed record MarketSnapshot(
    string Symbol,
    decimal Spot,
    decimal SpotChange,
    DateTimeOffset Timestamp,
    IReadOnlyList<OptionStrikeSnapshot> Strikes,
    MarketBreadth Breadth,
    IReadOnlyList<ChartPoint> PriceSeries,
    IReadOnlyList<ChartPoint> OiChangeSeries,
    IReadOnlyList<StrikeOiChangeSeries> StrikeOiChangeSeries,
    decimal? PreviousClose = null,
    string PreviousCloseStatus = "");

public interface IMarketDataSource
{
    string Name { get; }

    Task<IReadOnlyList<InstrumentSummary>> GetInstrumentsAsync(CancellationToken cancellationToken);

    Task<MarketSnapshot> GetSnapshotAsync(string symbol, CancellationToken cancellationToken);
}
