using System.Globalization;
using System.Text.Json;
using MarketAnalyser.Core.Market;

namespace MarketAnalyser.Core.Dhan;

internal static class DhanMarketQuoteParser
{
    public static MarketDepthSnapshot? TryParseDepth(
        string json,
        string expectedExchangeSegment,
        int expectedSecurityId)
    {
        using var document = JsonDocument.Parse(json);
        return TryParseDepth(document.RootElement, expectedExchangeSegment, expectedSecurityId);
    }

    public static decimal? TryParseSpotPrice(
        string json,
        string expectedExchangeSegment,
        int expectedSecurityId)
    {
        using var document = JsonDocument.Parse(json);
        return TryParseSpotPrice(document.RootElement, expectedExchangeSegment, expectedSecurityId);
    }

    public static MarketDepthSnapshot? TryParseDepth(
        JsonElement root,
        string expectedExchangeSegment,
        int expectedSecurityId)
    {
        var candidates = EnumerateObjects(root).ToArray();

        foreach (var candidate in candidates.Where(candidate => MatchesInstrument(candidate, expectedExchangeSegment, expectedSecurityId)))
        {
            if (TryParseDepthObject(candidate, out var depth))
            {
                return depth;
            }
        }

        foreach (var candidate in candidates)
        {
            if (TryParseDepthObject(candidate, out var depth))
            {
                return depth;
            }
        }

        return null;
    }

