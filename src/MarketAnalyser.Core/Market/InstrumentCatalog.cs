using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MarketAnalyser.Core.Configuration;

namespace MarketAnalyser.Core.Market;

public sealed partial class InstrumentCatalog(InstrumentCatalogOptions options)
{
    private readonly object gate = new();
    private IReadOnlyList<InstrumentSummary>? cached;

    public IReadOnlyList<InstrumentSummary> GetAll()
    {
        lock (gate)
        {
            cached ??= BuildCatalog();
            return cached;
        }
    }

    public InstrumentSummary? Find(string symbol)
    {
        return GetAll().FirstOrDefault(item =>
            string.Equals(item.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<InstrumentSummary> BuildCatalog()
    {
        var configured = GetConfiguredInstruments();
        if (!options.UseDhanScripMaster)
        {
            return configured;
        }

        try
        {
            var dynamicInstruments = LoadDynamicInstruments(configured);
            return configured
                .Concat(dynamicInstruments)
                .GroupBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(item => item.IsFavorite)
                .ThenBy(item => item.DisplayName)
                .ToList();
        }
        catch
        {
            return configured;
        }
    }

    private IReadOnlyList<InstrumentSummary> GetConfiguredInstruments()
    {
        var configured = options.Items
            .Where(item => item.IsEnabled)
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Symbol) &&
                !string.IsNullOrWhiteSpace(item.DisplayName) &&
                !string.IsNullOrWhiteSpace(item.UnderlyingSegment) &&
                !string.IsNullOrWhiteSpace(item.DerivativeSegment) &&
                item.UnderlyingSecurityId > 0 &&
                item.StrikeInterval > 0)
            .Select(item => new InstrumentSummary(
                item.Symbol.Trim().ToUpperInvariant(),
                item.DisplayName.Trim(),
                item.Segment,
                item.UnderlyingSecurityId,
                item.UnderlyingSegment.Trim(),
                item.DerivativeSegment.Trim(),
                item.StrikeInterval,
                item.IsFavorite))
            .GroupBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        return configured.Count > 0 ? configured : DefaultInstruments;
    }

    private IReadOnlyList<InstrumentSummary> LoadDynamicInstruments(IReadOnlyList<InstrumentSummary> configured)
    {
        using var stream = OpenScripMasterStream();
        using var reader = new StreamReader(stream);

        var header = reader.ReadLine();
        if (header is null)
        {
            return [];
        }

        var index = Csv.Split(header)
            .Select((name, i) => new { name, i })
            .ToDictionary(item => item.name, item => item.i, StringComparer.OrdinalIgnoreCase);

        var equities = new Dictionary<string, DhanEquityRow>(StringComparer.OrdinalIgnoreCase);
        var commodityFutures = new Dictionary<string, List<DhanFutureRow>>(StringComparer.OrdinalIgnoreCase);
        var optionGroups = new Dictionary<string, List<DhanOptionRow>>(StringComparer.OrdinalIgnoreCase);
        var commodityOptionGroups = new Dictionary<string, List<DhanOptionRow>>(StringComparer.OrdinalIgnoreCase);
        var configuredSymbols = configured.Select(item => item.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        while (reader.ReadLine() is { } line)
        {
            var row = Csv.Split(line);
            if (row.Count <= 1)
            {
                continue;
            }

            var exchange = Value(row, index, "SEM_EXM_EXCH_ID");
            var segment = Value(row, index, "SEM_SEGMENT");
            var instrument = Value(row, index, "SEM_INSTRUMENT_NAME");
            var tradingSymbol = Value(row, index, "SEM_TRADING_SYMBOL");
            var symbolName = Value(row, index, "SM_SYMBOL_NAME");

            if (exchange == "NSE" && segment == "E" && instrument == "EQUITY")
            {
                var series = Value(row, index, "SEM_SERIES");
                if (series != "EQ")
                {
                    continue;
                }

                if (int.TryParse(Value(row, index, "SEM_SMST_SECURITY_ID"), out var securityId))
                {
                    equities[tradingSymbol] = new DhanEquityRow(
                        tradingSymbol,
                        securityId,
                        Value(row, index, "SEM_CUSTOM_SYMBOL"));
                }
            }
            else if (exchange == "NSE" && segment == "D" && instrument == "OPTSTK")
            {
                var match = OptionSymbolRegex().Match(tradingSymbol);
                if (!match.Success)
                {
                    continue;
                }

                var symbol = match.Groups["symbol"].Value;
                if (configuredSymbols.Contains(symbol))
                {
                    continue;
                }

                if (!decimal.TryParse(Value(row, index, "SEM_STRIKE_PRICE"), NumberStyles.Number, CultureInfo.InvariantCulture, out var strike))
                {
                    continue;
                }

                if (!DateTime.TryParse(Value(row, index, "SEM_EXPIRY_DATE"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var expiry))
                {
                    continue;
                }

                if (!optionGroups.TryGetValue(symbol, out var rows))
                {
                    rows = [];
                    optionGroups[symbol] = rows;
                }

                rows.Add(new DhanOptionRow(symbol, strike, expiry));
            }
            else if (exchange == "MCX" && segment == "M" && instrument == "FUTCOM")
            {
                if (string.IsNullOrWhiteSpace(symbolName) || configuredSymbols.Contains(symbolName))
                {
                    continue;
                }

                if (!int.TryParse(Value(row, index, "SEM_SMST_SECURITY_ID"), out var securityId))
                {
                    continue;
                }

                if (!DateTime.TryParse(Value(row, index, "SEM_EXPIRY_DATE"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var expiry))
                {
                    continue;
                }

                if (!commodityFutures.TryGetValue(symbolName, out var rows))
                {
                    rows = [];
                    commodityFutures[symbolName] = rows;
                }

                rows.Add(new DhanFutureRow(symbolName, securityId, expiry, Value(row, index, "SEM_CUSTOM_SYMBOL")));
            }
            else if (exchange == "MCX" && segment == "M" && instrument == "OPTFUT")
            {
                if (string.IsNullOrWhiteSpace(symbolName) || configuredSymbols.Contains(symbolName))
                {
                    continue;
                }

                if (!decimal.TryParse(Value(row, index, "SEM_STRIKE_PRICE"), NumberStyles.Number, CultureInfo.InvariantCulture, out var strike))
                {
                    continue;
                }

                if (!DateTime.TryParse(Value(row, index, "SEM_EXPIRY_DATE"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var expiry))
                {
                    continue;
                }

                if (!commodityOptionGroups.TryGetValue(symbolName, out var rows))
                {
                    rows = [];
                    commodityOptionGroups[symbolName] = rows;
                }

                rows.Add(new DhanOptionRow(symbolName, strike, expiry));
            }
        }

        var stocks = optionGroups
            .Where(group => equities.ContainsKey(group.Key))
            .Select(group => ToInstrument(group.Key, equities[group.Key], group.Value))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => item.DisplayName)
            .Take(Math.Max(0, options.MaxDynamicStocks))
            .ToList();

        var commodities = commodityOptionGroups
            .Where(group => commodityFutures.ContainsKey(group.Key))
            .Select(group => ToCommodityInstrument(group.Key, commodityFutures[group.Key], group.Value))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => item.DisplayName)
            .Take(Math.Max(0, options.MaxDynamicCommodities))
            .ToList();

        return stocks.Concat(commodities).ToList();
    }

    private Stream OpenScripMasterStream()
    {
        if (File.Exists(options.ScripMasterUrl))
        {
            return File.OpenRead(options.ScripMasterUrl);
        }

        try
        {
            using var client = new HttpClient();
            return client.GetStreamAsync(options.ScripMasterUrl).GetAwaiter().GetResult();
        }
        catch when (!string.IsNullOrWhiteSpace(options.ScripMasterFallbackPath) &&
            File.Exists(options.ScripMasterFallbackPath))
        {
            return File.OpenRead(options.ScripMasterFallbackPath);
        }
    }

    private static InstrumentSummary? ToInstrument(string symbol, DhanEquityRow equity, IReadOnlyList<DhanOptionRow> optionRows)
    {
        var nearestExpiry = optionRows
            .Where(item => item.Expiry.Date >= DateTime.Today)
            .OrderBy(item => item.Expiry)
            .Select(item => item.Expiry)
            .FirstOrDefault();

        var source = nearestExpiry == default
            ? optionRows
            : optionRows.Where(item => item.Expiry.Date == nearestExpiry.Date).ToList();

        var strikeInterval = InferStrikeInterval(source.Select(item => item.Strike));
        if (strikeInterval <= 0)
        {
            return null;
        }

        return new InstrumentSummary(
            symbol,
            string.IsNullOrWhiteSpace(equity.DisplayName) ? symbol : equity.DisplayName,
            MarketSegment.Stock,
            equity.SecurityId,
            "NSE_EQ",
            "NSE_FNO",
            strikeInterval,
            false);
    }

    private static InstrumentSummary? ToCommodityInstrument(string symbol, IReadOnlyList<DhanFutureRow> futures, IReadOnlyList<DhanOptionRow> optionRows)
    {
        var future = futures
            .Where(item => item.Expiry.Date >= DateTime.Today)
            .OrderBy(item => item.Expiry)
            .FirstOrDefault();

        if (future is null)
        {
            return null;
        }

        var nearestExpiry = optionRows
            .Where(item => item.Expiry.Date >= DateTime.Today)
            .OrderBy(item => item.Expiry)
            .Select(item => item.Expiry)
            .FirstOrDefault();

        var source = nearestExpiry == default
            ? optionRows
            : optionRows.Where(item => item.Expiry.Date == nearestExpiry.Date).ToList();

        var strikeInterval = InferStrikeInterval(source.Select(item => item.Strike));
        if (strikeInterval <= 0)
        {
            return null;
        }

        return new InstrumentSummary(
            symbol,
            string.IsNullOrWhiteSpace(future.DisplayName) ? symbol : future.DisplayName,
            MarketSegment.Commodity,
            future.SecurityId,
            "MCX_COMM",
            "MCX_COMM",
            strikeInterval,
            false);
    }

    private static decimal InferStrikeInterval(IEnumerable<decimal> strikes)
    {
        var ordered = strikes
            .Distinct()
            .Order()
            .ToList();

        if (ordered.Count < 2)
        {
            return 1;
        }

        return ordered
            .Zip(ordered.Skip(1), (left, right) => right - left)
            .Where(diff => diff > 0)
            .DefaultIfEmpty(1)
            .Min();
    }

    private static string Value(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> index, string key)
    {
        return index.TryGetValue(key, out var i) && i < row.Count ? row[i].Trim() : string.Empty;
    }

    [GeneratedRegex("^(?<symbol>.+)-(?<expiry>[A-Za-z]{3}\\d{4})-(?<strike>-?\\d+(?:\\.\\d+)?)-(?<side>CE|PE)$")]
    private static partial Regex OptionSymbolRegex();

    private static readonly IReadOnlyList<InstrumentSummary> DefaultInstruments =
    [
        new("NIFTY", "NIFTY 50", MarketSegment.Nifty, 13, "IDX_I", "NSE_FNO", 50, true),
        new("SENSEX", "SENSEX", MarketSegment.Sensex, 51, "IDX_I", "BSE_FNO", 100, true),
        new("BANKNIFTY", "NIFTY BANK", MarketSegment.BankNifty, 25, "IDX_I", "NSE_FNO", 100, false),
        new("FINNIFTY", "NIFTY FIN SERVICE", MarketSegment.FinNifty, 27, "IDX_I", "NSE_FNO", 50, false)
    ];

    private sealed record DhanEquityRow(string Symbol, int SecurityId, string DisplayName);

    private sealed record DhanFutureRow(string Symbol, int SecurityId, DateTime Expiry, string DisplayName);

    private sealed record DhanOptionRow(string Symbol, decimal Strike, DateTime Expiry);
}

file static class Csv
{
    public static IReadOnlyList<string> Split(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        values.Add(current.ToString());
        return values;
    }
}
