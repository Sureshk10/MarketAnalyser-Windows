using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;

namespace MarketAnalyser.App.News;

public sealed class MarketNewsService
{
    private static readonly HttpClient HttpClient = new();

    public async Task<IReadOnlyList<MarketNewsItem>> GetNewsAsync(
        IReadOnlyList<string> favoriteSymbols,
        CancellationToken cancellationToken)
    {
        var requests = new[]
        {
            BuildQueryUrl(string.Join(" OR ", favoriteSymbols.Take(8).DefaultIfEmpty("NIFTY")), "favorites"),
            BuildQueryUrl("NIFTY BANK SENSEX India markets", "market"),
            BuildQueryUrl("Fed dollar crude oil Wall Street global markets", "global")
        };

        var items = new List<MarketNewsItem>();
        foreach (var (url, category) in requests)
        {
            try
            {
                var xml = await HttpClient.GetStringAsync(url, cancellationToken);
                items.AddRange(ParseFeed(xml, category, favoriteSymbols));
            }
            catch
            {
                items.Add(new MarketNewsItem(
                    category,
                    $"{category} news unavailable",
                    "Could not load news feed right now.",
                    "Market Analyser",
                    DateTimeOffset.Now,
                    url,
                    string.Empty));
            }
        }

        return items
            .OrderByDescending(item => item.PublishedAt)
            .ToArray();
    }

    private static IEnumerable<MarketNewsItem> ParseFeed(string xml, string category, IReadOnlyList<string> favoriteSymbols)
    {
        var document = XDocument.Parse(xml);
        var entries = document.Descendants("item").Take(12);
        foreach (var entry in entries)
        {
            var title = entry.Element("title")?.Value?.Trim() ?? string.Empty;
            var link = entry.Element("link")?.Value?.Trim() ?? string.Empty;
            var source = entry.Element(XName.Get("source"))?.Value?.Trim() ?? "RSS";
            var pubDate = entry.Element("pubDate")?.Value?.Trim();
            var publishedAt = DateTimeOffset.TryParse(pubDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.Now;
            var impact = BuildImpactSummary(category, title, favoriteSymbols);
            yield return new MarketNewsItem(category, title, impact, source, publishedAt, link, BuildRelatedSymbols(title, favoriteSymbols));
        }
    }

    private static string BuildImpactSummary(string category, string title, IReadOnlyList<string> favoriteSymbols)
    {
        var text = title.ToLowerInvariant();
        var impact = new List<string>();

        if (text.Contains("rally") || text.Contains("gain") || text.Contains("beat") || text.Contains("upgrade") || text.Contains("record high"))
        {
            impact.Add("Potentially supportive for risk sentiment");
        }

        if (text.Contains("fall") || text.Contains("drop") || text.Contains("selloff") || text.Contains("downgrade") || text.Contains("loss"))
        {
            impact.Add("Could pressure near-term sentiment");
        }

        if (text.Contains("oil") || text.Contains("crude"))
        {
            impact.Add("Watch energy-sensitive names and inflation tone");
        }

        if (text.Contains("fed") || text.Contains("rate") || text.Contains("inflation") || text.Contains("yield"))
        {
            impact.Add("Macro-sensitive; can move index direction and volatility");
        }

        if (text.Contains("bank") || text.Contains("financial") || text.Contains("loan") || text.Contains("credit"))
        {
            impact.Add("Banking and rate-sensitive stocks may react");
        }

        if (text.Contains("india") || text.Contains("nifty") || text.Contains("sensex"))
        {
            impact.Add("Directly relevant to domestic market mood");
        }

        if (impact.Count == 0)
        {
            impact.Add(category switch
            {
                "favorites" => "Likely relevant to one of the watched symbols",
                "market" => "May influence index direction and sector rotation",
                _ => "Global macro backdrop may affect risk appetite"
            });
        }

        return string.Join(" | ", impact.Distinct().Take(2));
    }

    private static string BuildRelatedSymbols(string title, IReadOnlyList<string> favoriteSymbols)
    {
        var text = title.ToLowerInvariant();
        var matches = favoriteSymbols
            .Where(symbol => text.Contains(symbol.ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        return string.Join(", ", matches);
    }

    private static (string Url, string Category) BuildQueryUrl(string query, string category)
    {
        var encoded = Uri.EscapeDataString(query);
        return ($"https://news.google.com/rss/search?q={encoded}&hl=en-IN&gl=IN&ceid=IN:en", category);
    }
}