    public static decimal? TryParseSpotPrice(
        JsonElement root,
        string expectedExchangeSegment,
        int expectedSecurityId)
    {
        var candidates = EnumerateObjects(root).ToArray();

        foreach (var candidate in candidates.Where(candidate => MatchesInstrument(candidate, expectedExchangeSegment, expectedSecurityId)))
        {
            if (TryReadDecimalProperty(candidate, out var price, "ltp", "last_price", "lastPrice", "price") && price > 0)
            {
                return price;
            }
        }

        foreach (var candidate in candidates)
        {
            if (TryReadDecimalProperty(candidate, out var price, "ltp", "last_price", "lastPrice", "price") && price > 0)
            {
                return price;
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;

            foreach (var property in element.EnumerateObject())
            {
                foreach (var nested in EnumerateObjects(property.Value))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateObjects(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool MatchesInstrument(
        JsonElement element,
        string expectedExchangeSegment,
        int expectedSecurityId)
    {
        if (TryGetPropertyIgnoreCase(element, "security_id", out var securityIdElement) ||
            TryGetPropertyIgnoreCase(element, "securityId", out securityIdElement))
        {
            var securityIdText = ReadString(securityIdElement);
            if (!string.IsNullOrWhiteSpace(securityIdText) &&
                !string.Equals(securityIdText, expectedSecurityId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (TryGetPropertyIgnoreCase(element, "exchange_segment", out var exchangeSegmentElement) ||
            TryGetPropertyIgnoreCase(element, "exchangeSegment", out exchangeSegmentElement))
        {
            var exchangeSegment = ReadString(exchangeSegmentElement);
            if (!string.IsNullOrWhiteSpace(exchangeSegment) &&
                !string.Equals(exchangeSegment, expectedExchangeSegment, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseDepthObject(JsonElement element, out MarketDepthSnapshot? depth)
    {
        depth = null;

        if (TryParseCombinedDepth(element, out depth))
        {
            return true;
        }

        if (TryParseSeparateDepth(element, out depth))
        {
            return true;
        }

        return false;
    }

    private static bool TryParseCombinedDepth(JsonElement element, out MarketDepthSnapshot? depth)
    {
        depth = null;
        if (!TryGetPropertyIgnoreCase(element, "depth", out var depthElement) || depthElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var bids = new List<MarketDepthLevelSnapshot>();
        var asks = new List<MarketDepthLevelSnapshot>();
        foreach (var entry in depthElement.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object)
            {
                if (TryReadCombinedDepthRow(entry, out var bidLevel, out var askLevel))
                {
                    if (bidLevel is not null)
                    {
                        bids.Add(bidLevel);
                    }

                    if (askLevel is not null)
                    {
                        asks.Add(askLevel);
                    }

                    continue;
                }

                if (TryReadDepthLevel(entry, "bid", out var bidOnly))
                {
                    bids.Add(bidOnly);
                }

                if (TryReadDepthLevel(entry, "ask", out var askOnly))
                {
                    asks.Add(askOnly);
                }
            }
            else if (entry.ValueKind == JsonValueKind.Array)
            {
                if (TryReadDepthLevel(entry, null, out var level))
                {
                    bids.Add(level);
                }
            }
        }

        if (bids.Count == 0 && asks.Count == 0)
        {
            return false;
        }

        depth = new MarketDepthSnapshot(
            bids.Take(5).ToArray(),
            asks.Take(5).ToArray());
        return true;
    }

    private static bool TryParseSeparateDepth(JsonElement element, out MarketDepthSnapshot? depth)
    {
        depth = null;

        var bidElement = GetFirstProperty(element, "bids", "buy", "bid_depth", "bidDepth", "buy_depth", "buyDepth");
        var askElement = GetFirstProperty(element, "asks", "sell", "ask_depth", "askDepth", "sell_depth", "sellDepth");

        if (bidElement is null && askElement is null)
        {
            return false;
        }

        var bids = ParseDepthLevels(bidElement, "bid");
        var asks = ParseDepthLevels(askElement, "ask");
        if (bids.Count == 0 && asks.Count == 0)
        {
            return false;
        }

        depth = new MarketDepthSnapshot(
            bids.Take(5).ToArray(),
            asks.Take(5).ToArray());
        return true;
    }

    private static IReadOnlyList<MarketDepthLevelSnapshot> ParseDepthLevels(JsonElement? element, string sideHint)
    {
        if (element is null)
        {
            return [];
        }

        var levels = new List<MarketDepthLevelSnapshot>();
        switch (element.Value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var entry in element.Value.EnumerateArray())
                {
                    if (TryReadDepthLevel(entry, sideHint, out var level))
                    {
                        levels.Add(level);
                    }
                }
                break;
            case JsonValueKind.Object:
                if (TryReadDepthLevel(element.Value, sideHint, out var singleLevel))
                {
                    levels.Add(singleLevel);
                }
                break;
        }

        return levels;
    }

    private static bool TryReadCombinedDepthRow(
        JsonElement element,
        out MarketDepthLevelSnapshot? bidLevel,
        out MarketDepthLevelSnapshot? askLevel)
    {
        bidLevel = null;
        askLevel = null;

        if (!TryReadDecimalProperty(element, out var bidPrice, "bid_price", "buy_price", "bidPrice", "buyPrice", "bid", "buy") &&
            !TryReadDecimalProperty(element, out bidPrice, "price"))
        {
            bidPrice = 0;
        }

        if (!TryReadLongProperty(element, out var bidQuantity, "bid_quantity", "buy_quantity", "bidQty", "buyQty", "quantity"))
        {
            bidQuantity = 0;
        }

        if (!TryReadIntProperty(element, out var bidOrders, "bid_orders", "buy_orders", "bidOrders", "buyOrders", "orders", "order_count", "orderCount"))
        {
            bidOrders = 0;
        }

        if (bidPrice > 0 && bidQuantity > 0)
        {
            bidLevel = new MarketDepthLevelSnapshot(decimal.Round(bidPrice, 2), bidQuantity, bidOrders);
        }

        if (!TryReadDecimalProperty(element, out var askPrice, "ask_price", "sell_price", "askPrice", "sellPrice", "ask", "sell") &&
            !TryReadDecimalProperty(element, out askPrice, "price"))
        {
            askPrice = 0;
        }

        if (!TryReadLongProperty(element, out var askQuantity, "ask_quantity", "sell_quantity", "askQty", "sellQty", "quantity"))
        {
            askQuantity = 0;
        }

        if (!TryReadIntProperty(element, out var askOrders, "ask_orders", "sell_orders", "askOrders", "sellOrders", "orders", "order_count", "orderCount"))
        {
            askOrders = 0;
        }

        if (askPrice > 0 && askQuantity > 0)
        {
            askLevel = new MarketDepthLevelSnapshot(decimal.Round(askPrice, 2), askQuantity, askOrders);
        }

        return bidLevel is not null || askLevel is not null;
    }

    private static bool TryReadDepthLevel(
        JsonElement element,
        string? sideHint,
        out MarketDepthLevelSnapshot level)
    {
        level = default!;

        var price = 0m;
        var quantity = 0L;
        var orders = 0;

        if (TryReadDecimalProperty(element, out var explicitPrice, "price", "rate", "bid_price", "ask_price", "buy_price", "sell_price"))
        {
            price = decimal.Round(explicitPrice, 2);
        }

        if (TryReadLongProperty(element, out var explicitQuantity, "quantity", "qty", "bid_quantity", "ask_quantity", "buy_quantity", "sell_quantity"))
        {
            quantity = explicitQuantity;
        }

        if (TryReadIntProperty(element, out var explicitOrders, "orders", "order_count", "bid_orders", "ask_orders", "buy_orders", "sell_orders"))
        {
            orders = explicitOrders;
        }

        if (price <= 0 && quantity <= 0)
        {
            if (TryReadDecimalProperty(element, out var fallbackPrice, "ltp", "last_price"))
            {
                price = decimal.Round(fallbackPrice, 2);
            }

            if (TryReadLongProperty(element, out var fallbackQuantity, "volume"))
            {
                quantity = fallbackQuantity;
            }
        }

        if (price <= 0 || quantity < 0)
        {
            return false;
        }

        if (price == 0 && sideHint is not null)
        {
            return false;
        }

        level = new MarketDepthLevelSnapshot(price, quantity, orders);
        return true;
    }

    private static bool TryReadDecimalProperty(JsonElement element, out decimal value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var property) && TryReadDecimal(property, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryReadLongProperty(JsonElement element, out long value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var property) && TryReadLong(property, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryReadIntProperty(JsonElement element, out int value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var property) && TryReadInt(property, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static JsonElement? GetFirstProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var property))
            {
                return property;
            }
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            property = default;
            return false;
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static bool TryReadLong(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        value = 0;

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetInt32(out value))
            {
                return true;
            }

            if (element.TryGetInt64(out var longValue) &&
                longValue >= int.MinValue &&
                longValue <= int.MaxValue)
            {
                value = (int)longValue;
                return true;
            }
        }

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }

    private static string? ReadString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }
}
