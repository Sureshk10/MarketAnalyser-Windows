using System.Net.Http.Json;

namespace MarketAnalyser.Core.Market;

public sealed class RestMarketDataSource(HttpClient httpClient) : IMarketDataSource
{
    public string Name => "REST API feed";

    public async Task<IReadOnlyList<InstrumentSummary>> GetInstrumentsAsync(CancellationToken cancellationToken)
    {
        return await httpClient.GetFromJsonAsync<IReadOnlyList<InstrumentSummary>>(
            "api/instruments",
            cancellationToken) ?? [];
    }

    public async Task<MarketSnapshot> GetSnapshotAsync(string symbol, CancellationToken cancellationToken)
    {
        return await httpClient.GetFromJsonAsync<MarketSnapshot>(
            $"api/market/{Uri.EscapeDataString(symbol)}/snapshot",
            cancellationToken) ?? throw new InvalidOperationException($"No snapshot returned for {symbol}.");
    }
}
