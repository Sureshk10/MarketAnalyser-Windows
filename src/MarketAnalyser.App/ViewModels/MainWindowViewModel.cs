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
using MarketAnalyser.App;
using MarketAnalyser.App.Session;
using MarketAnalyser.Core.Configuration;
using MarketAnalyser.Core.Market;
using MarketAnalyser.Core.Orders;

namespace MarketAnalyser.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private const decimal UnderlyingBullishRatio = 1.20m;
    private const decimal UnderlyingBearishRatio = 0.83m;
    private const decimal UnusualUnderlyingBullishRatio = 1.50m;
    private const decimal UnusualUnderlyingBearishRatio = 0.67m;
    private const long UnusualStrikeDepthThreshold = 25_000;

    private readonly IMarketDataSource marketDataSource;
    private readonly AppPreferencesStore preferencesStore;
    private readonly MarketSessionStore sessionStore;
    private readonly MovementTimelineStore movementTimelineStore;
    private readonly IHistoricalMarketDataSource historicalDataSource;
    private readonly IOrderBroker orderBroker;
    private readonly OrderOptions orderOptions;
    private readonly DispatcherTimer refreshTimer;
    private readonly DispatcherTimer replayTimer;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly SemaphoreSlim liveScanGate = new(1, 1);
    private readonly CancellationTokenSource liveScanCts = new();
    private CancellationTokenSource selectionCts = new();
    private Task? liveScanTask;
    private Task? historicalWarmupTask;
    private readonly SemaphoreSlim historicalWarmupGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> recentAlertKeys = [];
    private static readonly TimeSpan SignalRearmCooldown = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LiveScanInterval = TimeSpan.FromSeconds(60);
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
    private string movementReadingStats = string.Empty;
    private string movementReadingWatchText = "--";
    private Brush movementReadingForeground = Brushes.LightSlateGray;
    private string? lastMovementTimelineTitle;
    private string sessionReviewSummaryText = "Waiting for session data";
    private string sessionReviewPhasesText = "Timeline will appear as readings change";
    private string sessionReviewOiText = "--";
    private string sessionReviewExportText = string.Empty;
    private string liveScanStatusText = "Live scan not started";
    private string orderServiceStatusText = "Order service not enabled";
    private string replayOutcomeText = "Load replay to evaluate follow-through";
    private Brush replayOutcomeForeground = Brushes.LightSlateGray;
    private DateTime? replaySelectedDate = DateTime.Today;
    private int replaySelectedHour = 9;
    private int replaySelectedMinute = 15;
    private IReadOnlyList<MarketSessionRecord> replayRecords = [];
    private int replayIndex = -1;
    private bool isReplayMode;
    private bool isReplayPlaying;
    private string replayStatusText = "Replay not loaded";
    private bool isSessionReviewVisible = true;
    private bool isLeftPanelVisible = true;
    private bool isRightPanelVisible = true;
    private bool isAlertsPanelVisible = true;
    private int sessionRecordCountToday;
    private MarketSignalViewModel currentSignal = new("Waiting", "Live snapshot not loaded", Brushes.LightSlateGray);
    private string? activeSignalPlanDetail;
    private string? signalClosedOutcomeText;
    private string? signalClosedLabel;
    private DateTimeOffset? signalClosedAt;
    private bool signalPlanOpen;

    public MainWindowViewModel(
        IMarketDataSource marketDataSource,
        AppPreferencesStore preferencesStore,
        MarketSessionStore sessionStore,
        MovementTimelineStore movementTimelineStore,
        IHistoricalMarketDataSource historicalDataSource,
        IOrderBroker orderBroker,
        OrderOptions orderOptions)
    {
        this.marketDataSource = marketDataSource;
        this.preferencesStore = preferencesStore;
        this.sessionStore = sessionStore;
        this.movementTimelineStore = movementTimelineStore;
        this.historicalDataSource = historicalDataSource;
        this.orderBroker = orderBroker;
        this.orderOptions = orderOptions;
        DataSourceName = marketDataSource.Name;
        ToggleFavoriteCommand = new RelayCommand<CatalogInstrumentViewModel>(ToggleFavorite);
        ClearAlertsCommand = new RelayCommand<object>(_ => Alerts.Clear());
        ExportSessionReviewCommand = new RelayCommand<object>(_ => _ = ExportSessionReviewAsync());
        LoadReplayCommand = new RelayCommand<object>(_ => _ = LoadReplayAsync());
        ReplaySummaryCommand = new RelayCommand<object>(_ => _ = ShowReplaySummaryAsync());
        ReplayStepCommand = new RelayCommand<object>(_ => ReplayStep());
        ReplayPlayPauseCommand = new RelayCommand<object>(_ => ToggleReplayPlayback());
        ResumeLiveCommand = new RelayCommand<object>(_ => ResumeLive());
        OpenTradingViewChartCommand = new RelayCommand<object>(_ => OpenTradingViewChart());
        PlaceCeBuyCommand = new RelayCommand<object>(_ => _ = PlaceOrderAsync(OrderSide.Buy, "CE"));
        PlaceCeSellCommand = new RelayCommand<object>(_ => _ = PlaceOrderAsync(OrderSide.Sell, "CE"));
        PlacePeBuyCommand = new RelayCommand<object>(_ => _ = PlaceOrderAsync(OrderSide.Buy, "PE"));
        PlacePeSellCommand = new RelayCommand<object>(_ => _ = PlaceOrderAsync(OrderSide.Sell, "PE"));
        OpenOrdersPopupCommand = new RelayCommand<object>(_ => OpenOrdersPopup("Open"));
        OpenClosedOrdersPopupCommand = new RelayCommand<object>(_ => OpenOrdersPopup("Closed"));
        OpenStrikeOrderScreenCommand = new RelayCommand<object>(p => OpenStrikeOrderScreen(p));
        ShowStrikeDetailsCommand = new RelayCommand<object>(p => ShowStrikeDetails(p as OptionStrikeSnapshot));
        ToggleSessionReviewPanelCommand = new RelayCommand<object>(_ => IsSessionReviewVisible = !IsSessionReviewVisible);
        ToggleLeftPanelCommand = new RelayCommand<object>(_ => IsLeftPanelVisible = !IsLeftPanelVisible);
        ToggleRightPanelCommand = new RelayCommand<object>(_ => IsRightPanelVisible = !IsRightPanelVisible);
        ToggleAlertsPanelCommand = new RelayCommand<object>(_ => IsAlertsPanelVisible = !IsAlertsPanelVisible);
        OrderServiceStatusText = orderOptions.Enabled
            ? $"Orders enabled via {orderBroker.Kind}"
            : $"Orders scaffold ready via {orderBroker.Kind}";
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

    public ObservableCollection<LiveScanHitViewModel> LiveScanHits { get; } = [];

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

    public ICommand ReplaySummaryCommand { get; }

    public ICommand ReplayStepCommand { get; }

    public ICommand ReplayPlayPauseCommand { get; }

    public ICommand ResumeLiveCommand { get; }

    public ICommand OpenTradingViewChartCommand { get; }

    public ICommand ToggleSessionReviewPanelCommand { get; }

    public ICommand ToggleLeftPanelCommand { get; }

    public ICommand ToggleRightPanelCommand { get; }

    public ICommand ToggleAlertsPanelCommand { get; }

    public ICommand PlaceCeBuyCommand { get; }

    public ICommand PlaceCeSellCommand { get; }

    public ICommand PlacePeBuyCommand { get; }

    public ICommand PlacePeSellCommand { get; }

    public ICommand OpenOrdersPopupCommand { get; }

    public ICommand OpenClosedOrdersPopupCommand { get; }

    public ICommand OpenStrikeOrderScreenCommand { get; }

    public ICommand ShowStrikeDetailsCommand { get; }

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

    public bool IsLeftPanelVisible
    {
        get => isLeftPanelVisible;
        set
        {
            if (SetField(ref isLeftPanelVisible, value))
            {
                OnPropertyChanged(nameof(LeftPanelColumnWidth));
                OnPropertyChanged(nameof(LeftPanelVisibility));
                OnPropertyChanged(nameof(LeftPanelToggleText));
            }
        }
    }

    public bool IsRightPanelVisible
    {
        get => isRightPanelVisible;
        set
        {
            if (SetField(ref isRightPanelVisible, value))
            {
                OnPropertyChanged(nameof(RightPanelColumnWidth));
                OnPropertyChanged(nameof(RightPanelVisibility));
                OnPropertyChanged(nameof(RightPanelToggleText));
            }
        }
    }

    public bool IsAlertsPanelVisible
    {
        get => isAlertsPanelVisible;
        set
        {
            if (SetField(ref isAlertsPanelVisible, value))
            {
                OnPropertyChanged(nameof(AlertsRowHeight));
                OnPropertyChanged(nameof(AlertsPanelVisibility));
                OnPropertyChanged(nameof(AlertsPanelToggleText));
            }
        }
    }

    public bool IsSessionReviewVisible
    {
        get => isSessionReviewVisible;
        set
        {
            if (SetField(ref isSessionReviewVisible, value))
            {
                OnPropertyChanged(nameof(SessionReviewRowHeight));
                OnPropertyChanged(nameof(LiveScanRowHeight));
                OnPropertyChanged(nameof(SessionReviewVisibility));
                OnPropertyChanged(nameof(SessionReviewPanelToggleText));
            }
        }
    }

    public GridLength LeftPanelColumnWidth => IsLeftPanelVisible ? new GridLength(240) : new GridLength(0);

    public GridLength RightPanelColumnWidth => IsRightPanelVisible ? new GridLength(380) : new GridLength(0);

    public GridLength AlertsRowHeight => IsAlertsPanelVisible ? new GridLength(220) : new GridLength(0);

    public GridLength SessionReviewRowHeight => IsSessionReviewVisible ? new GridLength(170) : new GridLength(0);

    public GridLength LiveScanRowHeight => IsSessionReviewVisible ? new GridLength(162) : new GridLength(332);

    public Visibility LeftPanelVisibility => IsLeftPanelVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RightPanelVisibility => IsRightPanelVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AlertsPanelVisibility => IsAlertsPanelVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SessionReviewVisibility => IsSessionReviewVisible ? Visibility.Visible : Visibility.Collapsed;

    public string LeftPanelToggleText => IsLeftPanelVisible ? "Hide Left" : "Show Left";

    public string RightPanelToggleText => IsRightPanelVisible ? "Hide Right" : "Show Right";

    public string AlertsPanelToggleText => IsAlertsPanelVisible ? "Hide Alerts" : "Show Alerts";

    public string SessionReviewPanelToggleText => IsSessionReviewVisible ? "Hide Review" : "Show Review";

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
            var previousSymbol = selectedInstrument?.Symbol;
            if (SetField(ref selectedInstrument, value) && value is not null)
            {
                var previousSelectionCts = Interlocked.Exchange(ref selectionCts, new CancellationTokenSource());
                previousSelectionCts.Cancel();
                previousSelectionCts.Dispose();
                var selectionToken = selectionCts.Token;

                InvalidateSymbolCache(previousSymbol);
                InvalidateSymbolCache(value.Symbol);

                preferences.LastSelectedSymbol = value.Symbol;
                preferencesStore.Save(preferences);
                lastRecordedSnapshotTime = null;
                sessionRecordCountToday = 0;
                hasSessionBackfill = false;
                lastMovementTimelineTitle = null;
                StopReplay(clearRecords: true);
                ResetSignalState();
                Snapshot = null;
                Strikes.Clear();
                StrikeRows.Clear();
                PriceChartSeries = [];
                OiChartSeries = [];
                SelectedStrikeOiChartSeries = [];
                SelectedStrike = null;
                OnPropertyChanged(nameof(SelectedStrikeRow));
                MovementTimeline.Clear();
                UpdateSessionReview([], []);
                _ = InitializeSelectedInstrumentAsync(value.Symbol, selectionToken);
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
                OnPropertyChanged(nameof(VolumeText));
                OnPropertyChanged(nameof(RsiText));
                OnPropertyChanged(nameof(VwapText));
                OnPropertyChanged(nameof(VolumeRatioText));
                OnPropertyChanged(nameof(LastUpdatedText));
                OnPropertyChanged(nameof(MarketDepthInfluenceText));
                OnPropertyChanged(nameof(MarketDepthInfluenceForeground));
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

    public string MovementReadingStats
    {
        get => movementReadingStats;
        private set => SetField(ref movementReadingStats, value);
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

    public string LiveScanStatusText
    {
        get => liveScanStatusText;
        private set => SetField(ref liveScanStatusText, value);
    }

    public string OrderServiceStatusText
    {
        get => orderServiceStatusText;
        private set => SetField(ref orderServiceStatusText, value);
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

    public IReadOnlyList<int> ReplayHourOptions { get; } = Enumerable.Range(0, 24).ToArray();

    public IReadOnlyList<int> ReplayMinuteOptions { get; } = Enumerable.Range(0, 60).ToArray();

    public int ReplaySelectedHour
    {
        get => replaySelectedHour;
        set => SetField(ref replaySelectedHour, Math.Clamp(value, 0, 23));
    }

    public int ReplaySelectedMinute
    {
        get => replaySelectedMinute;
        set => SetField(ref replaySelectedMinute, Math.Clamp(value, 0, 59));
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

    public string VolumeText => Snapshot is null
        ? "--"
        : FormatNumber(Snapshot.VolumeSeries.LastOrDefault()?.Value ?? 0);

    public string RsiText => Snapshot is null
        ? "--"
        : CalculateRsi(Snapshot.PriceSeries, 14)?.ToString("N1", CultureInfo.CurrentCulture) ?? "--";

    public string VwapText => Snapshot is null
        ? "--"
        : CalculateVwap(Snapshot.PriceSeries, Snapshot.VolumeSeries)?.ToString("N2", CultureInfo.CurrentCulture) ?? "--";

    public string VolumeRatioText => Snapshot is null
        ? "--"
        : CalculateVolumeRatio(Snapshot.VolumeSeries)?.ToString("N2", CultureInfo.CurrentCulture) ?? "--";

    public string LastUpdatedText => Snapshot is null ? "--" : Snapshot.Timestamp.ToLocalTime().ToString("HH:mm:ss");

    public string MarketSignalText => currentSignal.Label;

    public string MarketSignalDetail => currentSignal.Detail;

    public string MarketDepthInfluenceText => Snapshot is null ? "--" : DescribeDepthPressure(Snapshot);

    public Brush MarketDepthInfluenceForeground => Snapshot is null
        ? Brushes.LightSlateGray
        : ResolveDepthPressureForeground(Snapshot);

    public Brush MarketSignalForeground => currentSignal.Foreground;

    public string SelectedStrikeLabel => SelectedStrike is null ? "Select a strike" : FormatNumber(SelectedStrike.Strike);

    public string SelectedStrikeDetail => SelectedStrike is null
        ? "Support, resistance and Greeks will appear here."
        : BuildStrikeDetailText(SelectedStrike);

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
        StartLiveScanLoop();
    }

    public void StopBackgroundWork()
    {
        if (!liveScanCts.IsCancellationRequested)
        {
            liveScanCts.Cancel();
        }

        if (!selectionCts.IsCancellationRequested)
        {
            selectionCts.Cancel();
        }
    }

    public async Task RefreshAsync()
    {
        await RefreshAsync(selectionCts.Token);
    }

    private void InvalidateSymbolCache(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || marketDataSource is not EmbeddedMarketDataSource embeddedMarketDataSource)
        {
            return;
        }

        embeddedMarketDataSource.InvalidateSymbol(symbol);
    }

    private async Task InitializeSelectedInstrumentAsync(string symbol, CancellationToken selectionToken)
    {
        await RefreshAsync(selectionToken, force: true);

        if (selectionToken.IsCancellationRequested ||
            SelectedInstrument is null ||
            !string.Equals(SelectedInstrument.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await LoadSessionBackfillAsync(symbol, selectionToken);

        if (selectionToken.IsCancellationRequested ||
            SelectedInstrument is null ||
            !string.Equals(SelectedInstrument.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await LoadMovementTimelineAsync(symbol, selectionToken);
    }

    private async Task RefreshAsync(CancellationToken selectionToken, bool force = false)
    {
        if (isReplayMode || SelectedInstrument is null)
        {
            return;
        }

        if (!force && !IsLiveRefreshAllowed(SelectedInstrument))
        {
            Status = $"Live refresh paused after market hours for {SelectedInstrument.Symbol}";
            return;
        }

        var entered = false;
        try
        {
            if (force)
            {
                await refreshGate.WaitAsync(selectionToken);
                entered = true;
            }
            else
            {
                entered = await refreshGate.WaitAsync(0);
            }

            if (!entered)
            {
                return;
            }

            IsBusy = true;
            Status = "Refreshing";
            var requestedSymbol = SelectedInstrument.Symbol;
            var next = await marketDataSource.GetSnapshotAsync(requestedSymbol, selectionToken);

            if (selectionToken.IsCancellationRequested ||
                SelectedInstrument is null ||
                !string.Equals(SelectedInstrument.Symbol, requestedSymbol, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ApplySnapshot(next, selectionToken: selectionToken);
            await RecordSnapshotAsync(next, selectionToken);
            Status = $"Live via {DataSourceName}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
            if (entered)
            {
                refreshGate.Release();
            }
        }
    }

    public void SelectInstrument(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var instrument = Instruments.FirstOrDefault(item =>
            string.Equals(item.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (instrument is null)
        {
            Status = $"Symbol {symbol} not found";
            return;
        }

        if (ReferenceEquals(SelectedInstrument, instrument))
        {
            _ = RefreshAsync(selectionCts.Token, force: true);
            return;
        }

        SelectedInstrument = instrument;
    }

    public void ShowStrikeDetails(OptionStrikeSnapshot? strike)
    {
        if (strike is null)
        {
            Status = "Select a strike to view details";
            return;
        }

        var window = new StrikeDetailWindow(
            SelectedInstrument?.Symbol ?? "Market",
            FormatNumber(strike.Strike),
            BuildStrikeDetailText(strike))
        {
            Owner = Application.Current?.MainWindow
        };

        window.ShowDialog();
    }

    private async Task PlaceOrderAsync(OrderSide side, string optionSide)
    {
        await OpenStrikeOrderPopupAsync(side, optionSide);
    }

    private void OpenStrikeOrderScreen(object? parameter)
    {
        if (parameter is string action)
        {
            var mode = action.Contains("Buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell;
            var optionSide = action.StartsWith("CE", StringComparison.OrdinalIgnoreCase) ? "CE" : "PE";
            _ = OpenStrikeOrderPopupAsync(mode, optionSide);
            return;
        }

        OpenOrdersPopup("Open");
    }

    private async Task OpenStrikeOrderPopupAsync(OrderSide side, string optionSide)
    {
        if (SelectedInstrument is null || SelectedStrike is null)
        {
            Status = "Select a symbol and strike before placing an order";
            return;
        }

        var window = new OrderPopupWindow(
            SelectedInstrument.Symbol,
            FormatNumber(SelectedStrike.Strike),
            $"{optionSide} {side}",
            BuildStrikeDetailText(SelectedStrike),
            optionSide.Equals("CE", StringComparison.OrdinalIgnoreCase) ? SelectedStrike.Call.LastPrice : SelectedStrike.Put.LastPrice,
            SelectedInstrument.LotSize,
            orderBroker,
            orderOptions.Enabled)
        {
            Owner = Application.Current?.MainWindow
        };

        await window.LoadOrdersAsync();
        window.ShowDialog();
        Status = $"{optionSide} {side} popup opened";
    }

    private void OpenOrdersPopup(string mode)
    {
        var window = new OrderPopupWindow(
            SelectedInstrument?.Symbol ?? "Market",
            SelectedStrike is null ? "--" : FormatNumber(SelectedStrike.Strike),
            mode,
            SelectedStrike is null ? "Select a strike to place an order." : BuildStrikeDetailText(SelectedStrike),
            SelectedStrike is null ? 0 : SelectedStrike.Call.LastPrice,
            SelectedInstrument?.LotSize ?? 1,
            orderBroker,
            orderOptions.Enabled)
        {
            Owner = Application.Current?.MainWindow
        };

        _ = window.LoadOrdersAsync();
        window.ShowDialog();
        Status = $"{mode} orders popup opened";
    }

    private void StartLiveScanLoop()
    {
        if (liveScanTask is not null)
        {
            return;
        }

        LiveScanStatusText = "Scanning favorites in background";
        liveScanTask = Task.Run(() => RunLiveScanLoopAsync(liveScanCts.Token));
    }

    private async Task RunLiveScanLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ScanLiveFavoritesAsync(cancellationToken);

            using var timer = new PeriodicTimer(LiveScanInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await ScanLiveFavoritesAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            if (Application.Current?.Dispatcher is Dispatcher dispatcher)
            {
                await dispatcher.InvokeAsync(() => LiveScanStatusText = "Live scan failed");
            }
        }
    }

    private async Task ScanLiveFavoritesAsync(CancellationToken cancellationToken)
    {
        if (isReplayMode)
        {
            await UpdateLiveScanResultsAsync([], "Live scan paused in replay mode", cancellationToken);
            return;
        }

        if (!await liveScanGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var favoriteSymbols = await GetFavoriteSymbolsAsync();
            if (favoriteSymbols.Length == 0)
            {
                await UpdateLiveScanResultsAsync([], "No favorites selected for live scan", cancellationToken);
                return;
            }

            var hits = new List<LiveScanHitViewModel>();
            foreach (var symbol in favoriteSymbols)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var snapshot = await marketDataSource.GetSnapshotAsync(symbol, cancellationToken);
                    var signal = BuildMarketSignal(snapshot);
                    if (signal.Label is "BUY" or "SELL")
                    {
                        hits.Add(new LiveScanHitViewModel(symbol, signal.Label, signal.Foreground));
                    }
                }
                catch (Exception ex)
                {
                    AppExceptionLogger.Log(ex);
                }
            }

            var orderedHits = hits
                .OrderBy(hit => hit.SignalLabel == "BUY" ? 0 : 1)
                .ThenBy(hit => hit.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var statusText = orderedHits.Length == 0
                ? $"Watching {favoriteSymbols.Length:N0} favorites - no active buy/sell hits"
                : $"Watching {favoriteSymbols.Length:N0} favorites - {orderedHits.Length:N0} active hits";

            await UpdateLiveScanResultsAsync(orderedHits, statusText, cancellationToken);
        }
        finally
        {
            liveScanGate.Release();
        }
    }

    private async Task<string[]> GetFavoriteSymbolsAsync()
    {
        if (Application.Current?.Dispatcher is not Dispatcher dispatcher)
        {
            return [];
        }

        return await dispatcher.InvokeAsync(() =>
            Instruments
                .Where(item => item.IsFavorite)
                .Select(item => item.Symbol)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private async Task UpdateLiveScanResultsAsync(
        IReadOnlyList<LiveScanHitViewModel> hits,
        string statusText,
        CancellationToken cancellationToken)
    {
        if (Application.Current?.Dispatcher is not Dispatcher dispatcher)
        {
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            LiveScanHits.Clear();
            foreach (var hit in hits)
            {
                LiveScanHits.Add(hit);
            }

            LiveScanStatusText = $"{statusText} • {DateTime.Now:HH:mm:ss}";
        }, DispatcherPriority.Background, cancellationToken);
    }

    private static string BuildStrikeDetailText(OptionStrikeSnapshot strike)
    {
        return $"Support {FormatNumber(strike.Support)} | Resistance {FormatNumber(strike.Resistance)} | Delta CE/PE {strike.Call.Delta:N4}/{strike.Put.Delta:N4} | IV CE/PE {strike.Call.ImpliedVolatility:N2}/{strike.Put.ImpliedVolatility:N2} | Depth CE B {CompactNumberFormatter.FormatCount(strike.Call.TopBidQuantity)}@{strike.Call.TopBidPrice:N2} / A {CompactNumberFormatter.FormatCount(strike.Call.TopAskQuantity)}@{strike.Call.TopAskPrice:N2} | PE B {CompactNumberFormatter.FormatCount(strike.Put.TopBidQuantity)}@{strike.Put.TopBidPrice:N2} / A {CompactNumberFormatter.FormatCount(strike.Put.TopAskQuantity)}@{strike.Put.TopAskPrice:N2}";
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

    private async Task LoadSessionBackfillAsync(string symbol, CancellationToken selectionToken)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var backfill = await sessionStore.LoadBackfillAsync(symbol, today, selectionToken);
            if (selectionToken.IsCancellationRequested || SelectedInstrument?.Symbol != symbol)
            {
                return;
            }

            hasSessionBackfill = backfill.RecordCount > 0;
            if (backfill.MissingRanges.Count > 0)
            {
                var fetched = await RequestHistoricalBackfillAsync(symbol, backfill.MissingRanges, selectionToken);
                if (fetched > 0)
                {
                    backfill = await sessionStore.LoadBackfillAsync(symbol, today, selectionToken);
                    hasSessionBackfill = backfill.RecordCount > 0;
                }
            }

            if (hasSessionBackfill)
            {
                var existingPricePoints = PriceChartSeries.FirstOrDefault()?.Points;
                var existingOiPoints = OiChartSeries.FirstOrDefault()?.Points;
                var pricePoints = MergeChartPoints(existingPricePoints, backfill.PriceSeries);
                var oiPoints = MergeChartPoints(existingOiPoints, backfill.OiChangeSeries);

                PriceChartSeries =
                [
                    new ChartSeriesViewModel("Spot", pricePoints, Brushes.MediumSeaGreen)
                ];
                OiChartSeries =
                [
                    new ChartSeriesViewModel("PE - CE OI Chg", oiPoints, Brushes.CornflowerBlue)
                ];
                lastRecordedSnapshotTime = backfill.LastTimestamp;
            }

            sessionRecordCountToday = backfill.RecordCount;
            SessionStatus = BuildSessionStatus(symbol, backfill);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            SessionStatus = "Session backfill unavailable";
        }
    }

    private async Task<int> RequestHistoricalBackfillAsync(
        string symbol,
        IReadOnlyList<MarketSessionMissingRange> missingRanges,
        CancellationToken selectionToken)
    {
        var fetched = 0;
        foreach (var range in missingRanges)
        {
            var snapshots = await historicalDataSource.GetSnapshotsAsync(
                symbol,
                range.From,
                range.To,
                selectionToken);

            if (snapshots.Count == 0)
            {
                continue;
            }

            foreach (var snapshot in snapshots.OrderBy(item => item.Timestamp))
            {
                if (selectionToken.IsCancellationRequested)
                {
                    return fetched;
                }

                await sessionStore.AppendAsync(
                    snapshot,
                    SelectedStrike,
                    BuildMarketSignal(snapshot),
                    selectionToken);
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

    private async Task RecordSnapshotAsync(MarketSnapshot next, CancellationToken selectionToken)
    {
        if (lastRecordedSnapshotTime == next.Timestamp)
        {
            return;
        }

        try
        {
            if (selectionToken.IsCancellationRequested)
            {
                return;
            }

            await sessionStore.AppendAsync(next, SelectedStrike, BuildMarketSignal(), selectionToken);
            lastRecordedSnapshotTime = next.Timestamp;
            sessionRecordCountToday++;
            SessionStatus = $"Recording {next.Symbol}: {sessionRecordCountToday:N0} local records today";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            SessionStatus = "Session recording issue";
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

    private void ApplySnapshot(MarketSnapshot next, bool recordTimeline = true, CancellationToken selectionToken = default)
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
        foreach (var strike in next.Strikes.OrderByDescending(strike => strike.Strike))
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
            RecordMovementTimeline(next, selectionToken);
        }
        UpdateSessionReview(pricePoints, oiPoints);
        UpdateSelectedStrikeOiChartSeries();
        UpdateSignalState(next);
        EvaluateAlerts(next);
    }

    private async Task LoadMovementTimelineAsync(string symbol, CancellationToken selectionToken)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var records = await movementTimelineStore.LoadAsync(symbol, today, selectionToken);
            if (selectionToken.IsCancellationRequested || SelectedInstrument?.Symbol != symbol)
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
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
        }
    }

    private void RecordMovementTimeline(MarketSnapshot next, CancellationToken selectionToken)
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

        _ = PersistMovementTimelineAsync(record, selectionToken);
        UpdateSessionReview(CurrentPricePoints(), CurrentOiPoints());
    }

    private async Task PersistMovementTimelineAsync(MovementTimelineRecord record, CancellationToken selectionToken)
    {
        try
        {
            await movementTimelineStore.AppendAsync(record, selectionToken);
        }
        catch (OperationCanceledException)
        {
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
            MovementReadingStats = "RSI -- | VWAP -- | Vol -- | Vol x avg --";
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

        var depthPressure = AggregateDepthPressure(Snapshot);
        if (depthPressure > 0)
        {
            score++;
            reasons.Add(DescribeDepthPressure(Snapshot));
        }
        else if (depthPressure < 0)
        {
            score--;
            reasons.Add(DescribeDepthPressure(Snapshot));
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
            MovementReadingStats = BuildMovementStatsText();
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
        MovementReadingStats = BuildMovementStatsText();
        MovementReadingWatchText = BuildMovementWatchText();
    }

    private string BuildMovementStatsText()
    {
        if (Snapshot is null)
        {
            return "RSI -- | VWAP -- | Vol -- | Vol x avg --";
        }

        return $"RSI {RsiText} | VWAP {VwapText} | Vol {VolumeText} | Vol x avg {VolumeRatioText}";
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
        var depthPressure = AggregateDepthPressure(snapshot);
        var bullishOptionConfirm = oiShift > 0 || snapshot.Breadth.PutCallRatioOi >= 1.05m || depthPressure > 0;
        var bearishOptionConfirm = oiShift < 0 || snapshot.Breadth.PutCallRatioOi <= 0.95m || depthPressure < 0;
        var strongUp = move20 >= 150m || move10 >= 75m;
        var strongDown = move20 <= -150m || move10 <= -75m;
        var depthText = DescribeDepthPressure(snapshot);

        if (strongUp && breaksHigh && bullishOptionConfirm)
        {
            return new SharpMovementViewModel(
                move20 >= 150m ? "Sharp Bullish Expansion" : "Fast Bullish Break",
                BuildSharpMovementDetail(move10, move20, "new session high", oiShift, depthText, snapshot.Breadth.PutCallRatioOi),
                Brushes.MediumSeaGreen);
        }

        if (strongDown && breaksLow && bearishOptionConfirm)
        {
            return new SharpMovementViewModel(
                move20 <= -150m ? "Sharp Bearish Expansion" : "Fast Bearish Break",
                BuildSharpMovementDetail(move10, move20, "new session low", oiShift, depthText, snapshot.Breadth.PutCallRatioOi),
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
        string depthText,
        decimal pcrOi)
    {
        var pressure = oiShift > 0
            ? "PE OI shift positive"
            : oiShift < 0
                ? "CE OI shift stronger"
                : "OI balanced";

        return $"10m {FormatSigned(move10)} | 20m {FormatSigned(move20)} | {rangeBreak} | {pressure} | {depthText} | PCR OI {pcrOi:N2}";
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
        builder.AppendLine("Signal");
        builder.AppendLine(MarketSignalText);
        builder.AppendLine(MarketSignalDetail);
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
            var startFrom = BuildReplayStartTimestamp(date);
            var records = await sessionStore.LoadRecordsAsync(
                SelectedInstrument.Symbol,
                date,
                startFrom,
                CancellationToken.None);
            if (records.Count == 0)
            {
                StopReplay(clearRecords: true);
                ResetSignalState();
                ReplayStatusText = $"No replay records for {date:yyyy-MM-dd} from {startFrom.ToLocalTime():HH:mm}";
                ReplayOutcomeText = "No replay records to evaluate";
                ReplayOutcomeForeground = Brushes.LightSlateGray;
                return;
            }

            ResetSignalState();
            replayRecords = records;
            replayIndex = -1;
            isReplayMode = true;
            refreshTimer.Stop();
            OnPropertyChanged(nameof(LiveModeText));
            ReplayStatusText = $"Loaded {records.Count:N0} records for {date:yyyy-MM-dd} from {startFrom.ToLocalTime():HH:mm}";
            ReplayStep();
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            ReplayStatusText = "Replay load failed";
        }
    }

    private async Task ShowReplaySummaryAsync()
    {
        try
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Summary click started");
            ReplayStatusText = "Summary clicked";

            if (SelectedInstrument is null)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Summary blocked: no selected instrument");
                ReplayStatusText = "Select a symbol first";
                MessageBox.Show(
                    "Please select a symbol before opening Summary.",
                    "Market Analyser",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (ReplaySelectedDate is null)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Summary blocked: no replay date");
                ReplayStatusText = "Select a replay date first";
                MessageBox.Show(
                    "Please select a replay date before opening Summary.",
                    "Market Analyser",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var selection = await LoadOrDownloadSelectedReplaySelectionAsync();
            if (selection is null)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Summary blocked: selection lookup returned null");
                ReplayStatusText = "Select symbol and replay date first";
                MessageBox.Show(
                    "Please select a symbol and replay date before opening Summary.",
                    "Market Analyser",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Summary loading report for {selection.Date:yyyy-MM-dd} symbol={SelectedInstrument.Symbol}");
            var report = BuildReplayReport(selection);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Summary report rows full={report.FullRows.Count} summary={report.SummaryRows.Count}");
            if (report.FullRows.Count == 0 && report.SummaryRows.Count == 0)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Summary blocked: no rows found");
                ReplayStatusText = $"No summary data for {selection.Date:yyyy-MM-dd}";
                MessageBox.Show(
                    $"No summary data was available for {selection.Date:yyyy-MM-dd}.",
                    "Market Analyser",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var window = new ReplaySummaryWindow(
                SelectedInstrument.Symbol,
                report.FromDate,
                report.ToDate,
                report.FullRows,
                report.SummaryRows,
                report.SelectedDetail)
            {
                Owner = Application.Current?.MainWindow
            };

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Summary window opening");
            ReplayStatusText = $"Summary opened for {selection.Date:yyyy-MM-dd}";
            window.Show();
            window.Activate();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Summary window shown");
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Summary failed: {ex.GetType().Name}: {ex.Message}");
            ReplayStatusText = "Summary failed";
            MessageBox.Show(
                $"Summary could not open: {ex.Message}",
                "Market Analyser",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static bool IsLiveRefreshAllowed(CatalogInstrumentViewModel instrument)
    {
        if (instrument.Source.Segment == MarketSegment.Commodity)
        {
            return true;
        }

        var now = DateTime.Now.TimeOfDay;
        var marketOpen = new TimeSpan(9, 15, 0);
        var marketClose = new TimeSpan(15, 30, 0);
        return now >= marketOpen && now <= marketClose;
    }

    private async Task<ReplaySelectionData?> LoadSelectedReplaySelectionAsync()
    {
        if (SelectedInstrument is null || ReplaySelectedDate is null)
        {
            ReplayStatusText = "Select symbol and date";
            return null;
        }

        var date = DateOnly.FromDateTime(ReplaySelectedDate.Value);
        var startFrom = BuildReplayStartTimestamp(date);
        var records = await sessionStore.LoadRecordsAsync(
            SelectedInstrument.Symbol,
            date,
            startFrom,
            CancellationToken.None);

        return new ReplaySelectionData(date, startFrom, records);
    }

    private async Task<ReplaySelectionData?> LoadOrDownloadSelectedReplaySelectionAsync()
    {
        if (SelectedInstrument is null || ReplaySelectedDate is null)
        {
            ReplayStatusText = "Select symbol and date";
            return null;
        }

        var date = DateOnly.FromDateTime(ReplaySelectedDate.Value);
        var from = date.ToDateTime(new TimeOnly(9, 15));
        var to = date.ToDateTime(new TimeOnly(15, 30));
        var startFrom = new DateTimeOffset(from, TimeZoneInfo.Local.GetUtcOffset(from));
        var endAt = new DateTimeOffset(to, TimeZoneInfo.Local.GetUtcOffset(to));

        var records = await sessionStore.LoadRecordsAsync(
            SelectedInstrument.Symbol,
            date,
            startFrom,
            CancellationToken.None);

        if (records.Count == 0)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Summary cache miss for {SelectedInstrument.Symbol} {date:yyyy-MM-dd}; downloading full day");
            ReplayStatusText = $"Downloading {SelectedInstrument.Symbol} {date:yyyy-MM-dd}";

            var snapshots = await historicalDataSource.GetSnapshotsAsync(
                SelectedInstrument.Symbol,
                startFrom,
                endAt,
                CancellationToken.None);

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Summary download returned {snapshots.Count:N0} snapshots");
            if (snapshots.Count > 0)
            {
                foreach (var snapshot in snapshots.OrderBy(snapshot => snapshot.Timestamp))
                {
                    await sessionStore.AppendAsync(snapshot, null, BuildMarketSignal(snapshot), CancellationToken.None);
                }

                records = await sessionStore.LoadRecordsAsync(
                    SelectedInstrument.Symbol,
                    date,
                    startFrom,
                    CancellationToken.None);
            }
        }

        return new ReplaySelectionData(date, startFrom, records);
    }

    private static ReplayReportData BuildReplayReport(ReplaySelectionData selection)
    {
        var records = selection.Records;
        if (records.Count == 0)
        {
            return new ReplayReportData(selection.Date, selection.Date, [], [], string.Empty);
        }

        var daySummary = BuildDailySummary(selection.Date, records);
        var fullRows = BuildCallRows(records);
        var summaryRows = BuildTradeSummaryRows(records);
        return new ReplayReportData(selection.Date, selection.Date, fullRows, summaryRows, daySummary.Text);
    }

    private static IReadOnlyList<ReplayCallSummaryRow> BuildCallRows(IReadOnlyList<MarketSessionRecord> records)
    {
        return records
            .Where(record =>
            {
                var label = (record.Signal ?? string.Empty).Trim().ToUpperInvariant();
                return label is "BUY" or "SELL";
            })
            .Select((record, index) =>
            {
                var label = (record.Signal ?? string.Empty).Trim().ToUpperInvariant();
                var signalDetail = record.SignalDetail ?? string.Empty;
                var outcome = signalDetail.Contains("Target hit", StringComparison.OrdinalIgnoreCase)
                    ? "Target hit"
                    : signalDetail.Contains("Stoploss hit", StringComparison.OrdinalIgnoreCase)
                        ? "Stoploss hit"
                        : signalDetail.Contains("pending", StringComparison.OrdinalIgnoreCase)
                            ? "Pending"
                            : string.Empty;

                return new ReplayCallSummaryRow(
                    index + 1,
                    record.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
                    record.Timestamp.ToLocalTime().ToString("yyyy-MM-dd"),
                    label,
                    record.SelectedStrike is null ? string.Empty : FormatNumber(record.SelectedStrike.Value),
                    FormatNumber(record.Spot),
                    GetSignalEntry(signalDetail),
                    GetSignalStopLoss(signalDetail),
                    GetSignalTarget(signalDetail),
                    outcome,
                    signalDetail);
            })
            .ToArray();
    }

    private static IReadOnlyList<ReplayTradeSummaryRow> BuildTradeSummaryRows(IReadOnlyList<MarketSessionRecord> records)
    {
        var ordered = records.OrderBy(record => record.Timestamp).ToArray();
        var rows = new List<ReplayTradeSummaryRow>();
        var current = default(TradeBuilder);

        foreach (var record in ordered)
        {
            var label = (record.Signal ?? string.Empty).Trim().ToUpperInvariant();
            var detail = record.SignalDetail ?? string.Empty;
            var isOpenSignal = label is "BUY" or "SELL" && !IsClosedDetail(detail);

            if (current is null)
            {
                if (!isOpenSignal)
                {
                    continue;
                }

                current = new TradeBuilder(record, label);
                continue;
            }

            if (isOpenSignal)
            {
                if (label != current.Signal)
                {
                    rows.Add(current.Build());
                    current = new TradeBuilder(record, label);
                }
                continue;
            }

            current.Observe(record);

            if (IsClosedDetail(detail))
            {
                current.Close(record, detail);
                rows.Add(current.Build());
                current = null;
            }
        }

        if (current is not null)
        {
            rows.Add(current.Build());
        }

        return rows
            .OrderBy(row => row.StartTime)
            .ToArray();
    }

    private static string GetSignalEntry(string detail)
    {
        return ExtractAfterLabel(detail, "Entry");
    }

    private static string GetSignalStopLoss(string detail)
    {
        return ExtractAfterLabel(detail, "SL");
    }

    private static string GetSignalTarget(string detail)
    {
        return ExtractAfterLabel(detail, "T1");
    }

    private static string ExtractAfterLabel(string detail, string label)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        var token = $"{label} ";
        var index = detail.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return string.Empty;
        }

        var start = index + token.Length;
        var end = detail.IndexOf('|', start);
        var value = end < 0 ? detail[start..] : detail[start..end];
        return value.Trim();
    }

    private static bool IsClosedDetail(string detail)
    {
        return detail.Contains("Target hit", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("Stoploss hit", StringComparison.OrdinalIgnoreCase);
    }

    private DateTimeOffset BuildReplayStartTimestamp(DateOnly date)
    {
        var localStart = date.ToDateTime(new TimeOnly(ReplaySelectedHour, ReplaySelectedMinute));
        var offset = TimeZoneInfo.Local.GetUtcOffset(localStart);
        return new DateTimeOffset(localStart, offset);
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
        ResetSignalState();
        refreshTimer.Start();
        _ = RefreshAsync(selectionCts.Token, force: true);
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
        if (symbol.Contains(':'))
        {
            return symbol;
        }

        return symbol switch
        {
            "NIFTY" or "NIFTY 50" => "NSE:NIFTY",
            "BANKNIFTY" or "NIFTY BANK" => "NSE:BANKNIFTY",
            "FINNIFTY" => "NSE:CNXFINANCE",
            "SENSEX" => "BSE:SENSEX",
            _ when instrument.Source.Segment == MarketSegment.Commodity => $"MCX:{symbol}",
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

        var snapshot = BuildReplaySnapshot(replayIndex);
        var signal = BuildMarketSignal();
        if (!string.Equals(signal.Label, "BUY", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(signal.Label, "SELL", StringComparison.OrdinalIgnoreCase))
        {
            ReplayOutcomeText = $"WAIT -> no trade; {signal.Detail}";
            ReplayOutcomeForeground = Brushes.Goldenrod;
            return;
        }

        if (signal.Entry is null || signal.StopLoss is null || signal.Target is null)
        {
            ReplayOutcomeText = "Signal plan unavailable";
            ReplayOutcomeForeground = Brushes.LightSlateGray;
            return;
        }

        if (!signalPlanOpen &&
            (signal.Detail.Contains("Target hit", StringComparison.OrdinalIgnoreCase) ||
             signal.Detail.Contains("Stoploss hit", StringComparison.OrdinalIgnoreCase)))
        {
            ReplayOutcomeText = $"{signal.Label} @ {signal.Entry:N2} SL {signal.StopLoss:N2} T1 {signal.Target:N2} -> {signal.Detail}";
            ReplayOutcomeForeground = signal.Detail.Contains("Target hit", StringComparison.OrdinalIgnoreCase)
                ? Brushes.MediumSeaGreen
                : Brushes.IndianRed;
            return;
        }

        var futureRecords = replayRecords
            .Skip(replayIndex + 1)
            .ToArray();
        if (futureRecords.Length == 0)
        {
            ReplayOutcomeText = $"{signal.Label} @ {signal.Entry:N2} -> pending: no future replay data";
            ReplayOutcomeForeground = Brushes.LightSlateGray;
            return;
        }

        var targetIndex = signal.Label == "BUY"
            ? FindIndex(futureRecords, record => record.Spot >= signal.Target.Value)
            : FindIndex(futureRecords, record => record.Spot <= signal.Target.Value);
        var stopIndex = signal.Label == "BUY"
            ? FindIndex(futureRecords, record => record.Spot <= signal.StopLoss.Value)
            : FindIndex(futureRecords, record => record.Spot >= signal.StopLoss.Value);

        var outcome = targetIndex switch
        {
            >= 0 when stopIndex < 0 || targetIndex < stopIndex => "target hit",
            _ when stopIndex >= 0 && (targetIndex < 0 || stopIndex < targetIndex) => "stoploss hit",
            >= 0 when stopIndex == targetIndex => "target and stoploss touched together",
            _ => "pending"
        };

        ReplayOutcomeForeground = outcome switch
        {
            "target hit" => Brushes.MediumSeaGreen,
            "stoploss hit" => Brushes.IndianRed,
            "target and stoploss touched together" => Brushes.Goldenrod,
            _ => Brushes.LightSlateGray
        };

        var hitIndex = outcome == "target hit"
            ? targetIndex
            : outcome == "stoploss hit"
                ? stopIndex
                : -1;
        var hitText = hitIndex >= 0
            ? $" at {futureRecords[hitIndex].Timestamp.ToLocalTime():HH:mm:ss}"
            : string.Empty;

        ReplayOutcomeText =
            $"{signal.Label} @ {signal.Entry:N2} SL {signal.StopLoss:N2} T1 {signal.Target:N2} -> {outcome}{hitText}";
    }

    private static int FindIndex<T>(IReadOnlyList<T> items, Func<T, bool> predicate)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (predicate(items[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private MarketSnapshot BuildReplaySnapshot(int index)
    {
        var record = replayRecords[index];
        var recordsToPoint = replayRecords.Take(index + 1).ToArray();
        var priceSeries = recordsToPoint
            .Select(item => new ChartPoint(item.Timestamp, item.Spot))
            .ToArray();
        var volumeSeries = recordsToPoint
            .Select(item => new ChartPoint(item.Timestamp, item.UnderlyingVolume))
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
            volumeSeries,
            oiSeries,
            BuildReplayStrikeHistory(recordsToPoint),
            null,
            string.Empty,
            record.Depth?.ToDepth());
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
                0,
                record.CallTopBidPrice,
                record.CallTopBidQuantity,
                record.CallTopAskPrice,
                record.CallTopAskQuantity),
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
                0,
                record.PutTopBidPrice,
                record.PutTopBidQuantity,
                record.PutTopAskPrice,
                record.PutTopAskQuantity),
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
                $"{FormatNumber(strongestCall.Strike)} CE OI change {CompactNumberFormatter.FormatChange(strongestCall.Call.OpenInterestChange)}",
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
                $"{FormatNumber(strongestPut.Strike)} PE OI change {CompactNumberFormatter.FormatChange(strongestPut.Put.OpenInterestChange)}",
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
        return currentSignal;
    }

    private static MarketSignalViewModel BuildMarketSignal(MarketSnapshot snapshot)
    {
        var score = 0;
        var reasons = new List<string>();
        var currentPrice = snapshot.PriceSeries.LastOrDefault()?.Value ?? snapshot.Spot;
        var currentVolume = snapshot.VolumeSeries.LastOrDefault()?.Value ?? 0;
        var rsi = CalculateRsi(snapshot.PriceSeries, 14);
        var vwap = CalculateVwap(snapshot.PriceSeries, snapshot.VolumeSeries);
        var volumeRatio = CalculateVolumeRatio(snapshot.VolumeSeries);

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

        var depthPressure = AggregateDepthPressure(snapshot);
        if (depthPressure > 0)
        {
            score++;
            reasons.Add(DescribeDepthPressure(snapshot));
        }
        else if (depthPressure < 0)
        {
            score--;
            reasons.Add(DescribeDepthPressure(snapshot));
        }

        if (rsi is not null)
        {
            if (rsi >= 60m)
            {
                score++;
                reasons.Add($"RSI {rsi:N1}");
            }
            else if (rsi <= 40m)
            {
                score--;
                reasons.Add($"RSI {rsi:N1}");
            }
        }

        if (vwap is not null && currentPrice > 0)
        {
            var distanceFromVwap = Math.Abs(currentPrice - vwap.Value) / currentPrice;
            if (currentPrice > vwap.Value && distanceFromVwap >= 0.0015m)
            {
                score++;
                reasons.Add($"above VWAP {vwap:N2}");
            }
            else if (currentPrice < vwap.Value && distanceFromVwap >= 0.0015m)
            {
                score--;
                reasons.Add($"below VWAP {vwap:N2}");
            }
        }

        if (currentVolume > 0 && volumeRatio is not null)
        {
            if (volumeRatio >= 1.35m)
            {
                score++;
                reasons.Add($"vol {currentVolume:N0} x{volumeRatio:N2}");
            }
            else if (volumeRatio <= 0.75m)
            {
                score--;
                reasons.Add($"vol {currentVolume:N0} x{volumeRatio:N2}");
            }
        }

        var riskPoints = ResolveSignalRiskPoints(snapshot);
        var label = score >= 3
            ? "BUY"
            : score <= -3
                ? "SELL"
                : "WAIT";
        var foreground = score >= 3
            ? Brushes.MediumSeaGreen
            : score <= -3
                ? Brushes.IndianRed
                : Brushes.Goldenrod;
        var entry = snapshot.Spot;
        decimal? stopLoss = null;
        decimal? target = null;
        if (label == "BUY")
        {
            stopLoss = entry - riskPoints;
            target = entry + riskPoints;
        }
        else if (label == "SELL")
        {
            stopLoss = entry + riskPoints;
            target = entry - riskPoints;
        }
        var detail = label == "WAIT"
            ? reasons.Count == 0 ? "No clear pressure yet" : string.Join(" | ", reasons.Take(4))
            : $"Entry {entry:N2} | SL {stopLoss:N2} | T1 {target:N2} | {string.Join(" | ", reasons.Take(3))}";

        return new MarketSignalViewModel(
            label,
            detail,
            foreground,
            entry,
            stopLoss,
            target,
            riskPoints);
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
            var pv = priceSeries[i].Value * volumeSeries[i].Value;
            priceVolume += pv;
            volume += volumeSeries[i].Value;
        }

        if (volume <= 0)
        {
            return null;
        }

        return decimal.Round(priceVolume / volume, 2);
    }

    private static decimal? CalculateVolumeRatio(IReadOnlyList<ChartPoint> volumeSeries)
    {
        if (volumeSeries.Count < 2)
        {
            return null;
        }

        var current = volumeSeries.Last().Value;
        if (current <= 0)
        {
            return null;
        }

        var prior = volumeSeries
            .Take(Math.Max(1, volumeSeries.Count - 1))
            .TakeLast(Math.Min(20, volumeSeries.Count - 1))
            .Select(point => point.Value)
            .Where(value => value > 0)
            .ToArray();

        if (prior.Length == 0)
        {
            return null;
        }

        var average = prior.Average();
        if (average <= 0)
        {
            return null;
        }

        return decimal.Round(current / average, 2);
    }

    private void UpdateSignalState(MarketSnapshot snapshot)
    {
        var freshSignal = BuildMarketSignal(snapshot);

        if (signalPlanOpen)
        {
            var outcome = ResolveLiveSignalOutcome(currentSignal, snapshot.Spot);
            if (outcome is not null)
            {
                signalPlanOpen = false;
                signalClosedLabel = currentSignal.Label;
                signalClosedAt = snapshot.Timestamp;
                signalClosedOutcomeText = $"{FormatOutcome(outcome)} at {snapshot.Timestamp.ToLocalTime():HH:mm:ss}";
                SetCurrentSignal(currentSignal with
                {
                    Detail = $"{BuildLiveSignalDetail(activeSignalPlanDetail ?? currentSignal.Detail, snapshot)} | {signalClosedOutcomeText}",
                    Foreground = currentSignal.Foreground
                });
                return;
            }

            SetCurrentSignal(currentSignal with
            {
                Detail = BuildLiveSignalDetail(activeSignalPlanDetail ?? currentSignal.Detail, snapshot)
            });
            return;
        }

        if (freshSignal.Label == "WAIT")
        {
            if (IsClosedSignal(currentSignal))
            {
                SetCurrentSignal(currentSignal with
                {
                    Detail = BuildLiveSignalDetail(activeSignalPlanDetail ?? currentSignal.Detail, snapshot) +
                        (string.IsNullOrWhiteSpace(signalClosedOutcomeText) ? string.Empty : $" | {signalClosedOutcomeText}")
                });
                return;
            }

            SetCurrentSignal(freshSignal);
            return;
        }

        if (IsClosedSignal(currentSignal) &&
            string.Equals(freshSignal.Label, signalClosedLabel, StringComparison.OrdinalIgnoreCase) &&
            signalClosedAt is not null &&
            snapshot.Timestamp - signalClosedAt.Value < SignalRearmCooldown)
        {
            return;
        }

        signalClosedLabel = null;
        signalClosedAt = null;
        signalClosedOutcomeText = null;
        activeSignalPlanDetail = freshSignal.Detail;
        signalPlanOpen = true;
        SetCurrentSignal(freshSignal with
        {
            Detail = BuildLiveSignalDetail(freshSignal.Detail, snapshot)
        });
    }

    private void ResetSignalState()
    {
        signalPlanOpen = false;
        activeSignalPlanDetail = null;
        signalClosedOutcomeText = null;
        signalClosedLabel = null;
        signalClosedAt = null;
        lastSignalLabel = null;
        SetCurrentSignal(new MarketSignalViewModel("Waiting", "Live snapshot not loaded", Brushes.LightSlateGray));
    }

    private void SetCurrentSignal(MarketSignalViewModel next)
    {
        if (EqualityComparer<MarketSignalViewModel>.Default.Equals(currentSignal, next))
        {
            return;
        }

        currentSignal = next;
        OnPropertyChanged(nameof(MarketSignalText));
        OnPropertyChanged(nameof(MarketSignalDetail));
        OnPropertyChanged(nameof(MarketSignalForeground));
    }

    private static string FormatOutcome(string outcome)
    {
        return outcome switch
        {
            "target hit" => "Target hit",
            "stoploss hit" => "Stoploss hit",
            _ => "Trade closed"
        };
    }

    private static string BuildLiveSignalDetail(string baseDetail, MarketSnapshot snapshot)
    {
        return $"Spot {FormatNumber(snapshot.Spot)} @ {snapshot.Timestamp.ToLocalTime():HH:mm:ss} | {baseDetail}";
    }

    private static DailySummary BuildDailySummary(DateOnly date, IReadOnlyList<MarketSessionRecord> records)
    {
        var ordered = records.OrderBy(record => record.Timestamp).ToArray();
        var first = ordered.First();
        var last = ordered.Last();
        var high = ordered.MaxBy(record => record.Spot)!;
        var low = ordered.MinBy(record => record.Spot)!;
        var netMove = last.Spot - first.Spot;

        var buySetups = 0;
        var sellSetups = 0;
        var targetHits = 0;
        var stoplossHits = 0;
        string? activeLabel = null;
        var tradeClosed = true;
        var trail = new List<string>();

        foreach (var record in ordered)
        {
            var label = (record.Signal ?? string.Empty).Trim().ToUpperInvariant();
            if (label is not ("BUY" or "SELL"))
            {
                continue;
            }

            var detail = record.SignalDetail ?? string.Empty;
            var closed = detail.Contains("Target hit", StringComparison.OrdinalIgnoreCase) ||
                         detail.Contains("Stoploss hit", StringComparison.OrdinalIgnoreCase);

            if (tradeClosed)
            {
                if (closed)
                {
                    continue;
                }

                tradeClosed = false;
                activeLabel = label;
                if (label == "BUY")
                {
                    buySetups++;
                }
                else
                {
                    sellSetups++;
                }

                trail.Add($"{record.Timestamp.ToLocalTime():HH:mm} {label} @ {record.Spot:N2}");
                continue;
            }

            if (!string.Equals(activeLabel, label, StringComparison.OrdinalIgnoreCase))
            {
                activeLabel = label;
                if (label == "BUY")
                {
                    buySetups++;
                }
                else
                {
                    sellSetups++;
                }

                trail.Add($"{record.Timestamp.ToLocalTime():HH:mm} {label} @ {record.Spot:N2}");
            }

            if (closed)
            {
                tradeClosed = true;
                if (detail.Contains("Target hit", StringComparison.OrdinalIgnoreCase))
                {
                    targetHits++;
                    trail.Add($"{record.Timestamp.ToLocalTime():HH:mm} Target hit");
                }
                else
                {
                    stoplossHits++;
                    trail.Add($"{record.Timestamp.ToLocalTime():HH:mm} Stoploss hit");
                }
            }
        }

        var totalSetups = buySetups + sellSetups;
        var builder = new StringBuilder();
        builder.AppendLine($"{date:yyyy-MM-dd} summary");
        var netMoveText = FormatSigned(netMove);
        builder.AppendLine($"Open {first.Spot:N2}, high {high.Spot:N2} at {high.Timestamp.ToLocalTime():HH:mm}, low {low.Spot:N2} at {low.Timestamp.ToLocalTime():HH:mm}, latest {last.Spot:N2}; net {netMoveText}.");
        builder.AppendLine($"Calls {totalSetups:N0} ({buySetups} BUY, {sellSetups} SELL) | {targetHits} target hit, {stoplossHits} stoploss hit{(tradeClosed ? string.Empty : " | pending")}.");

        if (trail.Count == 0)
        {
            builder.AppendLine("No BUY/SELL call was generated in the selected range.");
        }
        else
        {
            builder.AppendLine($"Trail: {string.Join(" -> ", trail.TakeLast(8))}");
        }

        if (!tradeClosed && activeLabel is not null)
        {
            builder.AppendLine($"Current: {activeLabel} pending");
        }

        return new DailySummary(date, buySetups, sellSetups, targetHits, stoplossHits, totalSetups, netMoveText, builder.ToString().TrimEnd());
    }

    private static bool IsClosedSignal(MarketSignalViewModel signal)
    {
        return signal.Detail.Contains("Target hit", StringComparison.OrdinalIgnoreCase) ||
               signal.Detail.Contains("Stoploss hit", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveLiveSignalOutcome(MarketSignalViewModel signal, decimal spot)
    {
        if (signal.Entry is null || signal.StopLoss is null || signal.Target is null)
        {
            return null;
        }

        if (string.Equals(signal.Label, "BUY", StringComparison.OrdinalIgnoreCase))
        {
            if (spot >= signal.Target.Value)
            {
                return "target hit";
            }

            if (spot <= signal.StopLoss.Value)
            {
                return "stoploss hit";
            }
        }
        else if (string.Equals(signal.Label, "SELL", StringComparison.OrdinalIgnoreCase))
        {
            if (spot <= signal.Target.Value)
            {
                return "target hit";
            }

            if (spot >= signal.StopLoss.Value)
            {
                return "stoploss hit";
            }
        }

        return null;
    }

    private sealed record ReplaySelectionData(
        DateOnly Date,
        DateTimeOffset StartFrom,
        IReadOnlyList<MarketSessionRecord> Records);

    private sealed record ReplayReportData(
        DateOnly FromDate,
        DateOnly ToDate,
        IReadOnlyList<ReplayCallSummaryRow> FullRows,
        IReadOnlyList<ReplayTradeSummaryRow> SummaryRows,
        string SelectedDetail);

    private sealed record DailySummary(
        DateOnly Date,
        int BuyCount,
        int SellCount,
        int TargetHits,
        int StoplossHits,
        int TotalCalls,
        string NetMoveText,
        string Text);

    public sealed record ReplayCallSummaryRow(
        int RowNo,
        string TimeText,
        string DateText,
        string Signal,
        string StrikeText,
        string SpotText,
        string EntryText,
        string StopLossText,
        string TargetText,
        string OutcomeText,
        string Detail);

    public sealed record ReplayTradeSummaryRow(
        int RowNo,
        string StartTime,
        string DateText,
        string Signal,
        string StrikeText,
        string EntryText,
        string ExitTime,
        string OutcomeText,
        string BestExcursionText,
        string BestExcursionTime,
        string StopLossText,
        string TargetText,
        string Detail);

    private sealed class TradeBuilder
    {
        public TradeBuilder(MarketSessionRecord firstRecord, string signal)
        {
            Signal = signal;
            DateText = firstRecord.Timestamp.ToLocalTime().ToString("yyyy-MM-dd");
            StartTime = firstRecord.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            EntrySpot = firstRecord.Spot;
            BestSpot = firstRecord.Spot;
            BestTime = firstRecord.Timestamp;
            StrikeText = firstRecord.SelectedStrike is null ? string.Empty : FormatNumber(firstRecord.SelectedStrike.Value);
            StopLossText = GetSignalStopLoss(firstRecord.SignalDetail ?? string.Empty);
            TargetText = GetSignalTarget(firstRecord.SignalDetail ?? string.Empty);
            Detail = firstRecord.SignalDetail ?? string.Empty;
        }

        public string Signal { get; }
        public string DateText { get; }
        public string StartTime { get; }
        public decimal EntrySpot { get; private set; }
        public decimal BestSpot { get; private set; }
        public DateTimeOffset BestTime { get; private set; }
        public DateTimeOffset? ExitTime { get; private set; }
        public string OutcomeText { get; private set; } = "Pending";
        public string StrikeText { get; }
        public string StopLossText { get; }
        public string TargetText { get; }
        public string Detail { get; private set; }

        public void Observe(MarketSessionRecord record)
        {
            if (Signal == "BUY")
            {
                if (record.Spot > BestSpot)
                {
                    BestSpot = record.Spot;
                    BestTime = record.Timestamp;
                }
            }
            else if (record.Spot < BestSpot)
            {
                BestSpot = record.Spot;
                BestTime = record.Timestamp;
            }
        }

        public void Close(MarketSessionRecord record, string detail)
        {
            ExitTime = record.Timestamp;
            OutcomeText = detail.Contains("Target hit", StringComparison.OrdinalIgnoreCase)
                ? "Target hit"
                : detail.Contains("Stoploss hit", StringComparison.OrdinalIgnoreCase)
                    ? "Stoploss hit"
                    : "Closed";
            EntrySpot = EntrySpot == 0 ? record.Spot : EntrySpot;
            Detail = detail;
        }

        public ReplayTradeSummaryRow Build()
        {
            var bestExcursionText = Signal == "BUY"
                ? BestSpot.ToString("N2")
                : BestSpot.ToString("N2");
            return new ReplayTradeSummaryRow(
                0,
                StartTime,
                DateText,
                Signal,
                StrikeText,
                EntrySpot.ToString("N2"),
                ExitTime?.ToLocalTime().ToString("HH:mm:ss") ?? string.Empty,
                OutcomeText,
                bestExcursionText,
                BestTime.ToLocalTime().ToString("HH:mm:ss"),
                StopLossText,
                TargetText,
                Detail);
        }
    }

    private static long AggregateDepthPressure(MarketSnapshot snapshot)
    {
        if (snapshot.Depth is { } depth && (depth.BidQuantity > 0 || depth.AskQuantity > 0))
        {
            return depth.Imbalance;
        }

        return AggregateStrikeDepthPressure(snapshot);
    }

    private static long AggregateStrikeDepthPressure(MarketSnapshot snapshot)
    {
        return snapshot.Strikes
            .OrderBy(strike => Math.Abs(strike.Strike - snapshot.Spot))
            .Take(3)
            .Sum(strike => strike.Call.DepthImbalance - strike.Put.DepthImbalance);
    }

    private static string DescribeDepthPressure(MarketSnapshot snapshot)
    {
        if (snapshot.Depth is { } depth && (depth.BidQuantity > 0 || depth.AskQuantity > 0))
        {
            var ratio = depth.AskQuantity > 0 ? (decimal)depth.BidQuantity / depth.AskQuantity : 0;
            var prefix = ratio >= UnusualUnderlyingBullishRatio
                ? "Unusual bullish"
                : ratio <= UnusualUnderlyingBearishRatio
                    ? "Unusual bearish"
                    : ratio >= UnderlyingBullishRatio
                        ? "Bullish"
                        : ratio <= UnderlyingBearishRatio
                            ? "Bearish"
                            : "Balanced";
            var bestBid = depth.BestBid is null ? string.Empty : $" best bid {CompactNumberFormatter.FormatCount(depth.BestBid.Quantity)}@{depth.BestBid.Price:N2}";
            var bestAsk = depth.BestAsk is null ? string.Empty : $" best ask {CompactNumberFormatter.FormatCount(depth.BestAsk.Quantity)}@{depth.BestAsk.Price:N2}";
            return $"{prefix} 5L depth B {CompactNumberFormatter.FormatCount(depth.BidQuantity)} / A {CompactNumberFormatter.FormatCount(depth.AskQuantity)}{bestBid}{bestAsk}";
        }

        var strikeDepth = AggregateStrikeDepthPressure(snapshot);
        return Math.Abs(strikeDepth) >= UnusualStrikeDepthThreshold
            ? $"Unusual strike depth {CompactNumberFormatter.FormatChange(strikeDepth)}"
            : strikeDepth == 0
            ? "depth balanced"
            : $"strike depth {CompactNumberFormatter.FormatChange(strikeDepth)}";
    }

    private static Brush ResolveDepthPressureForeground(MarketSnapshot snapshot)
    {
        if (snapshot.Depth is { } depth && (depth.BidQuantity > 0 || depth.AskQuantity > 0))
        {
            var ratio = depth.AskQuantity > 0 ? (decimal)depth.BidQuantity / depth.AskQuantity : 0;
            if (ratio >= UnderlyingBullishRatio)
            {
                return Brushes.MediumSeaGreen;
            }

            if (ratio <= UnderlyingBearishRatio)
            {
                return Brushes.IndianRed;
            }

            return Brushes.Goldenrod;
        }

        var strikeDepth = AggregateStrikeDepthPressure(snapshot);
        if (Math.Abs(strikeDepth) >= UnusualStrikeDepthThreshold)
        {
            return strikeDepth > 0 ? Brushes.MediumSeaGreen : Brushes.IndianRed;
        }

        return Brushes.Goldenrod;
    }

    private static decimal ResolveSignalRiskPoints(MarketSnapshot snapshot)
    {
        var strikeInterval = InferStrikeInterval(snapshot.Strikes);
        var rawRisk = Math.Max(strikeInterval, snapshot.Spot * 0.002m);
        var roundedRisk = decimal.Round(rawRisk / 5m, 0) * 5m;
        return Math.Max(5m, roundedRisk);
    }

    private static decimal InferStrikeInterval(IReadOnlyList<OptionStrikeSnapshot> strikes)
    {
        var ordered = strikes
            .Select(strike => strike.Strike)
            .Distinct()
            .Order()
            .ToArray();

        if (ordered.Length < 2)
        {
            return 50m;
        }

        return ordered
            .Zip(ordered.Skip(1), (left, right) => right - left)
            .Where(diff => diff > 0)
            .DefaultIfEmpty(50m)
            .Min();
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

    public string CallOpenInterestText => CompactNumberFormatter.FormatCount(Snapshot.Call.OpenInterest);

    public string CallVolumeText => CompactNumberFormatter.FormatCount(Snapshot.Call.Volume);

    public string CallOpenInterestChangeText => CompactNumberFormatter.FormatChange(Snapshot.Call.OpenInterestChange);

    public string PutOpenInterestChangeText => CompactNumberFormatter.FormatChange(Snapshot.Put.OpenInterestChange);

    public string PutVolumeText => CompactNumberFormatter.FormatCount(Snapshot.Put.Volume);

    public string PutOpenInterestText => CompactNumberFormatter.FormatCount(Snapshot.Put.OpenInterest);

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

internal static class CompactNumberFormatter
{
    public static string FormatCount(long value)
    {
        return Format(value, includeSign: false);
    }

    public static string FormatChange(long value)
    {
        return Format(value, includeSign: true);
    }

    private static string Format(long value, bool includeSign)
    {
        var abs = Math.Abs(value);
        var formatted = abs >= 10_000_000
            ? $"{abs / 10_000_000m:N1}Cr"
            : abs >= 100_000
                ? $"{abs / 100_000m:N1}L"
                : abs >= 1_000
                    ? $"{abs / 1_000m:N1}k"
                    : abs.ToString("N0", CultureInfo.CurrentCulture);

        if (value < 0)
        {
            return "-" + formatted;
        }

        return includeSign && value > 0 ? "+" + formatted : formatted;
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

    public int LotSize => Source.LotSize;

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

public sealed record MarketSignalViewModel(
    string Label,
    string Detail,
    Brush Foreground,
    decimal? Entry = null,
    decimal? StopLoss = null,
    decimal? Target = null,
    decimal? RiskPoints = null);

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
