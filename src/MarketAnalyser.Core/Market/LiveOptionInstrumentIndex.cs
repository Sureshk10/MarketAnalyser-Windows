using System.Collections.Concurrent;

namespace MarketAnalyser.Core.Market;

public sealed class LiveOptionInstrumentIndex
{
    private readonly ConcurrentDictionary<int, OptionInstrumentRef> bySecurityId = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<OptionInstrumentRef>> bySymbol = new(StringComparer.OrdinalIgnoreCase);

    public event Action<IReadOnlyList<OptionInstrumentRef>>? InstrumentsChanged;

    public IReadOnlyList<OptionInstrumentRef> GetAll()
    {
        return bySymbol.Values.SelectMany(item => item).ToList();
    }

    public bool TryGet(int securityId, out OptionInstrumentRef instrument)
    {
        return bySecurityId.TryGetValue(securityId, out instrument!);
    }

    public void ReplaceSymbol(string symbol, IEnumerable<OptionInstrumentRef> instruments)
    {
        var refs = instruments
            .GroupBy(item => item.SecurityId)
            .Select(group => group.First())
            .ToList();

        if (bySymbol.TryGetValue(symbol, out var oldRefs))
        {
            foreach (var oldRef in oldRefs)
            {
                bySecurityId.TryRemove(oldRef.SecurityId, out _);
            }
        }

        bySymbol[symbol] = refs;

        foreach (var item in refs)
        {
            bySecurityId[item.SecurityId] = item;
        }

        InstrumentsChanged?.Invoke(refs);
    }
}

public sealed record OptionInstrumentRef(
    string Symbol,
    decimal Strike,
    OptionSide Side,
    string ExchangeSegment,
    int SecurityId,
    bool IsUnderlying = false);
