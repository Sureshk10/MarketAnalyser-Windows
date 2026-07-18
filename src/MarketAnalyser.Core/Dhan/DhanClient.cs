using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketAnalyser.Core.Configuration;

namespace MarketAnalyser.Core.Dhan;

public sealed class DhanClient(HttpClient httpClient, DhanOptions options)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.ClientId) &&
        !string.IsNullOrWhiteSpace(options.AccessToken);

    public async Task<DhanExpiryListResponse?> GetExpiryListAsync(
        int underlyingScrip,
        string underlyingSeg,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        using var message = CreateRequest(HttpMethod.Post, "optionchain/expirylist");
        message.Content = JsonContent.Create(new
        {
            UnderlyingScrip = underlyingScrip,
            UnderlyingSeg = underlyingSeg
        });

        return await SendAsync<DhanExpiryListResponse>(message, cancellationToken);
    }

    public async Task<DhanOptionChainResponse?> GetOptionChainAsync(
        DhanOptionChainRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        using var message = CreateRequest(HttpMethod.Post, "optionchain");
        message.Content = JsonContent.Create(new
        {
            UnderlyingScrip = request.UnderlyingScrip,
            UnderlyingSeg = request.UnderlyingSeg,
            Expiry = request.Expiry.ToString("yyyy-MM-dd")
        });

        return await SendAsync<DhanOptionChainResponse>(message, cancellationToken);
    }

    public async Task<DhanHistoricalResponse?> GetDailyHistoricalAsync(
        DhanHistoricalRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        using var message = CreateRequest(HttpMethod.Post, "charts/historical");
        message.Content = JsonContent.Create(new
        {
            securityId = request.SecurityId.ToString(),
            exchangeSegment = request.ExchangeSegment,
            instrument = request.Instrument,
            expiryCode = 0,
            oi = false,
            fromDate = request.FromDate.ToString("yyyy-MM-dd"),
            toDate = request.ToDate.ToString("yyyy-MM-dd")
        });

        var body = await SendRawAsync(message, cancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        return new DhanHistoricalResponse(
            ReadDecimalArray(root, "close"),
            ReadLongArray(root, "timestamp"));
    }

    public async Task<DhanIntradayHistoricalResponse?> GetIntradayHistoricalAsync(
        DhanIntradayHistoricalRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        using var message = CreateRequest(HttpMethod.Post, "charts/intraday");
        message.Content = JsonContent.Create(new
        {
            securityId = request.SecurityId.ToString(),
            exchangeSegment = request.ExchangeSegment,
            instrument = request.Instrument,
            interval = request.Interval.ToString(CultureInfo.InvariantCulture),
            oi = request.Oi,
            fromDate = request.FromDate.ToString("yyyy-MM-dd HH:mm:ss"),
            toDate = request.ToDate.ToString("yyyy-MM-dd HH:mm:ss")
        });

        var body = await SendRawAsync(message, cancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        return new DhanIntradayHistoricalResponse(
            ReadDecimalArray(root, "open"),
            ReadDecimalArray(root, "high"),
            ReadDecimalArray(root, "low"),
            ReadDecimalArray(root, "close"),
            ReadLongArray(root, "volume"),
            ReadLongArray(root, "timestamp"),
            ReadLongArray(root, "open_interest"));
    }

    public async Task<string?> GetFullQuoteAsync(
        DhanMarketQuoteRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        using var message = CreateRequest(HttpMethod.Post, "marketfeed/quote");
        message.Content = JsonContent.Create(new Dictionary<string, int[]>
        {
            [request.ExchangeSegment] = [request.SecurityId]
        });

        return await SendRawAsync(message, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var uri = new Uri($"{options.RestBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}");
        var message = new HttpRequestMessage(method, uri);
        message.Headers.TryAddWithoutValidation("access-token", options.AccessToken);
        message.Headers.TryAddWithoutValidation("client-id", options.ClientId);
        return message;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        var body = await SendRawAsync(message, cancellationToken);
        return JsonSerializer.Deserialize<T>(body);
    }

    private async Task<string> SendRawAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var trimmed = body.AsSpan().TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            var preview = body.Length <= 160 ? body : body[..160];
            throw new InvalidOperationException($"Dhan returned a non-JSON response. Preview: {preview}");
        }

        return body;
    }

    private static IReadOnlyList<decimal> ReadDecimalArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<decimal>();
        foreach (var item in array.EnumerateArray())
        {
            if (TryReadDecimal(item, out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static IReadOnlyList<long> ReadLongArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<long>();
        foreach (var item in array.EnumerateArray())
        {
            if (TryReadLong(item, out var value))
            {
                values.Add(value);
            }
        }

        return values;
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
}

public sealed record DhanOptionChainRequest(int UnderlyingScrip, string UnderlyingSeg, DateOnly Expiry);

public sealed record DhanHistoricalRequest(
    int SecurityId,
    string ExchangeSegment,
    string Instrument,
    DateOnly FromDate,
    DateOnly ToDate);

public sealed record DhanIntradayHistoricalRequest(
    int SecurityId,
    string ExchangeSegment,
    string Instrument,
    int Interval,
    bool Oi,
    DateTimeOffset FromDate,
    DateTimeOffset ToDate);

public sealed record DhanMarketQuoteRequest(
    string ExchangeSegment,
    int SecurityId);

public sealed record DhanHistoricalResponse(
    [property: JsonPropertyName("close")]
    [property: JsonConverter(typeof(DecimalListJsonConverter))]
    IReadOnlyList<decimal> Close,
    [property: JsonPropertyName("timestamp")]
    [property: JsonConverter(typeof(LongListJsonConverter))]
    IReadOnlyList<long> Timestamp);

public sealed record DhanIntradayHistoricalResponse(
    [property: JsonPropertyName("open")]
    [property: JsonConverter(typeof(DecimalListJsonConverter))]
    IReadOnlyList<decimal> Open,
    [property: JsonPropertyName("high")]
    [property: JsonConverter(typeof(DecimalListJsonConverter))]
    IReadOnlyList<decimal> High,
    [property: JsonPropertyName("low")]
    [property: JsonConverter(typeof(DecimalListJsonConverter))]
    IReadOnlyList<decimal> Low,
    [property: JsonPropertyName("close")]
    [property: JsonConverter(typeof(DecimalListJsonConverter))]
    IReadOnlyList<decimal> Close,
    [property: JsonPropertyName("volume")]
    [property: JsonConverter(typeof(LongListJsonConverter))]
    IReadOnlyList<long> Volume,
    [property: JsonPropertyName("timestamp")]
    [property: JsonConverter(typeof(LongListJsonConverter))]
    IReadOnlyList<long> Timestamp,
    [property: JsonPropertyName("open_interest")]
    [property: JsonConverter(typeof(LongListJsonConverter))]
    IReadOnlyList<long> OpenInterest);

public sealed record DhanExpiryListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<DateOnly> Data,
    [property: JsonPropertyName("status")] string Status);

public sealed record DhanOptionChainResponse(
    [property: JsonPropertyName("data")] DhanOptionChainData Data,
    [property: JsonPropertyName("status")] string Status);

public sealed record DhanOptionChainData(
    [property: JsonPropertyName("last_price")] decimal LastPrice,
    [property: JsonPropertyName("oc")] IReadOnlyDictionary<string, DhanOptionStrike> OptionChain);

public sealed record DhanOptionStrike(
    [property: JsonPropertyName("ce")] DhanOptionLeg? Call,
    [property: JsonPropertyName("pe")] DhanOptionLeg? Put);

public sealed record DhanOptionLeg(
    [property: JsonPropertyName("average_price")] decimal AveragePrice,
    [property: JsonPropertyName("greeks")] DhanGreeks? Greeks,
    [property: JsonPropertyName("implied_volatility")] decimal ImpliedVolatility,
    [property: JsonPropertyName("last_price")] decimal LastPrice,
    [property: JsonPropertyName("oi")] long OpenInterest,
    [property: JsonPropertyName("previous_close_price")] decimal PreviousClosePrice,
    [property: JsonPropertyName("previous_oi")] long PreviousOpenInterest,
    [property: JsonPropertyName("previous_volume")] long PreviousVolume,
    [property: JsonPropertyName("security_id")] int SecurityId,
    [property: JsonPropertyName("top_ask_price")] decimal TopAskPrice,
    [property: JsonPropertyName("top_ask_quantity")] long TopAskQuantity,
    [property: JsonPropertyName("top_bid_price")] decimal TopBidPrice,
    [property: JsonPropertyName("top_bid_quantity")] long TopBidQuantity,
    [property: JsonPropertyName("volume")] long Volume);

public sealed record DhanGreeks(
    [property: JsonPropertyName("delta")] decimal Delta,
    [property: JsonPropertyName("theta")] decimal Theta,
    [property: JsonPropertyName("gamma")] decimal Gamma,
    [property: JsonPropertyName("vega")] decimal Vega);

internal sealed class DecimalListJsonConverter : JsonConverter<IReadOnlyList<decimal>>
{
    public override IReadOnlyList<decimal> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected an array of decimal values.");
        }

        var values = new List<decimal>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return values;
            }

            values.Add(ReadDecimal(ref reader));
        }

        throw new JsonException("Unexpected end while reading decimal array.");
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<decimal> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteNumberValue(item);
        }

        writer.WriteEndArray();
    }

    private static decimal ReadDecimal(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetDecimal(out var value) => value,
            JsonTokenType.String when decimal.TryParse(reader.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) => value,
            JsonTokenType.Null => 0,
            _ => throw new JsonException($"Cannot convert {reader.TokenType} to decimal.")
        };
    }
}

internal sealed class LongListJsonConverter : JsonConverter<IReadOnlyList<long>>
{
    public override IReadOnlyList<long> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected an array of long values.");
        }

        var values = new List<long>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return values;
            }

            values.Add(ReadLong(ref reader));
        }

        throw new JsonException("Unexpected end while reading long array.");
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<long> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteNumberValue(item);
        }

        writer.WriteEndArray();
    }

    private static long ReadLong(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt64(out var value) => value,
            JsonTokenType.String when long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            JsonTokenType.Null => 0,
            _ => throw new JsonException($"Cannot convert {reader.TokenType} to long.")
        };
    }
}
