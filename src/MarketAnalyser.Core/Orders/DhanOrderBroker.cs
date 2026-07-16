using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketAnalyser.Core.Configuration;
using MarketAnalyser.Core.Dhan;

namespace MarketAnalyser.Core.Orders;

public sealed class DhanOrderBroker(HttpClient httpClient, DhanOptions options) : IOrderBroker
{
    private readonly HttpClient httpClient = httpClient;
    private readonly DhanOptions options = options;

    public BrokerKind Kind => BrokerKind.Dhan;

    public async Task<OrderResponse> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken)
    {
        var payload = new
        {
            dhanClientId = options.ClientId,
            correlationId = request.CorrelationId ?? Guid.NewGuid().ToString("N"),
            transactionType = request.Side == OrderSide.Buy ? "BUY" : "SELL",
            exchangeSegment = request.ExchangeSegment,
            productType = MapProductType(request.Product),
            orderType = MapOrderType(request.OrderType),
            validity = "DAY",
            securityId = request.SecurityId,
            quantity = request.Quantity,
            disclosedQuantity = 0,
            price = request.Price ?? 0m,
            triggerPrice = request.TriggerPrice ?? 0m,
            afterMarketOrder = false,
            amoTime = "OPEN",
            boProfitValue = 0m,
            boStopLossValue = 0m
        };

        var response = await SendAsync<DhanOrderActionResponse>(
            HttpMethod.Post,
            "orders",
            payload,
            cancellationToken);

        return new OrderResponse(
            response.OrderId ?? string.Empty,
            MapOrderStatus(response.OrderStatus),
            response.OrderStatus ?? string.Empty);
    }

    public async Task<OrderResponse> ModifyOrderAsync(ModifyOrderRequest request, CancellationToken cancellationToken)
    {
        var payload = new
        {
            dhanClientId = options.ClientId,
            orderId = request.OrderId,
            orderType = "LIMIT",
            legName = "ENTRY_LEG",
            quantity = request.Quantity,
            price = request.Price ?? 0m,
            disclosedQuantity = 0,
            triggerPrice = request.TriggerPrice ?? 0m,
            validity = "DAY"
        };

        var response = await SendAsync<DhanOrderActionResponse>(
            HttpMethod.Put,
            $"orders/{request.OrderId}",
            payload,
            cancellationToken);

        return new OrderResponse(
            response.OrderId ?? request.OrderId,
            MapOrderStatus(response.OrderStatus),
            response.OrderStatus ?? string.Empty);
    }

    public async Task<OrderResponse> CancelOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        var response = await SendAsync<DhanOrderActionResponse>(
            HttpMethod.Delete,
            $"orders/{orderId}",
            null,
            cancellationToken);

        return new OrderResponse(
            response.OrderId ?? orderId,
            MapOrderStatus(response.OrderStatus),
            response.OrderStatus ?? string.Empty);
    }

    public async Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(CancellationToken cancellationToken)
    {
        var items = await SendAsync<List<DhanOrderRecord>>(HttpMethod.Get, "orders", null, cancellationToken);
        return (items ?? [])
            .Select(ToOrderInfo)
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
    }

    public async Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(CancellationToken cancellationToken)
    {
        var items = await SendAsync<List<DhanPositionRecord>>(HttpMethod.Get, "positions", null, cancellationToken);
        return (items ?? [])
            .Select(ToPositionInfo)
            .OrderByDescending(item => item.UpdatedAt)
            .ToArray();
    }

    public async Task<IReadOnlyList<HoldingInfo>> GetHoldingsAsync(CancellationToken cancellationToken)
    {
        var items = await SendAsync<List<DhanHoldingRecord>>(HttpMethod.Get, "holdings", null, cancellationToken);
        return (items ?? [])
            .Select(item => new HoldingInfo(
                item.Exchange ?? string.Empty,
                item.TradingSymbol ?? string.Empty,
                item.SecurityId ?? string.Empty,
                item.Isin ?? string.Empty,
                item.TotalQty,
                item.AvailableQty,
                item.AvgCostPrice,
                item.LastTradedPrice))
            .OrderByDescending(item => item.TotalQty)
            .ToArray();
    }

    public async Task<OrderResponse> ExitAllPositionsAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync<DhanExitPositionsResponse>(HttpMethod.Delete, "positions", null, cancellationToken);
        return new OrderResponse(
            string.Empty,
            MapOrderStatus(response.Status),
            response.Message ?? string.Empty);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        using var message = CreateRequest(method, path);
        if (payload is not null)
        {
            message.Content = JsonContent.Create(payload);
        }

        using var response = await httpClient.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<T>(body, Json.Options);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var uri = new Uri($"{options.RestBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}");
        var message = new HttpRequestMessage(method, uri);
        message.Headers.TryAddWithoutValidation("access-token", options.AccessToken);
        message.Headers.TryAddWithoutValidation("client-id", options.ClientId);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        return message;
    }

    private static OrderStatus MapOrderStatus(string? status)
    {
        return status?.ToUpperInvariant() switch
        {
            "TRANSIT" => OrderStatus.Pending,
            "PENDING" => OrderStatus.Open,
            "PART_TRADED" => OrderStatus.PartialFill,
            "TRADED" => OrderStatus.Filled,
            "CANCELLED" => OrderStatus.Cancelled,
            "REJECTED" => OrderStatus.Rejected,
            "EXPIRED" => OrderStatus.Rejected,
            _ => OrderStatus.Unknown
        };
    }

    private static string MapProductType(OrderProduct product)
    {
        return product switch
        {
            OrderProduct.Intraday => "INTRADAY",
            OrderProduct.Delivery => "CNC",
            OrderProduct.Cover => "CO",
            OrderProduct.Bracket => "BO",
            OrderProduct.Margin => "MARGIN",
            _ => "INTRADAY"
        };
    }

    private static string MapOrderType(OrderType orderType)
    {
        return orderType switch
        {
            OrderType.Market => "MARKET",
            OrderType.Limit => "LIMIT",
            OrderType.StopMarket => "STOP_LOSS_MARKET",
            OrderType.StopLimit => "STOP_LOSS",
            _ => "MARKET"
        };
    }

    private static OrderInfo ToOrderInfo(DhanOrderRecord record)
    {
        return new OrderInfo(
            record.OrderId ?? string.Empty,
            record.TradingSymbol ?? record.SecurityId ?? string.Empty,
            record.SecurityId ?? string.Empty,
            string.Equals(record.TransactionType, "BUY", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell,
            record.Quantity,
            record.FilledQty,
            record.AverageTradedPrice,
            MapOrderStatus(record.OrderStatus),
            ParseDateTime(record.CreateTime),
            ParseDateTime(record.UpdateTime),
            record.OmsErrorDescription);
    }

    private static PositionInfo ToPositionInfo(DhanPositionRecord record)
    {
        return new PositionInfo(
            record.TradingSymbol ?? record.SecurityId ?? string.Empty,
            record.SecurityId ?? string.Empty,
            record.NetQty,
            record.EffectiveAveragePrice,
            record.LastTradedPrice,
            record.Mtm,
            ParseDateTime(record.UpdateTime));
    }

    private static DateTimeOffset ParseDateTime(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }
}

