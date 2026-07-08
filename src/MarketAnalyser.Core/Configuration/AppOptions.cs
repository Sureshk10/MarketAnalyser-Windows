using System.Text.Json;
using System.Text.Json.Serialization;
using MarketAnalyser.Core.Market;

namespace MarketAnalyser.Core.Configuration;

public sealed class AppOptions
{
    public DataSourceOptions DataSource { get; init; } = new();

    public DhanOptions Dhan { get; init; } = new();

    public InstrumentCatalogOptions Instruments { get; init; } = new();

    public static AppOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppOptions();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppOptions>(json, Json.Options) ?? new AppOptions();
    }
}

public sealed class DataSourceOptions
{
    public string Mode { get; init; } = "Embedded";

    public string RestBaseUrl { get; init; } = "http://localhost:5265";

    public string HistoricalRestBaseUrl { get; init; } = string.Empty;

    public string HistoricalApiKey { get; init; } = string.Empty;
}

public sealed class DhanOptions
{
    public string ClientId { get; init; } = string.Empty;

    public string AccessToken { get; init; } = string.Empty;

    public string RestBaseUrl { get; init; } = "https://api.dhan.co/v2";

    public string FeedUrl { get; init; } = "wss://api-feed.dhan.co";

    public bool UseMockData { get; init; } = true;

    public bool UseWebSocket { get; init; } = true;
}

public sealed class InstrumentCatalogOptions
{
    public bool UseDhanScripMaster { get; init; } = true;

    public string ScripMasterUrl { get; init; } = "https://images.dhan.co/api-data/api-scrip-master.csv";

    public string ScripMasterFallbackPath { get; init; } = string.Empty;

    public int MaxDynamicStocks { get; init; } = 250;

    public int MaxDynamicCommodities { get; init; } = 40;

    public IReadOnlyList<InstrumentOptions> Items { get; init; } =
    [
        new()
        {
            Symbol = "NIFTY",
            DisplayName = "NIFTY 50",
            Segment = MarketSegment.Nifty,
            UnderlyingSecurityId = 13,
            UnderlyingSegment = "IDX_I",
            DerivativeSegment = "NSE_FNO",
            StrikeInterval = 50,
            IsFavorite = true
        },
        new()
        {
            Symbol = "SENSEX",
            DisplayName = "SENSEX",
            Segment = MarketSegment.Sensex,
            UnderlyingSecurityId = 51,
            UnderlyingSegment = "IDX_I",
            DerivativeSegment = "BSE_FNO",
            StrikeInterval = 100,
            IsFavorite = true
        },
        new()
        {
            Symbol = "BANKNIFTY",
            DisplayName = "NIFTY BANK",
            Segment = MarketSegment.BankNifty,
            UnderlyingSecurityId = 25,
            UnderlyingSegment = "IDX_I",
            DerivativeSegment = "NSE_FNO",
            StrikeInterval = 100
        },
        new()
        {
            Symbol = "FINNIFTY",
            DisplayName = "NIFTY FIN SERVICE",
            Segment = MarketSegment.FinNifty,
            UnderlyingSecurityId = 27,
            UnderlyingSegment = "IDX_I",
            DerivativeSegment = "NSE_FNO",
            StrikeInterval = 50
        }
    ];
}

public sealed class InstrumentOptions
{
    public string Symbol { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter<MarketSegment>))]
    public MarketSegment Segment { get; init; } = MarketSegment.Stock;

    public int UnderlyingSecurityId { get; init; }

    public string UnderlyingSegment { get; init; } = string.Empty;

    public string DerivativeSegment { get; init; } = string.Empty;

    public decimal StrikeInterval { get; init; }

    public bool IsFavorite { get; init; }

    public bool IsEnabled { get; init; } = true;
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
