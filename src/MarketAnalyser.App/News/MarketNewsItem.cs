namespace MarketAnalyser.App.News;

public sealed record MarketNewsItem(
    string Category,
    string Headline,
    string ImpactSummary,
    string Source,
    DateTimeOffset PublishedAt,
    string Link,
    string RelatedSymbols);
