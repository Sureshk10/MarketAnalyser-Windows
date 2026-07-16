using MarketAnalyser.Core.Configuration;
using MarketAnalyser.Core.Dhan;

namespace MarketAnalyser.Core.Orders;

public static class OrderBrokerFactory
{
    public static IOrderBroker Create(AppOptions options, HttpClient httpClient)
    {
        var broker = ParseBroker(options.Orders.Broker);
        return broker switch
        {
            BrokerKind.Dhan => new DhanOrderBroker(httpClient, options.Dhan),
            BrokerKind.Kotak => new UnsupportedOrderBroker(BrokerKind.Kotak),
            BrokerKind.Religare => new UnsupportedOrderBroker(BrokerKind.Religare),
            _ => new UnsupportedOrderBroker(BrokerKind.Unknown)
        };
    }

    private static BrokerKind ParseBroker(string broker)
    {
        return Enum.TryParse<BrokerKind>(broker, true, out var parsed)
            ? parsed
            : BrokerKind.Unknown;
    }
}

internal sealed class UnsupportedOrderBroker(BrokerKind kind) : IOrderBroker
{
    public BrokerKind Kind => kind;

    public Task<OrderResponse> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"{kind} order service is not implemented yet.");
    }

    public Task<OrderResponse> ModifyOrderAsync(ModifyOrderRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"{kind} order service is not implemented yet.");
    }

    public Task<OrderResponse> CancelOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"{kind} order service is not implemented yet.");
    }

    public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"{kind} order service is not implemented yet.");
    }

    public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"{kind} order service is not implemented yet.");
    }

    public Task<IReadOnlyList<HoldingInfo>> GetHoldingsAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"{kind} order service is not implemented yet.");
    }

    public Task<OrderResponse> ExitAllPositionsAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"{kind} order service is not implemented yet.");
    }
}
