using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MarketAnalyser.App.Session;
using MarketAnalyser.App.ViewModels;
using MarketAnalyser.Core.Market;

namespace MarketAnalyser.App;

public partial class SupportResistanceWindow : Window, INotifyPropertyChanged
{
    private readonly IReadOnlyList<CatalogInstrumentViewModel> favorites;
    private readonly IHistoricalMarketDataSource historicalDataSource;
    private readonly MarketSessionStore sessionStore;
    private readonly IMarketDataSource marketDataSource;
    private readonly HistoricalSupportResistanceCacheStore cacheStore = new();
    private readonly ObservableCollection<SupportResistanceRow> rows = [];
    private static readonly TimeSpan SymbolFetchDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeOnly MarketOpenTime = new(9, 15);
    private static readonly TimeOnly MarketCloseTime = new(15, 30);
    private bool suppressFallbacksAfterClear;

    public SupportResistanceWindow(
        IReadOnlyList<CatalogInstrumentViewModel> favorites,
        IHistoricalMarketDataSource historicalDataSource,
        MarketSessionStore sessionStore,
        IMarketDataSource marketDataSource)
    {
        this.favorites = favorites;
        this.historicalDataSource = historicalDataSource;
        this.sessionStore = sessionStore;
        this.marketDataSource = marketDataSource;
        InitializeComponent();
        DataContext = this;
        Title = $"S/R Scanner - {favorites.Count} favorites";
        Rows = rows;
        SelectedTimeframe = "3m";
        TimeframeOptions = ["1m", "3m", "5m", "15m", "1h", "4h", "1d", "1w", "1M"];
        Loaded += async (_, _) => await RefreshAsync();
    }

    public ObservableCollection<SupportResistanceRow> Rows { get; }

    public IReadOnlyList<string> TimeframeOptions { get; }

    public string SelectedTimeframe
    {
        get => selectedTimeframe;
        set
        {
            if (selectedTimeframe == value)
            {
                return;
            }

            selectedTimeframe = value;
            OnPropertyChanged(nameof(SelectedTimeframe));
        }
    }