public sealed record DhanOrderActionResponse(
    [property: JsonPropertyName("orderId")] string? OrderId,
    [property: JsonPropertyName("orderStatus")] string? OrderStatus);

public sealed record DhanOrderRecord(
    [property: JsonPropertyName("dhanClientId")] string? DhanClientId,
    [property: JsonPropertyName("orderId")] string? OrderId,
    [property: JsonPropertyName("exchangeOrderId")] string? ExchangeOrderId,
    [property: JsonPropertyName("correlationId")] string? CorrelationId,
    [property: JsonPropertyName("orderStatus")] string? OrderStatus,
    [property: JsonPropertyName("transactionType")] string? TransactionType,
    [property: JsonPropertyName("exchangeSegment")] string? ExchangeSegment,
    [property: JsonPropertyName("productType")] string? ProductType,
    [property: JsonPropertyName("orderType")] string? OrderType,
    [property: JsonPropertyName("validity")] string? Validity,
    [property: JsonPropertyName("tradingSymbol")] string? TradingSymbol,
    [property: JsonPropertyName("securityId")] string? SecurityId,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("disclosedQuantity")] int DisclosedQuantity,
    [property: JsonPropertyName("price")] decimal Price,
    [property: JsonPropertyName("triggerPrice")] decimal TriggerPrice,
    [property: JsonPropertyName("afterMarketOrder")] bool AfterMarketOrder,
    [property: JsonPropertyName("boProfitValue")] decimal BoProfitValue,
    [property: JsonPropertyName("boStopLossValue")] decimal BoStopLossValue,
    [property: JsonPropertyName("legName")] string? LegName,
    [property: JsonPropertyName("createTime")] string? CreateTime,
    [property: JsonPropertyName("updateTime")] string? UpdateTime,
    [property: JsonPropertyName("exchangeTime")] string? ExchangeTime,
    [property: JsonPropertyName("drvExpiryDate")] string? DrvExpiryDate,
    [property: JsonPropertyName("drvOptionType")] string? DrvOptionType,
    [property: JsonPropertyName("drvStrikePrice")] decimal DrvStrikePrice,
    [property: JsonPropertyName("omsErrorCode")] string? OmsErrorCode,
    [property: JsonPropertyName("omsErrorDescription")] string? OmsErrorDescription,
    [property: JsonPropertyName("algoId")] string? AlgoId,
    [property: JsonPropertyName("remainingQuantity")] int RemainingQuantity,
    [property: JsonPropertyName("averageTradedPrice")] decimal AverageTradedPrice,
    [property: JsonPropertyName("filledQty")] int FilledQty);

