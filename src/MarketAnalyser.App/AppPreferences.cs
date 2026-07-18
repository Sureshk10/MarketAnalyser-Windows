using System.IO;
using System.Text.Json;

namespace MarketAnalyser.App;

public sealed class AppPreferences
{
    public string LastSelectedSymbol { get; set; } = "NIFTY";

    public Dictionary<string, bool> FavoriteOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AlertSettings AlertSettings { get; set; } = new();
}

public sealed class AlertSettings
{
    public bool IsEnabled { get; set; } = true;

    public long OiBuildupThreshold { get; set; } = 100_000;

    public decimal SupportResistanceProximityPoints { get; set; } = 25;

    public int CooldownSeconds { get; set; } = 60;
}

public sealed class AppPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string directory;
    private readonly string path;

    public AppPreferencesStore()
    {
        directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketAnalyser");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "preferences.json");
    }

    public AppPreferences Load()
    {
        if (!File.Exists(path))
        {
            return new AppPreferences();
        }

        try
        {
            var json = File.ReadAllText(path);
            return Normalize(JsonSerializer.Deserialize<AppPreferences>(json, JsonOptions));
        }
        catch
        {
            return new AppPreferences();
        }
    }

    public void Save(AppPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(Normalize(preferences), JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
        }
    }

    private static AppPreferences Normalize(AppPreferences? preferences)
    {
        if (preferences is null)
        {
            return new AppPreferences();
        }

        preferences.LastSelectedSymbol = string.IsNullOrWhiteSpace(preferences.LastSelectedSymbol)
            ? "NIFTY"
            : preferences.LastSelectedSymbol.Trim().ToUpperInvariant();

        preferences.FavoriteOverrides = new Dictionary<string, bool>(
            preferences.FavoriteOverrides ?? new Dictionary<string, bool>(),
            StringComparer.OrdinalIgnoreCase);
        preferences.AlertSettings ??= new AlertSettings();
        preferences.AlertSettings.OiBuildupThreshold = Math.Clamp(preferences.AlertSettings.OiBuildupThreshold, 1_000, 10_000_000);
        preferences.AlertSettings.SupportResistanceProximityPoints = Math.Clamp(preferences.AlertSettings.SupportResistanceProximityPoints, 1, 1_000);
        preferences.AlertSettings.CooldownSeconds = Math.Clamp(preferences.AlertSettings.CooldownSeconds, 5, 600);

        return preferences;
    }
}

public static class AppExceptionLogger
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MarketAnalyser",
        "error.log");

    public static void Log(Exception exception)
    {
        try
        {
            Write($"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never become the reason the app exits.
        }
    }

    public static void LogProgress(string message)
    {
        try
        {
            Write($"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never become the reason the app exits.
        }
    }

    private static void Write(string message)
    {
        var directory = Path.GetDirectoryName(LogPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        lock (Gate)
        {
            File.AppendAllText(LogPath, message);
        }
    }
}
