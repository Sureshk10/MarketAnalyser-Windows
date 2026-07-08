using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MarketAnalyser.App.Session;
using MarketAnalyser.Core.Market;

namespace MarketAnalyser.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IMarketDataSource marketDataSource;
    private readonly AppPreferencesStore preferencesStore;
    private readonly MarketSessionStore sessionStore;
    private readonly MovementTimelineStore movementTimelineStore;
    private readonly IHistoricalMarketDataSource historicalDataSource;
    private readonly DispatcherTimer refreshTimer;
    private readonly DispatcherTimer replayTimer;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> recentAlertKeys = [];
    private AppPreferences preferences = new();
    private MarketSnapshot? snapshot;
    private CatalogInstrumentViewModel? selectedInstrument;
    private OptionStrikeSnapshot? selectedStrike;
    private string? lastSignalLabel;
    private string status = "Starting";
    private string sessionStatus = "Session not loaded";
    private string instrumentSearchText = string.Empty;
    private DateTimeOffset? lastRecordedSnapshotTime;
    private CatalogFilter selectedCatalogFilter = CatalogFilter.All;
    private bool isBusy;
    private bool hasSessionBackfill;
    private IReadOnlyList<ChartSeriesViewModel> priceChartSeries = [];
    private IReadOnlyList<ChartSeriesViewModel> oiChartSeries = [];
    private IReadOnlyList<ChartSeriesViewModel> selectedStrikeOiChartSeries = [];
    private string sessionOpenMoveText = "--";
    private string sessionRangeText = "--";
    private string sessionTrendText = "Waiting";
    private string sessionOiShiftText = "--";
    private Brush sessionTrendForeground = Brushes.LightSlateGray;
    private Brush sessionOiShiftForeground = Brushes.LightSlateGray;
    private string movementReadingTitle = "Waiting";
    private string movementReadingDetail = "Live snapshot not loaded";
    private string movementReadingWatchText = "--";
    private Brush movementReadingForeground = Brushes.LightSlateGray;
    private string? lastMovementTimelineTitle;
    private string sessionReviewSummaryText = "Waiting for session data";
    private string sessionReviewPhasesText = "Timeline will appear as readings change";
    private string sessionReviewOiText = "--";
    private string sessionReviewExportText = string.Empty;
    private string replayOutcomeText = "Load replay to evaluate follow-through";
    private Brush replayOutcomeForeground = Brushes.LightSlateGray;
    private DateTime? replaySelectedDate = DateTime.Today;
    private IReadOnlyList<MarketSessionRecord> replayRecords = [];
    private int replayIndex = -1;
    private bool isReplayMode;
    private bool isReplayPlaying;
    private string replayStatusText = "Replay not loaded";

    public MainWindowViewModel(
        IMarketDataSource marketDataSource,
        AppPreferencesStore preferencesStore,
        MarketSessionStore sessionStore,
        MovementTimelineStore movementTimelineStore,
        IHistoricalMarketDataSource historicalDataSource)
    {
        this.marketDataSource = marketDataSource;
        this.preferencesStore = preferencesStore;
        this.sessionStore = sessionStore;
        this.movementTimelineStore = movementTimelineStore;
        this.historicalDataSource = historicalDataSource;
        DataSourceName = marketDataSource.Name;
        ToggleFavoriteCommand = new RelayCommand<CatalogInstrumentViewModel>(ToggleFavorite);
        ClearAlertsCommand = new RelayCommand<object>(_ => Alerts.Clear());
        ExportSessionReviewCommand = new RelayCommand<object>(_ => _ = ExportSessionReviewAsync());
        LoadReplayCommand = new RelayCommand<object>(_ => _ = LoadReplayAsync());
        ReplayStepCommand = new RelayCommand<object>(_ => ReplayStep());
        ReplayPlayPauseCommand = new RelayCommand<object>(_ => ToggleReplayPlayback());
        ResumeLiveCommand = new RelayCommand<object>(_ => ResumeLive());
        OpenTradingViewChartCommand = new RelayCommand<object>(_ => OpenTradingViewChart());
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        replayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        replayTimer.Tick += (_, _) => ReplayStep();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CatalogInstrumentViewModel> Instruments { get; } = [];

    public ObservableCollection<CatalogInstrumentViewModel> FilteredInstruments { get; } = [];

    public ObservableCollection<OptionStrikeSnapshot> Strikes { get; } = [];

    public ObservableCollection<OptionChainRowViewModel> StrikeRows { get; } = [];

    public ObservableCollection<MarketAlertViewModel> Alerts { get; } = [];

    public ObservableCollection<MovementTimelineEntryViewModel> MovementTimeline { get; } = [];

    public string DataSourceName { get; }

    public IReadOnlyList<CatalogFilter> CatalogFilters { get; } =
    [
        CatalogFilter.All,
        CatalogFilter.Favorites,
        CatalogFilter.Index,
        CatalogFilter.Stock,
        CatalogFilter.Commodity
    ];

    public ICommand ToggleFavoriteCommand { get; }

    public ICommand ClearAlertsCommand { get; }

    public ICommand ExportSessionReviewCommand { get; }

    public ICommand LoadReplayCommand { get; }

    public ICommand ReplayStepCommand { get; }

    public ICommand ReplayPlayPauseCommand { get; }

    public ICommand ResumeLiveCommand { get; }

    public ICommand OpenTradingViewChartCommand { get; }

    public string Status
    {
        get => status;
        private set => SetField(ref status, value);
    }

    public string SessionStatus
    {
        get => sessionStatus;
        private set => SetField(ref sessionStatus, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetField(ref isBusy, value);
    }

    public string InstrumentSearchText
    {
        get => instrumentSearchText;
        set
        {
            if (SetField(ref instrumentSearchText, value))
            {
                ApplyInstrumentFilter();
            }
        }
    }

    public CatalogFilter SelectedCatalogFilter
    {
        get => selectedCatalogFilter;
        set
        {
            if (SetField(ref selectedCatalogFilter, value))
            {
                ApplyInstrumentFilter();
            }
        }
    }

    public bool AlertsEnabled
    {
        get => preferences.AlertSettings.IsEnabled;
        set
        {
            if (preferences.AlertSettings.IsEnabled == value)
            {
                return;
            }

            preferences.AlertSettings.IsEnabled = value;
            OnPropertyChanged();
            SaveAlertPreferences();
        }
    }

    public long OiBuildupThreshold
    {
        get => preferences.AlertSettings.OiBuildupThreshold;
        set
        {
            var normalized = Math.Clamp(value, 1_000, 10_000_000);
            if (preferences.AlertSettings.OiBuildupThreshold == normalized)
            {
                return;
            }

            preferences.AlertSettings.OiBuildupThreshold = normalized;
            OnPropertyChanged();
            SaveAlertPreferences();
        }
    }

    public decimal SupportResistanceProximityPoints
    {
        get => preferences.AlertSettings.SupportResistanceProximityPoints;
        set
        {
            var normalized = Math.Clamp(value, 1, 1_000);
            if (preferences.AlertSettings.SupportResistanceProximityPoints == normalized)
            {
                return;
            }

            preferences.AlertSettings.SupportResistanceProximityPoints = normalized;
            OnPropertyChanged();
            SaveAlertPreferences();
        }
    }

    public int AlertCooldownSeconds
    {
        get => preferences.AlertSettings.CooldownSeconds;
        set
        {
            var normalized = Math.Clamp(value, 5, 600);
            if (preferences.AlertSettings.CooldownSeconds == normalized)
            {
                return;
            }

            preferences.AlertSettings.CooldownSeconds = normalized;
            OnPropertyChanged();
            SaveAlertPreferences();
        }
    }

    public CatalogInstrumentViewModel? SelectedInstrument
    {
        get => selectedInstrument;
        set
        {
            if (SetField(ref selectedInstrument, value) && value is not null)
            {
                preferences.LastSelectedSymbol = value.Symbol;
                preferencesStore.Save(preferences);
                lastRecordedSnapshotTime = null;
                hasSessionBackfill = false;
                lastMovementTimelineTitle = null;
                StopReplay(clearRecords: true);
                MovementTimeline.Clear();
                UpdateSessionReview([], []);
                _ = LoadSessionBackfillAsync(value.Symbol);
                _ = LoadMovementTimelineAsync(value.Symbol);
                _ = RefreshAsync();
            }
        }
    }

    public OptionStrikeSnapshot? SelectedStrike
    {
        get => selectedStrike;
        set
        {
            if (SetField(ref selectedStrike, value))
            {
                OnPropertyChanged(nameof(SelectedStrikeLabel));
                OnPropertyChanged(nameof(SelectedStrikeDetail));
                OnPropertyChanged(nameof(SelectedStrikeChartTitle));
                OnPropertyChanged(nameof(MarketSignalText));
                OnPropertyChanged(nameof(MarketSignalDetail));
                OnPropertyChanged(nameof(MarketSignalForeground));
                UpdateMovementReading(CurrentPricePoints(), CurrentOiPoints());
                UpdateSessionReview(CurrentPricePoints(), CurrentOiPoints());
                UpdateSelectedStrikeOiChartSeries();
            }
        }
    }

    public OptionChainRowViewModel? SelectedStrikeRow
    {
        get => SelectedStrike is null
            ? null
            : StrikeRows.FirstOrDefault(row => row.Strike == SelectedStrike.Strike);
        set
        {
            SelectedStrike = value?.Snapshot;
            OnPropertyChanged();
        }
    }

    public MarketSnapshot? Snapshot
    {
        get => snapshot;
        private set
        {
            if (SetField(ref snapshot, value))
            {
                OnPropertyChanged(nameof(SymbolTitle));
                OnPropertyChanged(nameof(SpotText));
                OnPropertyChanged(nameof(SpotChangeText));
                OnPropertyChanged(nameof(SpotChangeForeground));
                OnPropertyChanged(nameof(PcrOiText));
                OnPropertyChanged(nameof(PcrVolumeText));
                OnPropertyChanged(nameof(VolumeShareText));
                OnPropertyChanged(nameof(LastUpdatedText));
                OnPropertyChanged(nameof(MarketSignalText));
                OnPropertyChanged(nameof(MarketSignalDetail));
                OnPropertyChanged(nameof(MarketSignalForeground));
            }
        }
    }

    public PointCollection PricePath { get; } = [];

    public PointCollection OiPath { get; } = [];

    public IReadOnlyList<ChartSeriesViewModel> PriceChartSeries
    {
        get => priceChartSeries;
        private set => SetField(ref priceChartSeries, value);
    }

    public IReadOnlyList<ChartSeriesViewModel> OiChartSeries
    {
        get => oiChartSeries;
        private set => SetField(ref oiChartSeries, value);
    }

    public IReadOnlyList<ChartSeriesViewModel> SelectedStrikeOiChartSeries
    {
        get => selectedStrikeOiChartSeries;
        private set => SetField(ref selectedStrikeOiChartSeries, value);
    }

    public string SymbolTitle => Snapshot is null ? "Market Analyser" : $"{Snapshot.Symbol}-AI";

    public string SpotText => Snapshot is null ? "--" : FormatNumber(Snapshot.Spot);

    public string SpotChangeText => Snapshot is null ? string.Empty : $"{(Snapshot.SpotChange >= 0 ? "+" : string.Empty)}{Snapshot.SpotChange:N2}";

    public Brush SpotChangeForeground => Snapshot?.SpotChange switch
    {
        > 0 => Brushes.MediumSeaGreen,
        < 0 => Brushes.IndianRed,
        _ => Brushes.LightSlateGray
    };

    public string SessionOpenMoveText
    {
        get => sessionOpenMoveText;
        private set => SetField(ref sessionOpenMoveText, value);
    }

    public string SessionRangeText
    {
        get => sessionRangeText;
        private set => SetField(ref sessionRangeText, value);
    }

    public string SessionTrendText
    {
        get => sessionTrendText;
        private set => SetField(ref sessionTrendText, value);
    }

    public string SessionOiShiftText
    {
        get => sessionOiShiftText;
        private set => SetField(ref sessionOiShiftText, value);
    }

    public Brush SessionTrendForeground
    {
        get => sessionTrendForeground;
        private set => SetField(ref sessionTrendForeground, value);
    }

    public Brush SessionOiShiftForeground
    {
        get => sessionOiShiftForeground;
        private set => SetField(ref sessionOiShiftForeground, value);
    }

    public string MovementReadingTitle
    {
        get => movementReadingTitle;
        private set => SetField(ref movementReadingTitle, value);
    }

    public string MovementReadingDetail
    {
        get => movementReadingDetail;
        private set => SetField(ref movementReadingDetail, value);
    }

    public string MovementReadingWatchText
    {
        get => movementReadingWatchText;
        private set => SetField(ref movementReadingWatchText, value);
    }

    public Brush MovementReadingForeground
    {
        get => movementReadingForeground;
        private set => SetField(ref movementReadingForeground, value);
    }

    public string SessionReviewSummaryText
    {
        get => sessionReviewSummaryText;
        private set => SetField(ref sessionReviewSummaryText, value);
    }

    public string SessionReviewPhasesText
    {
        get => sessionReviewPhasesText;
        private set => SetField(ref sessionReviewPhasesText, value);
    }

    public string SessionReviewOiText
    {
        get => sessionReviewOiText;
        private set => SetField(ref sessionReviewOiText, value);
    }

    public string SessionReviewExportText
    {
        get => sessionReviewExportText;
        private set => SetField(ref sessionReviewExportText, value);
    }

    public string ReplayOutcomeText
    {
        get => replayOutcomeText;
        private set => SetField(ref replayOutcomeText, value);
    }

    public Brush ReplayOutcomeForeground
    {
        get => replayOutcomeForeground;
        private set => SetField(ref replayOutcomeForeground, value);
    }

    public DateTime? ReplaySelectedDate
    {
        get => replaySelectedDate;
        set => SetField(ref replaySelectedDate, value);
    }

    public string ReplayStatusText
    {
        get => replayStatusText;
        private set => SetField(ref replayStatusText, value);
    }

    public string ReplayPlayPauseText => isReplayPlaying ? "Pause" : "Play";

    public string LiveModeText => isReplayMode ? "Replay" : "Live";

    public string PcrOiText => Snapshot is null ? "--" : Snapshot.Breadth.PutCallRatioOi.ToString("N2", CultureInfo.CurrentCulture);

    public string PcrVolumeText => Snapshot is null ? "--" : Snapshot.Breadth.PutCallRatioVolume.ToString("N2", CultureInfo.CurrentCulture);

    public string VolumeShareText => Snapshot is null
        ? "--"
        : $"{Snapshot.Breadth.CeVolumeShare:N0}% / {Snapshot.Breadth.PeVolumeShare:N0}%";

    public string LastUpdatedText => Snapshot is null ? "--" : Snapshot.Timestamp.ToLocalTime().ToString("HH:mm:ss");

    public string MarketSignalText => BuildMarketSignal().Label;

    public string MarketSignalDetail => BuildMarketSignal().Detail;

    public Brush MarketSignalForeground => BuildMarketSignal().Foreground;

    public string SelectedStrikeLabel => SelectedStrike is null ? "Select a strike" : FormatNumber(SelectedStrike.Strike);

    public string SelectedStrikeDetail => SelectedStrike is null
        ? "Support, resistance and Greeks will appear here."
        : $"Support {FormatNumber(SelectedStrike.Support)} | Resistance {FormatNumber(SelectedStrike.Resistance)} | Delta CE/PE {SelectedStrike.Call.Delta:N4}/{SelectedStrike.Put.Delta:N4} | IV CE/PE {SelectedStrike.Call.ImpliedVolatility:N2}/{SelectedStrike.Put.ImpliedVolatility:N2}";

    public string SelectedStrikeChartTitle => SelectedStrike is null
        ? "Selected Strike OI"
        : $"{FormatNumber(SelectedStrike.Strike)} Strike OI";

    public async Task StartAsync()
    {
        Status = "Loading instruments";
        preferences = preferencesStore.Load();
        OnPropertyChanged(nameof(AlertsEnabled));
        OnPropertyChanged(nameof(OiBuildupThreshold));
        OnPropertyChanged(nameof(SupportResistanceProximityPoints));
        OnPropertyChanged(nameof(AlertCooldownSeconds));
        var instruments = await marketDataSource.GetInstrumentsAsync(CancellationToken.None);

        Instruments.Clear();
        foreach (var instrument in instruments)
        {
            Instruments.Add(new CatalogInstrumentViewModel(instrument, EffectiveFavorite(instrument)));
        }

        ApplyInstrumentFilter();
        SelectedInstrument = FilteredInstruments.FirstOrDefault(item =>
            string.Equals(item.Symbol, preferences.LastSelectedSymbol, StringComparison.OrdinalIgnoreCase)) ??
            FilteredInstruments.FirstOrDefault(item => item.Symbol == "NIFTY") ??
            FilteredInstruments.FirstOrDefault();
        refreshTimer.Start();
    }

    public async Task RefreshAsync()
    {
        if (isReplayMode || SelectedInstrument is null || !await refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            IsBusy = true;
            Status = "Refreshing";
            var next = await marketDataSource.GetSnapshotAsync(SelectedInstrument.Symbol, CancellationToken.None);
            ApplySnapshot(next);
            await RecordSnapshotAsync(next);
            Status = $"Live via {DataSourceName}";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
            refreshGate.Release();
        }
    }

    private void ApplyInstrumentFilter()
    {
        var query = InstrumentSearchText.Trim();
        var filtered = Instruments
            .Where(item => MatchesFilter(item, SelectedCatalogFilter))
            .Where(item => string.IsNullOrWhiteSpace(query) ||
                item.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.IsFavorite)
            .ThenBy(item => item.SegmentLabel)
            .ThenBy(item => item.DisplayName)
            .ToList();

        FilteredInstruments.Clear();
        foreach (var item in filtered)
        {
            FilteredInstruments.Add(item);
        }
    }

    private async Task LoadSessionBackfillAsync(string symbol)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var backfill = await sessionStore.LoadBackfillAsync(symbol, today, CancellationToken.None);
            if (SelectedInstrument?.Symbol != symbol)
            {
                return;
            }

            hasSessionBackfill = backfill.RecordCount > 0;
            if (backfill.MissingRanges.Count > 0)
            {
                var fetched = await RequestHistoricalBackfillAsync(symbol, backfill.MissingRanges);
                if (fetched > 0)
                {
                    backfill = await sessionStore.LoadBackfillAsync(symbol, today, CancellationToken.None);
                    hasSessionBackfill = backfill.RecordCount > 0;
                }
            }

            if (hasSessionBackfill)
            {
                PriceChartSeries =
                [
                    new ChartSeriesViewModel("Spot", backfill.PriceSeries, Brushes.MediumSeaGreen)
                ];
                OiChartSeries =
                [
                    new ChartSeriesViewModel("PE - CE OI Chg", backfill.OiChangeSeries, Brushes.CornflowerBlue)
                ];
                lastRecordedSnapshotTime = backfill.LastTimestamp;
            }

            SessionStatus = BuildSessionStatus(symbol, backfill);
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            SessionStatus = "Local session backfill unavailable";
        }
    }

    private async Task<int> RequestHistoricalBackfillAsync(
        string symbol,
        IReadOnlyList<MarketSessionMissingRange> missingRanges)
    {
        var fetched = 0;
        foreach (var range in missingRanges)
        {
            var snapshots = await historicalDataSource.GetSnapshotsAsync(
                symbol,
                range.From,
                range.To,
                CancellationToken.None);

            if (snapshots.Count == 0)
            {
                continue;
            }

            foreach (var snapshot in snapshots.OrderBy(item => item.Timestamp))
            {
                await sessionStore.AppendAsync(
                    snapshot,
                    SelectedStrike,
                    BuildMarketSignal(snapshot, SelectedStrike),
                    CancellationToken.None);
                fetched++;
            }
        }

        return fetched;
    }

    private static string BuildSessionStatus(string symbol, MarketSessionBackfill backfill)
    {
        var missingText = backfill.MissingRanges.Count switch
        {
            0 => "no detected local gaps",
            1 => $"1 gap {FormatTimeRange(backfill.MissingRanges[0])}",
            _ => $"{backfill.MissingRanges.Count} gaps, first {FormatTimeRange(backfill.MissingRanges[0])}"
        };

        return backfill.RecordCount == 0
            ? $"Backfill needed for {symbol}: {missingText}"
            : $"Loaded {backfill.RecordCount:N0} local records for {symbol} through {backfill.LastTimestamp?.ToLocalTime():HH:mm:ss}; {missingText}";
    }

    private static string FormatTimeRange(MarketSessionMissingRange range)
    {
        return $"{range.From.ToLocalTime():HH:mm}-{range.To.ToLocalTime():HH:mm}";
    }

    private async Task RecordSnapshotAsync(MarketSnapshot next)
    {
        if (lastRecordedSnapshotTime == next.Timestamp)
        {
            return;
        }

        try
        {
            await sessionStore.AppendAsync(next, SelectedStrike, BuildMarketSignal(), CancellationToken.None);
            lastRecordedSnapshotTime = next.Timestamp;

            var today = DateOnly.FromDateTime(next.Timestamp.ToLocalTime().DateTime);
            var count = await sessionStore.CountRecordsAsync(next.Symbol, today, CancellationToken.None);
            SessionStatus = $"Recording {next.Symbol}: {count:N0} local records today";
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            SessionStatus = "Local session recording failed";
        }
    }

    private bool EffectiveFavorite(InstrumentSummary instrument)
    {
        return preferences.FavoriteOverrides.TryGetValue(instrument.Symbol, out var overrideValue)
            ? overrideValue
            : instrument.IsFavorite;
    }

    private void ToggleFavorite(CatalogInstrumentViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var nextValue = !item.IsFavorite;
        item.IsFavorite = nextValue;

        if (nextValue == item.Source.IsFavorite)
        {
            preferences.FavoriteOverrides.Remove(item.Symbol);
        }
        else
        {
            preferences.FavoriteOverrides[item.Symbol] = nextValue;
        }

        preferencesStore.Save(preferences);
        ApplyInstrumentFilter();
    }

    private void SaveAlertPreferences()
    {
        preferencesStore.Save(preferences);
        recentAlertKeys.Clear();
    }

    private static bool MatchesFilter(CatalogInstrumentViewModel instrument, CatalogFilter filter)
    {
        return filter switch
        {
            CatalogFilter.All => true,
            CatalogFilter.Favorites => instrument.IsFavorite,
            CatalogFilter.Index => instrument.Source.Segment is MarketSegment.Nifty or
                MarketSegment.BankNifty or
                MarketSegment.Sensex or
                MarketSegment.FinNifty,
            CatalogFilter.Stock => instrument.Source.Segment == MarketSegment.Stock,
            CatalogFilter.Commodity => instrument.Source.Segment == MarketSegment.Commodity,
            _ => true
        };
    }

    private void ApplySnapshot(MarketSnapshot next, bool recordTimeline = true)
    {
        Snapshot = next;
        var previousSelectedStrike = SelectedStrike?.Strike;
        Strikes.Clear();
        foreach (var strike in next.Strikes)
        {
            Strikes.Add(strike);
        }

        var maxCallOi = next.Strikes.Count == 0 ? 0 : next.Strikes.Max(strike => strike.Call.OpenInterest);
        var maxPutOi = next.Strikes.Count == 0 ? 0 : next.Strikes.Max(strike => strike.Put.OpenInterest);
        var maxCallOiChange = next.Strikes.Count == 0 ? 0 : next.Strikes.Max(strike => Math.Abs(strike.Call.OpenInterestChange));
        var maxPutOiChange = next.Strikes.Count == 0 ? 0 : next.Strikes.Max(strike => Math.Abs(strike.Put.OpenInterestChange));

        var atmStrike = next.Strikes
            .OrderBy(strike => Math.Abs(strike.Strike - next.Spot))
            .FirstOrDefault()
            ?.Strike;

        StrikeRows.Clear();
        foreach (var strike in next.Strikes)
        {
            StrikeRows.Add(new OptionChainRowViewModel(
                strike,
                atmStrike,
                maxCallOi,
                maxPutOi,
                maxCallOiChange,
                maxPutOiChange));
        }

        SelectedStrike = Strikes.FirstOrDefault(strike => strike.Strike == previousSelectedStrike) ??
            Strikes.OrderBy(strike => Math.Abs(strike.Strike - next.Spot))
            .FirstOrDefault();
        OnPropertyChanged(nameof(SelectedStrikeRow));

        UpdatePath(PricePath, next.PriceSeries, 360, 138);
        UpdatePath(OiPath, next.OiChangeSeries, 360, 118);
        OnPropertyChanged(nameof(PricePath));
        OnPropertyChanged(nameof(OiPath));

        var pricePoints = MergeChartPoints(hasSessionBackfill ? PriceChartSeries.FirstOrDefault()?.Points : null, next.PriceSeries);
        var oiPoints = MergeChartPoints(hasSessionBackfill ? OiChartSeries.FirstOrDefault()?.Points : null, next.OiChangeSeries);
        PriceChartSeries =
        [
            new ChartSeriesViewModel("Spot", pricePoints, Brushes.MediumSeaGreen)
        ];
        OiChartSeries =
        [
            new ChartSeriesViewModel("PE - CE OI Chg", oiPoints, Brushes.CornflowerBlue)
        ];
        hasSessionBackfill = PriceChartSeries.First().Points.Count > next.PriceSeries.Count;
        UpdateSessionAnalysis(pricePoints, oiPoints);
        UpdateMovementReading(pricePoints, oiPoints);
        if (recordTimeline)
        {
            RecordMovementTimeline(next);
        }
        UpdateSessionReview(pricePoints, oiPoints);
        UpdateSelectedStrikeOiChartSeries();
        EvaluateAlerts(next);
    }

    private async Task LoadMovementTimelineAsync(string symbol)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var records = await movementTimelineStore.LoadAsync(symbol, today, CancellationToken.None);
            if (SelectedInstrument?.Symbol != symbol)
            {
                return;
            }

            MovementTimeline.Clear();
            foreach (var record in records.TakeLast(25).Reverse())
            {
                MovementTimeline.Add(MovementTimelineEntryViewModel.FromRecord(record));
            }

            lastMovementTimelineTitle = records.LastOrDefault()?.Title;
            UpdateSessionReview(CurrentPricePoints(), CurrentOiPoints());
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
        }
    }

    private void RecordMovementTimeline(MarketSnapshot next)
    {
        if (string.IsNullOrWhiteSpace(MovementReadingTitle) ||
            MovementReadingTitle == "Waiting" ||
            string.Equals(lastMovementTimelineTitle, MovementReadingTitle, StringComparison.Ordinal))
        {
            return;
        }

        lastMovementTimelineTitle = MovementReadingTitle;
        var record = new MovementTimelineRecord(
            next.Symbol,
            next.Timestamp,
            MovementReadingTitle,
            MovementReadingDetail,
            MovementReadingWatchText,
            next.Spot,
            next.SpotChange);

        MovementTimeline.Insert(0, MovementTimelineEntryViewModel.FromRecord(record));
        while (MovementTimeline.Count > 25)
        {
            MovementTimeline.RemoveAt(MovementTimeline.Count - 1);
        }

        _ = PersistMovementTimelineAsync(record);
        UpdateSessionReview(CurrentPricePoints(), CurrentOiPoints());
    }

    private async Task PersistMovementTimelineAsync(MovementTimelineRecord record)
    {
        try
        {
            await movementTimelineStore.AppendAsync(record, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
        }
    }

    private void UpdateSessionAnalysis(
        IReadOnlyList<ChartPoint> pricePoints,
        IReadOnlyList<ChartPoint> oiPoints)
    {
        if (pricePoints.Count == 0)
        {
            SessionOpenMoveText = "--";
            SessionRangeText = "--";
            SessionTrendText = "Waiting";
            SessionOiShiftText = "--";
            SessionTrendForeground = Brushes.LightSlateGray;
            SessionOiShiftForeground = Brushes.LightSlateGray;
            return;
        }

        var first = pricePoints.First().Value;
        var last = pricePoints.Last().Value;
        var high = pricePoints.Max(point => point.Value);
        var low = pricePoints.Min(point => point.Value);
        var openMove = last - first;
        var range = high - low;
        var retraceFromHigh = high - last;
        var bounceFromLow = last - low;

        SessionOpenMoveText = FormatSigned(openMove);
        SessionRangeText = $"{range:N2} ({low:N2}-{high:N2})";

        if (openMove > 0 && retraceFromHigh <= range * 0.35m)
        {
            SessionTrendText = $"Uptrend, near high by {retraceFromHigh:N2}";
            SessionTrendForeground = Brushes.MediumSeaGreen;
        }
        else if (openMove < 0 && bounceFromLow <= range * 0.35m)
        {
            SessionTrendText = $"Downtrend, off low by {bounceFromLow:N2}";
            SessionTrendForeground = Brushes.IndianRed;
        }
        else
        {
            SessionTrendText = "Sideways / mean reversion";
            SessionTrendForeground = Brushes.Goldenrod;
        }

        var oiShift = oiPoints.Count == 0 ? 0 : oiPoints.Last().Value - oiPoints.First().Value;
        SessionOiShiftText = FormatSigned(oiShift);
        SessionOiShiftForeground = oiShift switch
        {
            > 0 => Brushes.MediumSeaGreen,
            < 0 => Brushes.IndianRed,
            _ => Brushes.LightSlateGray
        };
    }

    private void UpdateMovementReading(
        IReadOnlyList<ChartPoint> pricePoints,
        IReadOnlyList<ChartPoint> oiPoints)
    {
        if (Snapshot is null)
        {
            MovementReadingTitle = "Waiting";
            MovementReadingDetail = "Live snapshot not loaded";
            MovementReadingWatchText = "--";
            MovementReadingForeground = Brushes.LightSlateGray;
            return;
        }

        var score = 0;
        var reasons = new List<string>();
        var expansion = DetectSharpMovement(pricePoints, oiPoints, Snapshot);
        if (Snapshot.SpotChange > 0)
        {
            score++;
            reasons.Add($"day +{Snapshot.SpotChange:N2}");
        }
        else if (Snapshot.SpotChange < 0)
        {
            score--;
            reasons.Add($"day {Snapshot.SpotChange:N2}");
        }

        if (pricePoints.Count > 1)
        {
            var first = pricePoints.First().Value;
            var last = pricePoints.Last().Value;
            var high = pricePoints.Max(point => point.Value);
            var low = pricePoints.Min(point => point.Value);
            var range = high - low;
            var fromOpen = last - first;
            if (fromOpen > 0)
            {
                score++;
            }
            else if (fromOpen < 0)
            {
                score--;
            }

            if (range > 0)
            {
                var position = (last - low) / range;
                if (position >= 0.70m)
                {
                    score++;
                    reasons.Add("near session high");
                }
                else if (position <= 0.30m)
                {
                    score--;
                    reasons.Add("near session low");
                }
                else
                {
                    reasons.Add("mid-range");
                }
            }
        }

        var oiShift = oiPoints.Count == 0 ? 0 : oiPoints.Last().Value - oiPoints.First().Value;
        if (oiShift > 0)
        {
            score++;
            reasons.Add("PE OI shift positive");
        }
        else if (oiShift < 0)
        {
            score--;
            reasons.Add("CE OI shift stronger");
        }

        if (Snapshot.Breadth.PutCallRatioOi >= 1.15m)
        {
            score++;
            reasons.Add($"PCR OI {Snapshot.Breadth.PutCallRatioOi:N2}");
        }
        else if (Snapshot.Breadth.PutCallRatioOi <= 0.85m)
        {
            score--;
            reasons.Add($"PCR OI {Snapshot.Breadth.PutCallRatioOi:N2}");
        }

        if (expansion is not null)
        {
            MovementReadingTitle = expansion.Title;
            MovementReadingForeground = expansion.Foreground;
            MovementReadingDetail = expansion.Detail;
            MovementReadingWatchText = BuildMovementWatchText();
            return;
        }

        MovementReadingTitle = score >= 3
            ? "Bullish continuation"
            : score <= -3
                ? "Bearish continuation"
                : score > 0
                    ? "Bullish, watch pullbacks"
                    : score < 0
                        ? "Bearish, watch bounces"
                        : "Mixed / wait";
        MovementReadingForeground = score > 0
            ? Brushes.MediumSeaGreen
            : score < 0
                ? Brushes.IndianRed
                : Brushes.Goldenrod;
        MovementReadingDetail = reasons.Count == 0
            ? "No clear movement pressure yet"
            : string.Join(" | ", reasons.Take(4));
        MovementReadingWatchText = BuildMovementWatchText();
    }

    private string BuildMovementWatchText()
    {
        return SelectedStrike is null
            ? "Select a strike for support/resistance watch"
            : $"Watch support {SelectedStrike.Support:N2} and resistance {SelectedStrike.Resistance:N2}";
    }

    private static SharpMovementViewModel? DetectSharpMovement(
        IReadOnlyList<ChartPoint> pricePoints,
        IReadOnlyList<ChartPoint> oiPoints,
        MarketSnapshot snapshot)
    {
        if (pricePoints.Count < 2)
        {
            return null;
        }

        var latest = pricePoints.Last();
        var move10 = MoveSince(pricePoints, latest.Time - TimeSpan.FromMinutes(10));
        var move20 = MoveSince(pricePoints, latest.Time - TimeSpan.FromMinutes(20));
        var previousPoints = pricePoints.Take(Math.Max(0, pricePoints.Count - 1)).ToArray();
        var priorHigh = previousPoints.Length == 0 ? latest.Value : previousPoints.Max(point => point.Value);
        var priorLow = previousPoints.Length == 0 ? latest.Value : previousPoints.Min(point => point.Value);
        var breaksHigh = latest.Value >= priorHigh;
        var breaksLow = latest.Value <= priorLow;
        var oiShift = oiPoints.Count == 0 ? 0 : oiPoints.Last().Value - oiPoints.First().Value;
        var bullishOptionConfirm = oiShift > 0 || snapshot.Breadth.PutCallRatioOi >= 1.05m;
        var bearishOptionConfirm = oiShift < 0 || snapshot.Breadth.PutCallRatioOi <= 0.95m;
        var strongUp = move20 >= 150m || move10 >= 75m;
        var strongDown = move20 <= -150m || move10 <= -75m;

        if (strongUp && breaksHigh && bullishOptionConfirm)
        {
            return new SharpMovementViewModel(
                move20 >= 150m ? "Sharp Bullish Expansion" : "Fast Bullish Break",
                BuildSharpMovementDetail(move10, move20, "new session high", oiShift, snapshot.Breadth.PutCallRatioOi),
                Brushes.MediumSeaGreen);
        }

        if (strongDown && breaksLow && bearishOptionConfirm)
        {
            return new SharpMovementViewModel(
                move20 <= -150m ? "Sharp Bearish Expansion" : "Fast Bearish Break",
                BuildSharpMovementDetail(move10, move20, "new session low", oiShift, snapshot.Breadth.PutCallRatioOi),
                Brushes.IndianRed);
        }

        return null;
    }

    private static decimal MoveSince(IReadOnlyList<ChartPoint> pricePoints, DateTimeOffset from)
    {
        var start = pricePoints
            .Where(point => point.Time >= from)
            .OrderBy(point => point.Time)
            .FirstOrDefault();

        return start is null ? 0 : pricePoints.Last().Value - start.Value;
    }

    private static string BuildSharpMovementDetail(
        decimal move10,
        decimal move20,
        string rangeBreak,
        decimal oiShift,
        decimal pcrOi)
    {
        var pressure = oiShift > 0
            ? "PE OI shift positive"
            : oiShift < 0
                ? "CE OI shift stronger"
                : "OI balanced";

        return $"10m {FormatSigned(move10)} | 20m {FormatSigned(move20)} | {rangeBreak} | {pressure} | PCR OI {pcrOi:N2}";
    }

    private void UpdateSessionReview(
        IReadOnlyList<ChartPoint> pricePoints,
        IReadOnlyList<ChartPoint> oiPoints)
    {
        if (pricePoints.Count == 0)
        {
            SessionReviewSummaryText = "Waiting for session data";
            SessionReviewPhasesText = "Timeline will appear as readings change";
            SessionReviewOiText = "--";
            return;
        }

        var first = pricePoints.First();
        var last = pricePoints.Last();
        var high = pricePoints.MaxBy(point => point.Value)!;
        var low = pricePoints.MinBy(point => point.Value)!;
        var netMove = last.Value - first.Value;
        var timeline = MovementTimeline.Reverse().ToArray();
        var finalReading = timeline.LastOrDefault()?.Title ?? MovementReadingTitle;

        SessionReviewSummaryText =
            $"Open {first.Value:N2}, high {high.Value:N2} at {high.Time.ToLocalTime():HH:mm}, low {low.Value:N2} at {low.Time.ToLocalTime():HH:mm}, latest {last.Value:N2}; net {FormatSigned(netMove)}. Final: {finalReading}.";

        SessionReviewPhasesText = timeline.Length == 0
            ? "No reading changes recorded yet"
            : string.Join(" -> ", timeline.TakeLast(5).Select(item => $"{item.TimeText} {item.Title}"));

        if (oiPoints.Count == 0)
        {
            SessionReviewOiText = "OI pressure not available yet";
        }
        else
        {
            var oiShift = oiPoints.Last().Value - oiPoints.First().Value;
            var pressure = oiShift > 0 ? "PE pressure gained" : oiShift < 0 ? "CE pressure gained" : "OI pressure balanced";
            SessionReviewOiText = $"{pressure}; shift {FormatSigned(oiShift)}";
        }
    }

    private async Task ExportSessionReviewAsync()
    {
        if (SelectedInstrument is null)
        {
            SessionReviewExportText = "Select a symbol first";
            return;
        }

        try
        {
            var report = BuildSessionReviewReport();
            var today = DateOnly.FromDateTime(DateTime.Now);
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MarketAnalyser",
                "reports",
                today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);

            var safeSymbol = string.Concat(SelectedInstrument.Symbol.Where(char.IsLetterOrDigit)).ToUpperInvariant();
            var path = Path.Combine(directory, $"{safeSymbol}-session-review.txt");
            await File.WriteAllTextAsync(path, report);
            SessionReviewExportText = $"Saved {path}";
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            SessionReviewExportText = "Export failed";
        }
    }

    private string BuildSessionReviewReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{SelectedInstrument?.Symbol ?? "Market"} Session Review");
        builder.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
        builder.AppendLine();
        builder.AppendLine("Summary");
        builder.AppendLine(SessionReviewSummaryText);
        builder.AppendLine();
        builder.AppendLine("Movement Phases");
        builder.AppendLine(SessionReviewPhasesText);
        builder.AppendLine();
        builder.AppendLine("OI Pressure");
        builder.AppendLine(SessionReviewOiText);
        builder.AppendLine();
        builder.AppendLine("Replay Outcome");
        builder.AppendLine(ReplayOutcomeText);
        builder.AppendLine();
        builder.AppendLine("Current Reading");
        builder.AppendLine(MovementReadingTitle);
        builder.AppendLine(MovementReadingDetail);
        builder.AppendLine(MovementReadingWatchText);
        builder.AppendLine();
        builder.AppendLine("Timeline");

        foreach (var item in MovementTimeline.Reverse())
        {
            builder.AppendLine($"{item.TimeText} | {item.Title} | {item.SpotText} | {item.Detail} | {item.WatchText}");
        }

        return builder.ToString();
    }

    private async Task LoadReplayAsync()
    {
        if (SelectedInstrument is null || ReplaySelectedDate is null)
        {
            ReplayStatusText = "Select symbol and date";
            return;
        }

        try
        {
            var date = DateOnly.FromDateTime(ReplaySelectedDate.Value);
            var records = await sessionStore.LoadRecordsAsync(SelectedInstrument.Symbol, date, CancellationToken.None);
            if (records.Count == 0)
            {
                StopReplay(clearRecords: true);
                ReplayStatusText = $"No replay records for {date:yyyy-MM-dd}";
                ReplayOutcomeText = "No replay records to evaluate";
                ReplayOutcomeForeground = Brushes.LightSlateGray;
                return;
            }

            replayRecords = records;
            replayIndex = -1;
            isReplayMode = true;
            refreshTimer.Stop();
            OnPropertyChanged(nameof(LiveModeText));
            ReplayStatusText = $"Loaded {records.Count:N0} records for {date:yyyy-MM-dd}";
            ReplayStep();
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            ReplayStatusText = "Replay load failed";
        }
    }

    private void ReplayStep()
    {
        if (replayRecords.Count == 0)
        {
            ReplayStatusText = "Replay not loaded";
            StopReplay(clearRecords: false);
            return;
        }

        if (replayIndex >= replayRecords.Count - 1)
        {
            isReplayPlaying = false;
            replayTimer.Stop();
            OnPropertyChanged(nameof(ReplayPlayPauseText));
            ReplayStatusText = "Replay finished";
            return;
        }

        replayIndex++;
        var snapshot = BuildReplaySnapshot(replayIndex);
        ApplySnapshot(snapshot, recordTimeline: false);
        UpdateReplayOutcome();
        ReplayStatusText = $"{replayIndex + 1:N0}/{replayRecords.Count:N0} {snapshot.Timestamp.ToLocalTime():HH:mm:ss}";
    }

    private void ToggleReplayPlayback()
    {
        if (!isReplayMode || replayRecords.Count == 0)
        {
            _ = LoadReplayAsync();
            return;
        }

        isReplayPlaying = !isReplayPlaying;
        if (isReplayPlaying)
        {
            replayTimer.Start();
        }
        else
        {
            replayTimer.Stop();
        }

        OnPropertyChanged(nameof(ReplayPlayPauseText));
    }

    private void ResumeLive()
    {
        StopReplay(clearRecords: true);
        refreshTimer.Start();
        _ = RefreshAsync();
    }

    private void StopReplay(bool clearRecords)
    {
        isReplayPlaying = false;
        isReplayMode = false;
        replayTimer.Stop();
        replayIndex = -1;
        if (clearRecords)
        {
            replayRecords = [];
        }

        ReplayOutcomeText = "Load replay to evaluate follow-through";
        ReplayOutcomeForeground = Brushes.LightSlateGray;
        OnPropertyChanged(nameof(ReplayPlayPauseText));
        OnPropertyChanged(nameof(LiveModeText));
    }

    private void OpenTradingViewChart()
    {
        if (SelectedInstrument is null)
        {
            Status = "Select a symbol to open TradingView chart";
            return;
        }

        var window = new MarketAnalyser.App.TradingViewChartWindow(
            SelectedInstrument.Symbol,
            MapToTradingViewSymbol(SelectedInstrument));
        window.Show();
    }

    private static string MapToTradingViewSymbol(CatalogInstrumentViewModel instrument)
    {
        var symbol = instrument.Symbol.Trim().ToUpperInvariant();
        return symbol switch
        {
            "NIFTY" or "NIFTY 50" => "NSE:NIFTY",
            "BANKNIFTY" or "NIFTY BANK" => "NSE:BANKNIFTY",
            "FINNIFTY" => "NSE:CNXFINANCE",
            "SENSEX" => "BSE:SENSEX",
            _ when instrument.Source.Segment == MarketSegment.Commodity => symbol,
            _ => $"NSE:{symbol}"
        };
    }

    private void UpdateReplayOutcome()
    {
        if (!isReplayMode || replayIndex < 0 || replayIndex >= replayRecords.Count)
        {
            ReplayOutcomeText = "Load replay to evaluate follow-through";
            ReplayOutcomeForeground = Brushes.LightSlateGray;
            return;
        }

        var current = replayRecords[replayIndex];
        var move5 = FutureMove(current, TimeSpan.FromMinutes(5));
        var move15 = FutureMove(current, TimeSpan.FromMinutes(15));
        var move30 = FutureMove(current, TimeSpan.FromMinutes(30));
        var direction = ReadingDirection(MovementReadingTitle);
        var primaryMove = move15 ?? move5 ?? move30;

        if (primaryMove is null)
        {
            ReplayOutcomeText = "Follow-through pending: not enough future replay data";
            ReplayOutcomeForeground = Brushes.LightSlateGray;
            return;
        }

        var outcome = direction switch
        {
            > 0 when primaryMove > 15 => "followed through",
            > 0 when primaryMove < -15 => "failed",
            < 0 when primaryMove < -15 => "followed through",
            < 0 when primaryMove > 15 => "failed",
            0 when Math.Abs(primaryMove.Value) <= 15 => "stayed flat",
            0 => "broke out",
            _ => "mixed"
        };

        ReplayOutcomeForeground = outcome switch
        {
            "followed through" or "stayed flat" => Brushes.MediumSeaGreen,
            "failed" or "broke out" => Brushes.IndianRed,
            _ => Brushes.Goldenrod
        };
        ReplayOutcomeText =
            $"{MovementReadingTitle} -> {outcome}; 5m {FormatOptionalMove(move5)}, 15m {FormatOptionalMove(move15)}, 30m {FormatOptionalMove(move30)}";
    }

    private decimal? FutureMove(MarketSessionRecord current, TimeSpan horizon)
    {
        var targetTime = current.Timestamp + horizon;
        var future = replayRecords
            .Where(record => record.Timestamp >= targetTime)
            .OrderBy(record => record.Timestamp)
            .FirstOrDefault();

        return future is null ? null : future.Spot - current.Spot;
    }

    private static int ReadingDirection(string title)
    {
        if (title.Contains("Bullish", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (title.Contains("Bearish", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return 0;
    }

    private static string FormatOptionalMove(decimal? value)
    {
        return value is null ? "--" : FormatSigned(value.Value);
    }

    private MarketSnapshot BuildReplaySnapshot(int index)
    {
        var record = replayRecords[index];
        var recordsToPoint = replayRecords.Take(index + 1).ToArray();
        var priceSeries = recordsToPoint
            .Select(item => new ChartPoint(item.Timestamp, item.Spot))
            .ToArray();
        var oiSeries = recordsToPoint
            .Select(item => new ChartPoint(item.Timestamp, item.Strikes.Sum(strike => strike.PutOpenInterestChange - strike.CallOpenInterestChange)))
            .ToArray();
        var strikes = record.Strikes
            .Select(ToReplayStrike)
            .OrderBy(strike => strike.Strike)
            .ToArray();

        return new MarketSnapshot(
            record.Symbol,
            record.Spot,
            record.SpotChange,
            record.Timestamp,
            strikes,
            new MarketBreadth(
                record.PutCallRatioOi,
                record.PutCallRatioVolume,
                record.CeVolumeShare,
                record.PeVolumeShare,
                record.TotalCallOi,
                record.TotalPutOi,
                record.TotalCallVolume,
                record.TotalPutVolume),
            priceSeries,
            oiSeries,
            BuildReplayStrikeHistory(recordsToPoint));
    }

    private static OptionStrikeSnapshot ToReplayStrike(MarketSessionStrikeRecord record)
    {
        return new OptionStrikeSnapshot(
            record.Strike,
            new OptionLegSnapshot(
                record.CallLastPrice,
                0,
                0,
                record.CallOpenInterest,
                record.CallOpenInterestChange,
                record.CallImpliedVolatility,
                record.CallDelta,
                0,
                0,
                0),
            new OptionLegSnapshot(
                record.PutLastPrice,
                0,
                0,
                record.PutOpenInterest,
                record.PutOpenInterestChange,
                record.PutImpliedVolatility,
                record.PutDelta,
                0,
                0,
                0),
            record.Support,
            record.Resistance);
    }

    private static IReadOnlyList<StrikeOiChangeSeries> BuildReplayStrikeHistory(IReadOnlyList<MarketSessionRecord> records)
    {
        return records
            .SelectMany(record => record.Strikes.Select(strike => strike.Strike))
            .Distinct()
            .Order()
            .Select(strike =>
            {
                var points = records
                    .Select(record => new
                    {
                        record.Timestamp,
                        Strike = record.Strikes.FirstOrDefault(item => item.Strike == strike)
                    })
                    .Where(item => item.Strike is not null)
                    .ToArray();

                return new StrikeOiChangeSeries(
                    strike,
                    points.Select(item => new ChartPoint(item.Timestamp, item.Strike!.CallOpenInterestChange)).ToArray(),
                    points.Select(item => new ChartPoint(item.Timestamp, item.Strike!.PutOpenInterestChange)).ToArray(),
                    points.Select(item => new ChartPoint(item.Timestamp, item.Strike!.PutOpenInterestChange - item.Strike.CallOpenInterestChange)).ToArray());
            })
            .ToArray();
    }

    private IReadOnlyList<ChartPoint> CurrentPricePoints()
    {
        return PriceChartSeries.FirstOrDefault()?.Points ?? Snapshot?.PriceSeries ?? [];
    }

    private IReadOnlyList<ChartPoint> CurrentOiPoints()
    {
        return OiChartSeries.FirstOrDefault()?.Points ?? Snapshot?.OiChangeSeries ?? [];
    }

    private static IReadOnlyList<ChartPoint> MergeChartPoints(
        IReadOnlyList<ChartPoint>? existing,
        IReadOnlyList<ChartPoint> incoming)
    {
        if (existing is null || existing.Count == 0)
        {
            return incoming;
        }

        return existing
            .Concat(incoming)
            .GroupBy(point => point.Time)
            .Select(group => group.Last())
            .OrderBy(point => point.Time)
            .TakeLast(600)
            .ToArray();
    }

    private void EvaluateAlerts(MarketSnapshot next)
    {
        if (!AlertsEnabled)
        {
            return;
        }

        var cooldown = TimeSpan.FromSeconds(AlertCooldownSeconds);
        var signal = BuildMarketSignal();
        if (lastSignalLabel is null)
        {
            lastSignalLabel = signal.Label;
        }
        else if (!string.Equals(lastSignalLabel, signal.Label, StringComparison.Ordinal))
        {
            AddAlert(
                $"signal:{lastSignalLabel}->{signal.Label}",
                "Bias changed",
                $"{lastSignalLabel} -> {signal.Label}: {signal.Detail}",
                signal.Foreground,
                next.Timestamp,
                cooldown);
            lastSignalLabel = signal.Label;
        }

        var strongestCall = next.Strikes
            .OrderByDescending(strike => Math.Abs(strike.Call.OpenInterestChange))
            .FirstOrDefault();
        if (strongestCall is not null && Math.Abs(strongestCall.Call.OpenInterestChange) >= OiBuildupThreshold)
        {
            AddAlert(
                $"call-buildup:{strongestCall.Strike}",
                "CE buildup",
                $"{FormatNumber(strongestCall.Strike)} CE OI change {strongestCall.Call.OpenInterestChange:N0}",
                Brushes.IndianRed,
                next.Timestamp,
                cooldown);
        }

        var strongestPut = next.Strikes
            .OrderByDescending(strike => Math.Abs(strike.Put.OpenInterestChange))
            .FirstOrDefault();
        if (strongestPut is not null && Math.Abs(strongestPut.Put.OpenInterestChange) >= OiBuildupThreshold)
        {
            AddAlert(
                $"put-buildup:{strongestPut.Strike}",
                "PE buildup",
                $"{FormatNumber(strongestPut.Strike)} PE OI change {strongestPut.Put.OpenInterestChange:N0}",
                Brushes.MediumSeaGreen,
                next.Timestamp,
                cooldown);
        }

        var focusStrike = SelectedStrike;
        if (focusStrike is not null)
        {
            var proximity = SupportResistanceProximityPoints;
            if (Math.Abs(next.Spot - focusStrike.Support) <= proximity)
            {
                AddAlert(
                    $"near-support:{focusStrike.Strike}",
                    "Near support",
                    $"Spot {FormatNumber(next.Spot)} near support {FormatNumber(focusStrike.Support)}",
                    Brushes.MediumSeaGreen,
                    next.Timestamp,
                    cooldown);
            }

            if (Math.Abs(next.Spot - focusStrike.Resistance) <= proximity)
            {
                AddAlert(
                    $"near-resistance:{focusStrike.Strike}",
                    "Near resistance",
                    $"Spot {FormatNumber(next.Spot)} near resistance {FormatNumber(focusStrike.Resistance)}",
                    Brushes.IndianRed,
                    next.Timestamp,
                    cooldown);
            }
        }
    }

    private void AddAlert(
        string key,
        string title,
        string detail,
        Brush foreground,
        DateTimeOffset timestamp,
        TimeSpan cooldown)
    {
        if (recentAlertKeys.TryGetValue(key, out var lastRaised) && timestamp - lastRaised < cooldown)
        {
            return;
        }

        recentAlertKeys[key] = timestamp;
        Alerts.Insert(0, new MarketAlertViewModel(
            timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
            title,
            detail,
            foreground));

        while (Alerts.Count > 25)
        {
            Alerts.RemoveAt(Alerts.Count - 1);
        }
    }

    private void UpdateSelectedStrikeOiChartSeries()
    {
        var history = SelectedStrike is null
            ? null
            : Snapshot?.StrikeOiChangeSeries.FirstOrDefault(item => item.Strike == SelectedStrike.Strike);

        SelectedStrikeOiChartSeries = history is null
            ? []
            :
            [
                new ChartSeriesViewModel("PE OI Chg", history.Put, Brushes.MediumSeaGreen),
                new ChartSeriesViewModel("CE OI Chg", history.Call, Brushes.IndianRed),
                new ChartSeriesViewModel("PE - CE", history.Difference, Brushes.CornflowerBlue)
            ];
    }

    private MarketSignalViewModel BuildMarketSignal()
    {
        if (Snapshot is null)
        {
            return new MarketSignalViewModel("Waiting", "Live snapshot not loaded", Brushes.LightSlateGray);
        }

        return BuildMarketSignal(Snapshot, SelectedStrike);
    }

    private static MarketSignalViewModel BuildMarketSignal(
        MarketSnapshot snapshot,
        OptionStrikeSnapshot? selectedStrike)
    {
        var score = 0;
        var reasons = new List<string>();

        if (snapshot.SpotChange > 0)
        {
            score++;
            reasons.Add($"spot up {snapshot.SpotChange:N2}");
        }
        else if (snapshot.SpotChange < 0)
        {
            score--;
            reasons.Add($"spot down {snapshot.SpotChange:N2}");
        }

        if (snapshot.Breadth.PutCallRatioOi >= 1.15m)
        {
            score++;
            reasons.Add($"PCR OI {snapshot.Breadth.PutCallRatioOi:N2}");
        }
        else if (snapshot.Breadth.PutCallRatioOi <= 0.85m)
        {
            score--;
            reasons.Add($"PCR OI {snapshot.Breadth.PutCallRatioOi:N2}");
        }

        var latestOiMomentum = snapshot.OiChangeSeries.LastOrDefault()?.Value ?? 0;
        if (latestOiMomentum > 0)
        {
            score++;
            reasons.Add("put buildup leads");
        }
        else if (latestOiMomentum < 0)
        {
            score--;
            reasons.Add("call buildup leads");
        }

        var focusStrike = selectedStrike ?? snapshot.Strikes
            .OrderBy(strike => Math.Abs(strike.Strike - snapshot.Spot))
            .FirstOrDefault();
        if (focusStrike is not null)
        {
            var pressure = focusStrike.Put.OpenInterestChange - focusStrike.Call.OpenInterestChange;
            if (pressure > 0)
            {
                score++;
                reasons.Add($"{FormatNumber(focusStrike.Strike)} PE pressure");
            }
            else if (pressure < 0)
            {
                score--;
                reasons.Add($"{FormatNumber(focusStrike.Strike)} CE pressure");
            }
        }

        var label = score >= 2
            ? "Bullish Bias"
            : score <= -2
                ? "Bearish Bias"
                : "Neutral / Mixed";
        var foreground = score >= 2
            ? Brushes.MediumSeaGreen
            : score <= -2
                ? Brushes.IndianRed
                : Brushes.Goldenrod;

        return new MarketSignalViewModel(
            label,
            reasons.Count == 0 ? "No clear pressure yet" : string.Join(" | ", reasons.Take(3)),
            foreground);
    }

    private static void UpdatePath(PointCollection target, IReadOnlyList<ChartPoint> points, double width, double height)
    {
        target.Clear();
        if (points.Count == 0)
        {
            return;
        }

        var values = points.TakeLast(80).Select(item => item.Value).ToList();
        var min = values.Min();
        var max = values.Max();
        var span = max - min;

        for (var i = 0; i < values.Count; i++)
        {
            var x = values.Count == 1 ? 0 : i * width / (values.Count - 1);
            var normalized = span == 0 ? 0.5m : (values[i] - min) / span;
            var y = height - (double)normalized * height;
            target.Add(new Point(x, y));
        }
    }

    private static string FormatNumber(decimal value)
    {
        return value.ToString("N2", CultureInfo.CurrentCulture);
    }

    private static string FormatSigned(decimal value)
    {
        return $"{(value >= 0 ? "+" : string.Empty)}{value:N2}";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class OptionChainRowViewModel
{
    private static readonly Brush Transparent = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    private static readonly Brush AtmBrush = new SolidColorBrush(Color.FromRgb(36, 45, 32));
    private static readonly Brush MutedTextBrush = new SolidColorBrush(Color.FromRgb(174, 190, 209));
    private static readonly Brush PositiveBrush = new SolidColorBrush(Color.FromRgb(72, 211, 137));
    private static readonly Brush NegativeBrush = new SolidColorBrush(Color.FromRgb(255, 104, 104));
    private static readonly Brush NeutralBrush = new SolidColorBrush(Color.FromRgb(216, 225, 236));
    private static readonly Brush MildCallHeatBrush = new SolidColorBrush(Color.FromRgb(54, 27, 29));
    private static readonly Brush StrongCallHeatBrush = new SolidColorBrush(Color.FromRgb(91, 35, 32));
    private static readonly Brush MildPutHeatBrush = new SolidColorBrush(Color.FromRgb(26, 56, 39));
    private static readonly Brush StrongPutHeatBrush = new SolidColorBrush(Color.FromRgb(31, 91, 58));

    public OptionChainRowViewModel(
        OptionStrikeSnapshot snapshot,
        decimal? atmStrike,
        long maxCallOi,
        long maxPutOi,
        long maxCallOiChange,
        long maxPutOiChange)
    {
        Snapshot = snapshot;
        Strike = snapshot.Strike;
        IsAtm = atmStrike == snapshot.Strike;
        RowBackground = IsAtm ? AtmBrush : Transparent;
        CallOiBackground = Heat(snapshot.Call.OpenInterest, maxCallOi, isPut: false);
        PutOiBackground = Heat(snapshot.Put.OpenInterest, maxPutOi, isPut: true);
        CallOiChangeBackground = Heat(Math.Abs(snapshot.Call.OpenInterestChange), maxCallOiChange, isPut: false);
        PutOiChangeBackground = Heat(Math.Abs(snapshot.Put.OpenInterestChange), maxPutOiChange, isPut: true);
        CallOiChangeForeground = ChangeBrush(snapshot.Call.OpenInterestChange);
        PutOiChangeForeground = ChangeBrush(snapshot.Put.OpenInterestChange);
        PressureLabel = BuildPressureLabel(snapshot);
        var netPressure = snapshot.Put.OpenInterestChange - snapshot.Call.OpenInterestChange;
        PressureForeground = netPressure switch
        {
            > 0 => PositiveBrush,
            < 0 => NegativeBrush,
            _ => MutedTextBrush
        };
    }

    public OptionStrikeSnapshot Snapshot { get; }

    public decimal Strike { get; }

    public bool IsAtm { get; }

    public Brush RowBackground { get; }

    public Brush CallOiBackground { get; }

    public Brush PutOiBackground { get; }

    public Brush CallOiChangeBackground { get; }

    public Brush PutOiChangeBackground { get; }

    public Brush CallOiChangeForeground { get; }

    public Brush PutOiChangeForeground { get; }

    public string AtmLabel => IsAtm ? "ATM" : string.Empty;

    public string PressureLabel { get; }

    public Brush PressureForeground { get; }

    private static Brush Heat(long value, long max, bool isPut)
    {
        if (max <= 0)
        {
            return Transparent;
        }

        var ratio = (decimal)value / max;
        if (ratio >= 0.78m)
        {
            return isPut ? StrongPutHeatBrush : StrongCallHeatBrush;
        }

        if (ratio >= 0.52m)
        {
            return isPut ? MildPutHeatBrush : MildCallHeatBrush;
        }

        return Transparent;
    }

    private static Brush ChangeBrush(long value)
    {
        return value switch
        {
            > 0 => PositiveBrush,
            < 0 => NegativeBrush,
            _ => NeutralBrush
        };
    }

    private static string BuildPressureLabel(OptionStrikeSnapshot snapshot)
    {
        var net = snapshot.Put.OpenInterestChange - snapshot.Call.OpenInterestChange;
        if (net > 0)
        {
            return "PE pressure";
        }

        if (net < 0)
        {
            return "CE pressure";
        }

        return "Balanced";
    }
}

public sealed record MovementTimelineEntryViewModel(
    string TimeText,
    string Title,
    string Detail,
    string WatchText,
    string SpotText,
    Brush Foreground)
{
    public static MovementTimelineEntryViewModel FromRecord(MovementTimelineRecord record)
    {
        return new MovementTimelineEntryViewModel(
            record.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
            record.Title,
            record.Detail,
            record.WatchText,
            $"{record.Spot:N2} ({(record.SpotChange >= 0 ? "+" : string.Empty)}{record.SpotChange:N2})",
            GetForeground(record.Title));
    }

    private static Brush GetForeground(string title)
    {
        if (title.Contains("Bullish", StringComparison.OrdinalIgnoreCase))
        {
            return Brushes.MediumSeaGreen;
        }

        if (title.Contains("Bearish", StringComparison.OrdinalIgnoreCase))
        {
            return Brushes.IndianRed;
        }

        return Brushes.Goldenrod;
    }
}

public sealed record SharpMovementViewModel(
    string Title,
    string Detail,
    Brush Foreground);

public enum CatalogFilter
{
    All,
    Favorites,
    Index,
    Stock,
    Commodity
}

public sealed class CatalogInstrumentViewModel : INotifyPropertyChanged
{
    private bool isFavorite;

    public CatalogInstrumentViewModel(InstrumentSummary source, bool isFavorite)
    {
        Source = source;
        this.isFavorite = isFavorite;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public InstrumentSummary Source { get; }

    public string Symbol => Source.Symbol;

    public string DisplayName => Source.DisplayName;

    public string SegmentLabel => Source.Segment.ToString();

    public bool IsFavorite
    {
        get => isFavorite;
        set
        {
            if (isFavorite == value)
            {
                return;
            }

            isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteGlyph));
        }
    }

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record MarketSignalViewModel(string Label, string Detail, Brush Foreground);

public sealed record MarketAlertViewModel(
    string TimeText,
    string Title,
    string Detail,
    Brush Foreground);

public sealed class RelayCommand<T>(Action<T?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return true;
    }

    public void Execute(object? parameter)
    {
        execute(parameter is T value ? value : default);
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
