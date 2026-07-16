namespace MarketAnalyser.Core.Orders;

public enum BrokerKind
{
    Dhan,
    Kotak,
    Religare,
    Unknown
}

public enum OrderSide
{
    Buy,
    Sell
}

public enum OrderProduct
{
    Intraday,
    Delivery,
    Cover,
    Bracket,
    Margin
}

public enum OrderType
{
    Market,
    Limit,
    StopMarket,
    StopLimit
}

public enum OrderStatus
{
    Pending,
    Open,
    PartialFill,
    Filled,
    Cancelled,
    Rejected,
    Unknown
}

public sealed record OrderRequest(
    string Symbol,
    string ExchangeSegment,
    string SecurityId,
    OrderSide Side,
    int Quantity,
    OrderProduct Product,
    OrderType OrderType,
    decimal? Price = null,
    decimal? TriggerPrice = null,
    string? CorrelationId = null);

public sealed record ModifyOrderRequest(
    string OrderId,
    int Quantity,
    decimal? Price = null,
    decimal? TriggerPrice = null);

public sealed record OrderResponse(
    string OrderId,
    OrderStatus Status,
    string Message = "",
    string? BrokerOrderId = null);

public sealed record OrderInfo(
    string OrderId,
    string Symbol,
    string SecurityId,
    OrderSide Side,
    int Quantity,
    int FilledQuantity,
    decimal AveragePrice,
    OrderStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt = null,
    string? RejectionReason = null);

public sealed record PositionInfo(
    string Symbol,
    string SecurityId,
    int Quantity,
    decimal AveragePrice,
    decimal LastPrice,
    decimal Pnl,
    DateTimeOffset UpdatedAt);

public interface IOrderBroker
{
    BrokerKind Kind { get; }

    Task<OrderResponse> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken);

    Task<OrderResponse> ModifyOrderAsync(ModifyOrderRequest request, CancellationToken cancellationToken);

    Task<OrderResponse> CancelOrderAsync(string orderId, CancellationToken cancellationToken);

    Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<HoldingInfo>> GetHoldingsAsync(CancellationToken cancellationToken);

    Task<OrderResponse> ExitAllPositionsAsync(CancellationToken cancellationToken);
}

public sealed record HoldingInfo(
    string Exchange,
    string TradingSymbol,
    string SecurityId,
    string Isin,
    int TotalQty,
    int AvailableQty,
    decimal AvgCostPrice,
    decimal LastTradedPrice);