    public string StatusText
    {
        get => statusText;
        set
        {
            if (statusText == value)
            {
                return;
            }

            statusText = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private string selectedTimeframe = "3m";
    private string statusText = "Preparing scanner...";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText = "Clearing local cache...";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            await cacheStore.ClearAsync(CancellationToken.None);
            Rows.Clear();
            suppressFallbacksAfterClear = true;
            LogProgress("[S/R] local cache cleared");
            StatusText = "Local cache cleared";
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            StatusText = "Clear failed";
            MessageBox.Show(
                $"Could not clear local cache: {ex.Message}",
                "Market Analyser",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void TimeframeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            await RefreshAsync();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task RefreshAsync()
    {
        try
        {
            suppressFallbacksAfterClear = suppressFallbacksAfterClear;
            StatusText = $"Preparing {SelectedTimeframe} scan...";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            LogProgress($"[S/R] refresh start timeframe={SelectedTimeframe} favorites={favorites.Count}");
            Rows.Clear();

            var orderedFavorites = favorites.OrderBy(item => item.DisplayName).ToArray();
            foreach (var favorite in orderedFavorites)
            {
                Rows.Add(SupportResistanceRow.Empty(favorite.DisplayName, favorite.Symbol, "Waiting..."));
            }

            for (var i = 0; i < orderedFavorites.Length; i++)
            {
                var favorite = orderedFavorites[i];
                LogProgress($"[S/R] loading {favorite.Symbol}");
                StatusText = $"Fetching {SelectedTimeframe} for {favorite.DisplayName}...";
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                var row = await BuildRowAsync(favorite, SelectedTimeframe);
                Rows[i] = row;
                LogProgress($"[S/R] done {favorite.Symbol} => {row.SignalText} | {row.NearText}");
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                if (i < orderedFavorites.Length - 1)
                {
                    await Task.Delay(SymbolFetchDelay);
                }
            }

            StatusText = $"{Rows.Count} symbols scanned";
            LogProgress($"[S/R] refresh complete rows={Rows.Count}");
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            StatusText = ex.Message;
            LogProgress($"[S/R] refresh error {ex.Message}");
        }
        finally
        {
            suppressFallbacksAfterClear = false;
        }
    }

    private async Task<SupportResistanceRow> BuildRowAsync(CatalogInstrumentViewModel item, string timeframe)
    {
        try
        {
            var skipFallbacks = suppressFallbacksAfterClear;
            var window = ResolveLookback(timeframe);
            var now = DateTimeOffset.Now;
            var to = now;
            var from = to.AddDays(-window.days);
            LogProgress($"[S/R] cache lookup {item.Symbol} {timeframe} {from:O}..{to:O}");
            var snapshots = await cacheStore.LoadAsync(item.Symbol, timeframe, from, to, CancellationToken.None);
            var metadata = await cacheStore.GetMetadataAsync(item.Symbol, timeframe, CancellationToken.None);
            var latestCachedTimestamp = metadata?.LastSyncedThrough ?? await cacheStore.GetLatestTimestampAsync(item.Symbol, timeframe, CancellationToken.None);
            var missingFrom = latestCachedTimestamp is null
                ? from
                : latestCachedTimestamp.Value.AddMinutes(1);
            var cacheExists = latestCachedTimestamp is not null;
            var isAfterMarketClose = TimeOnly.FromTimeSpan(now.ToLocalTime().TimeOfDay) >= MarketCloseTime;
            var shouldFetch = ShouldFetchHistoricalData(now, metadata);
            var shouldFetchFromDhan = !cacheExists || (!isAfterMarketClose && shouldFetch);

            if (shouldFetchFromDhan)
            {
                var fetchFrom = cacheExists ? missingFrom : from;
                StatusText = cacheExists
                    ? $"Updating cache from Dhan for {item.DisplayName}..."
                    : $"Fetching from Dhan for {item.DisplayName}...";
                LogProgress($"[S/R] {(!cacheExists ? "no cache" : $"cache tail {item.Symbol} through {latestCachedTimestamp:O}")}, fetching {fetchFrom:O}..{to:O}");
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                var fetched = await historicalDataSource.GetSnapshotsAsync(item.Symbol, fetchFrom, to, CancellationToken.None);
                if (fetched.Count > 0)
                {
                    snapshots = cacheExists
                        ? snapshots
                            .Concat(fetched)
                            .GroupBy(snapshot => snapshot.Timestamp)
                            .Select(group => group.Last())
                            .OrderBy(snapshot => snapshot.Timestamp)
                            .ToArray()
                        : fetched
                            .OrderBy(snapshot => snapshot.Timestamp)
                            .ToArray();

                    LogProgress($"[S/R] historical fetched {item.Symbol} count={snapshots.Count}, caching");
                    await cacheStore.SaveAsync(item.Symbol, timeframe, snapshots, CancellationToken.None);
                }
                else
                {
                    LogProgress($"[S/R] historical fetch returned empty for {item.Symbol}");
                }
            }
            else if (cacheExists)
            {
                LogProgress($"[S/R] cache current for {item.Symbol} through {latestCachedTimestamp:O}; skipping Dhan fetch");
            }

            if (isAfterMarketClose && cacheExists)
            {
                if (snapshots.Count == 0)
                {
                    return SupportResistanceRow.Empty(item.DisplayName, item.Symbol, $"Cached data unavailable for {timeframe}");
                }

                return BuildRowFromSnapshots(item, timeframe, snapshots);
            }

            if (snapshots.Count == 0 && !skipFallbacks && cacheExists)
            {
                StatusText = $"Using session data for {item.DisplayName}...";
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                LogProgress($"[S/R] historical empty {item.Symbol}, checking session fallback");
                var today = DateOnly.FromDateTime(DateTime.Now);
                var records = await sessionStore.LoadRecordsAsync(item.Symbol, today, CancellationToken.None);
                if (records.Count > 0)
                {
                    LogProgress($"[S/R] session fallback {item.Symbol} count={records.Count}");
                    snapshots = records
                        .Select(record => new MarketSnapshot(
                            record.Symbol,
                            record.Spot,
                            record.SpotChange,
                            record.Timestamp,
                            [],
                            new MarketBreadth(
                                record.PutCallRatioOi,
                                record.PutCallRatioVolume,
                                record.CeVolumeShare,
                                record.PeVolumeShare,
                                record.TotalCallOi,
                                record.TotalPutOi,
                                record.TotalCallVolume,
                                record.TotalPutVolume),
                            [new ChartPoint(record.Timestamp, record.Spot)],
                            [new ChartPoint(record.Timestamp, record.UnderlyingVolume)],
                            [new ChartPoint(record.Timestamp, record.Strikes.Sum(strike => strike.PutOpenInterestChange - strike.CallOpenInterestChange))],
                            [],
                            null,
                            "Session fallback",
                            null))
                        .ToArray();
                }
            }

            if (snapshots.Count == 0 && !skipFallbacks && cacheExists)
            {
                StatusText = $"Using live fallback for {item.DisplayName}...";
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                LogProgress($"[S/R] live fallback {item.Symbol}");
                var liveSnapshot = await marketDataSource.GetSnapshotAsync(item.Symbol, CancellationToken.None);
                snapshots = liveSnapshot is null
                    ? []
                    : [liveSnapshot];
            }
            else if (snapshots.Count == 0 && skipFallbacks)
            {
                LogProgress($"[S/R] fallback suppressed for {item.Symbol} after clear");
            }
            if (snapshots.Count == 0)
            {
                StatusText = $"No data for {item.DisplayName}";
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                return SupportResistanceRow.Empty(item.DisplayName, item.Symbol, $"No {timeframe} history");
            }

            var candles = AggregateCandles(snapshots, timeframe);
            if (candles.Count == 0)
            {
                StatusText = $"No candles for {item.DisplayName}";
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                return SupportResistanceRow.Empty(item.DisplayName, item.Symbol, $"No {timeframe} candles");
            }

            var spot = candles.Last().Close;
            var support = candles.MinBy(candle => candle.Low)?.Low ?? spot;
            var resistance = candles.MaxBy(candle => candle.High)?.High ?? spot;
            var priceSeries = candles.Select(candle => new ChartPoint(candle.End, candle.Close)).ToArray();
            var volumeSeries = candles.Select(candle => new ChartPoint(candle.End, candle.Volume)).ToArray();
            var rsi = CalculateRsi(priceSeries, 14);
            var vwap = CalculateVwap(priceSeries, volumeSeries);
            var supertrend = CalculateSupertrend(candles, 10, 3m);
            var fib = BuildFibonacciText(candles, spot);
            var strength = BuildStrengthText(spot, support, resistance, candles, rsi, vwap);
            var signal = BuildSignal(spot, support, resistance, priceSeries, rsi, vwap);
            var near = BuildNearText(spot, support, resistance);

            return new SupportResistanceRow(
                item.DisplayName,
                item.Symbol,
                FormatNumber(spot),
                FormatNumber(support),
                FormatNumber(resistance),
                signal.Label,
                signal.Foreground,
                near,
                rsi is null ? "--" : rsi.Value.ToString("N1", CultureInfo.CurrentCulture),
                vwap is null ? "--" : vwap.Value.ToString("N2", CultureInfo.CurrentCulture),
                FormatCompactCount(volumeSeries.LastOrDefault()?.Value ?? 0),
                supertrend,
                fib,
                strength,
                window.label);
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            LogProgress($"[S/R] row error {item.Symbol} {ex.Message}");
            return SupportResistanceRow.Empty(item.DisplayName, item.Symbol, $"Error: {ex.Message}");
        }
    }

    private static SupportResistanceRow BuildRowFromSnapshots(
        CatalogInstrumentViewModel item,
        string timeframe,
        IReadOnlyList<MarketSnapshot> snapshots)
    {
        var candles = AggregateCandles(snapshots, timeframe);
        if (candles.Count == 0)
        {
            return SupportResistanceRow.Empty(item.DisplayName, item.Symbol, $"No {timeframe} candles");
        }

        var spot = candles.Last().Close;
        var support = candles.MinBy(candle => candle.Low)?.Low ?? spot;
        var resistance = candles.MaxBy(candle => candle.High)?.High ?? spot;
        var priceSeries = candles.Select(candle => new ChartPoint(candle.End, candle.Close)).ToArray();
        var volumeSeries = candles.Select(candle => new ChartPoint(candle.End, candle.Volume)).ToArray();
        var rsi = CalculateRsi(priceSeries, 14);
        var vwap = CalculateVwap(priceSeries, volumeSeries);
        var supertrend = CalculateSupertrend(candles, 10, 3m);
        var fib = BuildFibonacciText(candles, spot);
        var strength = BuildStrengthText(spot, support, resistance, candles, rsi, vwap);
        var signal = BuildSignal(spot, support, resistance, priceSeries, rsi, vwap);
        var near = BuildNearText(spot, support, resistance);

        return new SupportResistanceRow(
            item.DisplayName,
            item.Symbol,
            FormatNumber(spot),
            FormatNumber(support),
            FormatNumber(resistance),
            signal.Label,
            signal.Foreground,
            near,
            rsi is null ? "--" : rsi.Value.ToString("N1", CultureInfo.CurrentCulture),
            vwap is null ? "--" : vwap.Value.ToString("N2", CultureInfo.CurrentCulture),
            FormatCompactCount(volumeSeries.LastOrDefault()?.Value ?? 0),
            supertrend,
            fib,
            strength,
            timeframe);
    }

    private static bool ShouldFetchHistoricalData(DateTimeOffset now, HistoricalCacheMetadata? metadata)
    {
        if (metadata?.LastSyncedThrough is null)
        {
            return true;
        }

        var localNow = now.ToLocalTime();
        var localLatest = metadata.LastSyncedThrough.Value.ToLocalTime();
        var nowTime = TimeOnly.FromTimeSpan(localNow.TimeOfDay);
        var latestTime = TimeOnly.FromTimeSpan(localLatest.TimeOfDay);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var latestDate = DateOnly.FromDateTime(localLatest.DateTime);

        if (nowTime >= MarketCloseTime)
        {
            return false;
        }

        if (nowTime < MarketOpenTime)
        {
            return latestDate >= today;
        }

        return latestDate < today || latestTime < nowTime;
    }

    private static void LogProgress(string message)
    {
        Trace.WriteLine(message);
        Console.WriteLine(message);
        AppExceptionLogger.LogProgress(message);
    }

    private static (int days, string label) ResolveLookback(string timeframe)
    {
        return timeframe switch
        {
            "1m" => (3, "3D lookback"),
            "3m" => (5, "5D lookback"),
            "5m" => (8, "8D lookback"),
            "15m" => (12, "12D lookback"),
            "1h" => (20, "20D lookback"),
            "4h" => (45, "45D lookback"),
            "1d" => (90, "90D lookback"),
            "1w" => (180, "180D lookback"),
            "1M" => (365, "1Y lookback"),
            _ => (12, "12D lookback")
        };
    }

    private static IReadOnlyList<HistoricalCandle> AggregateCandles(IReadOnlyList<MarketSnapshot> snapshots, string timeframe)
    {
        if (snapshots.Count == 0)
        {
            return [];
        }

        var ordered = snapshots.OrderBy(snapshot => snapshot.Timestamp).ToArray();
        var bucket = timeframe switch
        {
            "1m" => TimeSpan.FromMinutes(1),
            "3m" => TimeSpan.FromMinutes(3),
            "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15),
            "1h" => TimeSpan.FromHours(1),
            "4h" => TimeSpan.FromHours(4),
            "1d" => TimeSpan.FromDays(1),
            "1w" => TimeSpan.FromDays(7),
            "1M" => TimeSpan.FromDays(30),
            _ => TimeSpan.FromMinutes(15)
        };

        var groups = ordered
            .GroupBy(snapshot => BucketKey(snapshot.Timestamp, timeframe, bucket))
            .OrderBy(group => group.Key);

        return groups.Select(group =>
        {
            var points = group.ToArray();
            var volume = points.Sum(point => point.VolumeSeries.LastOrDefault()?.Value ?? 0);
            return new HistoricalCandle(
                points.First().Timestamp,
                points.Last().Timestamp,
                points.First().Spot,
                points.Max(point => point.Spot),
                points.Min(point => point.Spot),
                points.Last().Spot,
                volume);
        }).ToArray();
    }

    private static string BucketKey(DateTimeOffset timestamp, string timeframe, TimeSpan bucket)
    {
        if (timeframe is "1d" or "1w" or "1M")
        {
            return timeframe switch
            {
                "1d" => timestamp.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "1w" => $"{GetIsoWeekYear(timestamp):D4}-W{System.Globalization.ISOWeek.GetWeekOfYear(timestamp.DateTime):D2}",
                "1M" => timestamp.ToLocalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture),
                _ => timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            };
        }

        var local = timestamp.ToLocalTime();
        var bucketMinutes = (int)bucket.TotalMinutes;
        var minuteBucket = (local.Minute / bucketMinutes) * bucketMinutes;
        var bucketStart = new DateTimeOffset(new DateTime(local.Year, local.Month, local.Day, local.Hour, minuteBucket, 0, DateTimeKind.Unspecified), local.Offset);
        return bucketStart.ToString("O", CultureInfo.InvariantCulture);
    }

    private static int GetIsoWeekYear(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime().DateTime;
        var week = System.Globalization.ISOWeek.GetWeekOfYear(local);
        if (week >= 52 && local.Month == 1)
        {
            return local.Year - 1;
        }

        if (week == 1 && local.Month == 12)
        {
            return local.Year + 1;
        }

        return local.Year;
    }

    private static (string Label, Brush Foreground) BuildSignal(
        decimal spot,
        decimal support,
        decimal resistance,
        IReadOnlyList<ChartPoint> priceSeries,
        decimal? rsi,
        decimal? vwap)
    {
        var score = 0;
        if (vwap is not null)
        {
            if (spot > vwap.Value)
            {
                score++;
            }
            else if (spot < vwap.Value)
            {
                score--;
            }
        }

        if (rsi is not null)
        {
            if (rsi >= 55m)
            {
                score++;
            }
            else if (rsi <= 45m)
            {
                score--;
            }
        }

        if (priceSeries.Count > 1)
        {
            var first = priceSeries.First().Value;
            var last = priceSeries.Last().Value;
            if (last > first)
            {
                score++;
            }
            else if (last < first)
            {
                score--;
            }
        }

        var label = score >= 2
            ? "BUY"
            : score <= -2
                ? "SELL"
                : "WAIT";

        return label switch
        {
            "BUY" => (label, Brushes.MediumSeaGreen),
            "SELL" => (label, Brushes.IndianRed),
            _ => (label, Brushes.Goldenrod)
        };
    }

    private static string BuildNearText(decimal spot, decimal support, decimal resistance)
    {
        var supportGap = spot - support;
        var resistanceGap = resistance - spot;
        if (supportGap <= resistanceGap)
        {
            return $"Near support {FormatNumber(support)}";
        }

        return $"Near resistance {FormatNumber(resistance)}";
    }

    private static string BuildStrengthText(
        decimal spot,
        decimal support,
        decimal resistance,
        IReadOnlyList<HistoricalCandle> candles,
        decimal? rsi,
        decimal? vwap)
    {
        var score = 0;

        if (vwap is not null)
        {
            score += spot > vwap.Value ? 1 : spot < vwap.Value ? -1 : 0;
        }

        if (rsi is not null)
        {
            score += rsi >= 60m ? 1 : rsi <= 40m ? -1 : 0;
        }

        var supertrend = CalculateSupertrendDirection(candles);
        if (supertrend is not null)
        {
            score += supertrend.Value ? 1 : -1;
        }

        var supportGap = spot - support;
        var resistanceGap = resistance - spot;
        var nearLevel = Math.Min(supportGap, resistanceGap);
        if (nearLevel <= Math.Max(spot * 0.003m, 10m))
        {
            score += supportGap <= resistanceGap ? 1 : -1;
        }

        var label = score >= 3
            ? "STRONG BUY"
            : score == 2
                ? "BUY"
                : score <= -3
                    ? "STRONG SELL"
                    : score == -2
                        ? "SELL"
                        : "WAIT";

        return label;
    }

    private static decimal? CalculateRsi(IReadOnlyList<ChartPoint> priceSeries, int period)
    {
        if (priceSeries.Count < period + 1)
        {
            return null;
        }

        var slice = priceSeries.TakeLast(period + 1).Select(point => point.Value).ToArray();
        decimal gains = 0;
        decimal losses = 0;
        for (var i = 1; i < slice.Length; i++)
        {
            var change = slice[i] - slice[i - 1];
            if (change > 0)
            {
                gains += change;
            }
            else
            {
                losses += Math.Abs(change);
            }
        }

        if (gains == 0 && losses == 0)
        {
            return 50m;
        }

        if (losses == 0)
        {
            return 100m;
        }

        var rs = (gains / period) / (losses / period);
        return decimal.Round(100m - (100m / (1m + rs)), 1);
    }

    private static decimal? CalculateVwap(IReadOnlyList<ChartPoint> priceSeries, IReadOnlyList<ChartPoint> volumeSeries)
    {
        var count = Math.Min(priceSeries.Count, volumeSeries.Count);
        if (count == 0)
        {
            return null;
        }

        decimal priceVolume = 0;
        decimal volume = 0;
        for (var i = 0; i < count; i++)
        {
            var v = volumeSeries[i].Value;
            priceVolume += priceSeries[i].Value * v;
            volume += v;
        }

        return volume <= 0 ? null : decimal.Round(priceVolume / volume, 2);
    }

    private static string CalculateSupertrend(IReadOnlyList<HistoricalCandle> candles, int atrPeriod, decimal multiplier)
    {
        if (candles.Count < atrPeriod + 1)
        {
            return "--";
        }

        var atr = CalculateAtr(candles, atrPeriod);
        if (atr is null)
        {
            return "--";
        }

        decimal? finalUpperBand = null;
        decimal? finalLowerBand = null;
        decimal? supertrend = null;
        bool bullish = true;

        for (var i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            var hl2 = (candle.High + candle.Low) / 2m;
            var basicUpperBand = hl2 + multiplier * atr.Value;
            var basicLowerBand = hl2 - multiplier * atr.Value;

            if (i == 0)
            {
                finalUpperBand = basicUpperBand;
                finalLowerBand = basicLowerBand;
                supertrend = basicLowerBand;
                bullish = candle.Close >= basicLowerBand;
                continue;
            }

            var prevClose = candles[i - 1].Close;
            finalUpperBand = basicUpperBand < finalUpperBand.Value || prevClose > finalUpperBand.Value
                ? basicUpperBand
                : finalUpperBand;
            finalLowerBand = basicLowerBand > finalLowerBand.Value || prevClose < finalLowerBand.Value
                ? basicLowerBand
                : finalLowerBand;

            if (supertrend == finalUpperBand && candle.Close <= finalUpperBand.Value)
            {
                supertrend = finalUpperBand;
                bullish = false;
            }
            else if (supertrend == finalUpperBand && candle.Close > finalUpperBand.Value)
            {
                supertrend = finalLowerBand;
                bullish = true;
            }
            else if (supertrend == finalLowerBand && candle.Close >= finalLowerBand.Value)
            {
                supertrend = finalLowerBand;
                bullish = true;
            }
            else if (supertrend == finalLowerBand && candle.Close < finalLowerBand.Value)
            {
                supertrend = finalUpperBand;
                bullish = false;
            }
            else
            {
                supertrend = candle.Close >= finalLowerBand ? finalLowerBand : finalUpperBand;
                bullish = candle.Close >= finalLowerBand;
            }
        }

        return $"{(bullish ? "Bull" : "Bear")} {FormatNumber(supertrend ?? 0)}";
    }

    private static bool? CalculateSupertrendDirection(IReadOnlyList<HistoricalCandle> candles, int atrPeriod = 10, decimal multiplier = 3m)
    {
        if (candles.Count < atrPeriod + 1)
        {
            return null;
        }

        var atr = CalculateAtr(candles, atrPeriod);
        if (atr is null)
        {
            return null;
        }

        decimal? finalUpperBand = null;
        decimal? finalLowerBand = null;
        decimal? supertrend = null;
        bool bullish = true;

        for (var i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            var hl2 = (candle.High + candle.Low) / 2m;
            var basicUpperBand = hl2 + multiplier * atr.Value;
            var basicLowerBand = hl2 - multiplier * atr.Value;

            if (i == 0)
            {
                finalUpperBand = basicUpperBand;
                finalLowerBand = basicLowerBand;
                supertrend = basicLowerBand;
                bullish = candle.Close >= basicLowerBand;
                continue;
            }

            var prevClose = candles[i - 1].Close;
            finalUpperBand = basicUpperBand < finalUpperBand!.Value || prevClose > finalUpperBand.Value
                ? basicUpperBand
                : finalUpperBand;
            finalLowerBand = basicLowerBand > finalLowerBand!.Value || prevClose < finalLowerBand.Value
                ? basicLowerBand
                : finalLowerBand;

            if (supertrend == finalUpperBand && candle.Close <= finalUpperBand.Value)
            {
                supertrend = finalUpperBand;
                bullish = false;
            }
            else if (supertrend == finalUpperBand && candle.Close > finalUpperBand.Value)
            {
                supertrend = finalLowerBand;
                bullish = true;
            }
            else if (supertrend == finalLowerBand && candle.Close >= finalLowerBand.Value)
            {
                supertrend = finalLowerBand;
                bullish = true;
            }
            else if (supertrend == finalLowerBand && candle.Close < finalLowerBand.Value)
            {
                supertrend = finalUpperBand;
                bullish = false;
            }
            else
            {
                supertrend = candle.Close >= finalLowerBand ? finalLowerBand : finalUpperBand;
                bullish = candle.Close >= finalLowerBand;
            }
        }

        return bullish;
    }

    private static decimal? CalculateAtr(IReadOnlyList<HistoricalCandle> candles, int period)
    {
        if (candles.Count < period + 1)
        {
            return null;
        }

        var trValues = new List<decimal>();
        for (var i = 1; i < candles.Count; i++)
        {
            var current = candles[i];
            var previousClose = candles[i - 1].Close;
            var tr = Math.Max(
                current.High - current.Low,
                Math.Max(Math.Abs(current.High - previousClose), Math.Abs(current.Low - previousClose)));
            trValues.Add(tr);
        }

        var slice = trValues.TakeLast(period).ToArray();
        if (slice.Length == 0)
        {
            return null;
        }

        return decimal.Round(slice.Average(), 2);
    }

    private static string BuildFibonacciText(IReadOnlyList<HistoricalCandle> candles, decimal spot)
    {
        if (candles.Count == 0)
        {
            return "--";
        }

        var swingHigh = candles.Max(candle => candle.High);
        var swingLow = candles.Min(candle => candle.Low);
        var range = swingHigh - swingLow;
        if (range <= 0)
        {
            return "--";
        }

        var levels = new (string Label, decimal Price)[]
        {
            ("23.6%", swingHigh - (range * 0.236m)),
            ("38.2%", swingHigh - (range * 0.382m)),
            ("50.0%", swingHigh - (range * 0.500m)),
            ("61.8%", swingHigh - (range * 0.618m)),
            ("78.6%", swingHigh - (range * 0.786m))
        };

        var nearest = levels.OrderBy(level => Math.Abs(level.Price - spot)).First();
        return $"{nearest.Label} {FormatNumber(nearest.Price)}";
    }

    private static string FormatNumber(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);

    private static string FormatCompactCount(decimal value)
    {
        var abs = Math.Abs(value);
        if (abs >= 1_00_00_000m)
        {
            return $"{value / 1_00_00_000m:N1}Cr";
        }

        if (abs >= 1_00_000m)
        {
            return $"{value / 1_00_000m:N1}L";
        }

        if (abs >= 1_000m)
        {
            return $"{value / 1_000m:N1}K";
        }

        return value.ToString("N0", CultureInfo.CurrentCulture);
    }

    private sealed record HistoricalCandle(
        DateTimeOffset Start,
        DateTimeOffset End,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal Volume);

    public sealed record SupportResistanceRow(
        string DisplayName,
        string Symbol,
        string SpotText,
        string SupportText,
        string ResistanceText,
        string SignalText,
        Brush SignalForeground,
        string NearText,
        string RsiText,
        string VwapText,
        string VolumeText,
        string SupertrendText,
        string FibonacciText,
        string StrengthText,
        string LookbackText)
    {
        public static SupportResistanceRow Empty(string displayName, string symbol, string note = "--")
        {
            return new SupportResistanceRow(displayName, symbol, "--", "--", "--", "WAIT", Brushes.Goldenrod, note, "--", "--", "--", "--", "--", "--", string.Empty);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ValueToBrushConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text == "--" || text == "-")
        {
            return Brushes.LightSlateGray;
        }

        var upper = text.ToUpperInvariant();
        if (upper.Contains("BUY") || upper.Contains("BULL") || upper.Contains("SUPPORT"))
        {
            return Brushes.MediumSeaGreen;
        }

        if (upper.Contains("SELL") || upper.Contains("BEAR") || upper.Contains("RESIST"))
        {
            return Brushes.IndianRed;
        }

        var numericPart = ExtractLeadingNumber(text);
        if (numericPart is not null)
        {
            if (numericPart > 0)
            {
                return Brushes.MediumSeaGreen;
            }

            if (numericPart < 0)
            {
                return Brushes.IndianRed;
            }
        }

        return Brushes.DodgerBlue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static decimal? ExtractLeadingNumber(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var candidate = parts[0]
            .Replace("%", string.Empty)
            .Replace("Cr", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("L", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("K", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (decimal.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        if (parts.Length > 1 && decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return value;
        }

        return null;
    }
}
