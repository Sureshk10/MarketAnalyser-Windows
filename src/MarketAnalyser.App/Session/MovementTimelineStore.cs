using System.Globalization;
using System.IO;
using System.Text.Json;

namespace MarketAnalyser.App.Session;

public sealed class MovementTimelineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string rootDirectory;

    public MovementTimelineStore()
    {
        rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketAnalyser",
            "movement-timeline");
    }

    public async Task<IReadOnlyList<MovementTimelineRecord>> LoadAsync(
        string symbol,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var path = GetTimelinePath(symbol, date);
        if (!File.Exists(path))
        {
            return [];
        }

        var records = new List<MovementTimelineRecord>();
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<MovementTimelineRecord>(line, JsonOptions);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (JsonException ex)
            {
                AppExceptionLogger.Log(ex);
            }
        }

        return records
            .OrderBy(record => record.Timestamp)
            .ToArray();
    }

    public async Task AppendAsync(MovementTimelineRecord record, CancellationToken cancellationToken)
    {
        var path = GetTimelinePath(record.Symbol, DateOnly.FromDateTime(record.Timestamp.ToLocalTime().DateTime));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(record, JsonOptions);
        await File.AppendAllTextAsync(path, json + Environment.NewLine, cancellationToken);
    }

    private string GetTimelinePath(string symbol, DateOnly date)
    {
        var safeSymbol = string.Concat(symbol.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        var dateFolder = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Path.Combine(rootDirectory, dateFolder, $"{safeSymbol}.jsonl");
    }
}

public sealed record MovementTimelineRecord(
    string Symbol,
    DateTimeOffset Timestamp,
    string Title,
    string Detail,
    string WatchText,
    decimal Spot,
    decimal SpotChange);
