namespace MarketAnalyser.Core.Dhan;

public sealed record DhanFeedPacket(
    byte ResponseCode,
    short MessageLength,
    byte ExchangeSegment,
    int SecurityId,
    decimal? LastPrice = null,
    long? Volume = null,
    long? OpenInterest = null,
    DateTimeOffset? LastTradeTime = null);