public sealed record DhanPositionRecord(
    [property: JsonPropertyName("tradingSymbol")] string? TradingSymbol,
    [property: JsonPropertyName("securityId")] string? SecurityId,
    [property: JsonPropertyName("netQty")] int NetQty,
    [property: JsonPropertyName("buyAvg")] decimal BuyAvg,
    [property: JsonPropertyName("sellAvg")] decimal SellAvg,
    [property: JsonPropertyName("averagePrice")] decimal BrokerAveragePrice,
    [property: JsonPropertyName("lastTradedPrice")] decimal LastTradedPrice,
    [property: JsonPropertyName("mtm")] decimal Mtm,
    [property: JsonPropertyName("updateTime")] string? UpdateTime)
{
    [JsonIgnore]
    public decimal EffectiveAveragePrice => BrokerAveragePrice == 0 ? (BuyAvg == 0 ? SellAvg : BuyAvg) : BrokerAveragePrice;
}

public sealed record DhanHoldingRecord(
    [property: JsonPropertyName("exchange")] string? Exchange,
    [property: JsonPropertyName("tradingSymbol")] string? TradingSymbol,
    [property: JsonPropertyName("securityId")] string? SecurityId,
    [property: JsonPropertyName("isin")] string? Isin,
    [property: JsonPropertyName("totalQty")] int TotalQty,
    [property: JsonPropertyName("dpQty")] int DpQty,
    [property: JsonPropertyName("t1Qty")] int T1Qty,
    [property: JsonPropertyName("mtf_t1_qty")] int MtfT1Qty,
    [property: JsonPropertyName("mtf_qty")] int MtfQty,
    [property: JsonPropertyName("availableQty")] int AvailableQty,
    [property: JsonPropertyName("collateralQty")] int CollateralQty,
    [property: JsonPropertyName("avgCostPrice")] decimal AvgCostPrice,
    [property: JsonPropertyName("lastTradedPrice")] decimal LastTradedPrice);

public sealed record DhanExitPositionsResponse(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("status")] string? Status);
