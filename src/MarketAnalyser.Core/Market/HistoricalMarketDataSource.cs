using System.Net.Http.Json;

namespace MarketAnalyser.Core.Market;

public interface IHistoricalMarketDataSource
{
    Task<IReadOnlyList<MarketSnapshot>> GetSnapshotsAsync(
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}

public sealed class EmptyHistoricalMarketDataSource : IHistoricalMarketDataSource
{
    public Task<IReadOnlyList<MarketSnapshot>> GetSnapshotsAsync(
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<MarketSnapshot>>([]);
    }
}

public sealed class RestHistoricalMarketDataSource(HttpClient httpClient, string apiKey = "") : IHistoricalMarketDataSource
{
    public async Task<IReadOnlyList<MarketSnapshot>> GetSnapshotsAsync(
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var url = $"api/sessions/{Uri.EscapeDataString(symbol)}?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-MarketAnalyser-Key", apiKey);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var snapshots = await response.Content.ReadFromJsonAsync<IReadOnlyList<MarketSnapshot>>(cancellationToken);
        return snapshots ?? [];
    }
}
