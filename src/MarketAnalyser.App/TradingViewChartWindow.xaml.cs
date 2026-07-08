using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace MarketAnalyser.App;

public partial class TradingViewChartWindow : Window
{
    private readonly string displaySymbol;
    private readonly string tradingViewSymbol;
    private bool isWebViewReady;

    public TradingViewChartWindow(string displaySymbol, string tradingViewSymbol)
    {
        InitializeComponent();
        this.displaySymbol = displaySymbol;
        this.tradingViewSymbol = tradingViewSymbol;
        Title = $"{displaySymbol} TradingView Chart";
        TitleText.Text = $"{displaySymbol} candles";
        Loaded += async (_, _) => await InitializeAndLoadChartAsync();
    }

    private async void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            await LoadChartAsync();
        }
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadChartAsync();
    }

    private async Task InitializeAndLoadChartAsync()
    {
        try
        {
            await ChartWebView.EnsureCoreWebView2Async();
            isWebViewReady = true;
            await LoadChartAsync();
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            MessageBox.Show(
                "TradingView chart could not be initialized. Please check WebView2 runtime installation.",
                "Market Analyser",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task LoadChartAsync()
    {
        if (!isWebViewReady)
        {
            await ChartWebView.EnsureCoreWebView2Async();
            isWebViewReady = true;
        }

        if (RequiresFullTradingViewPage())
        {
            ChartWebView.CoreWebView2.Navigate(BuildTradingViewUrl(CurrentInterval()));
            return;
        }

        ChartWebView.NavigateToString(BuildTradingViewHtml(CurrentInterval()));
    }

    private string CurrentInterval()
    {
        return (IntervalCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1";
    }

    private bool RequiresFullTradingViewPage()
    {
        return tradingViewSymbol.Equals("NSE:NIFTY", StringComparison.OrdinalIgnoreCase) ||
            tradingViewSymbol.Equals("NSE:BANKNIFTY", StringComparison.OrdinalIgnoreCase) ||
            tradingViewSymbol.Equals("NSE:CNXFINANCE", StringComparison.OrdinalIgnoreCase) ||
            tradingViewSymbol.Equals("BSE:SENSEX", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildTradingViewUrl(string interval)
    {
        var symbol = Uri.EscapeDataString(tradingViewSymbol);
        var chartInterval = Uri.EscapeDataString(interval);
        return $"https://www.tradingview.com/chart/?symbol={symbol}&interval={chartInterval}";
    }

    private string BuildTradingViewHtml(string interval)
    {
        var symbol = JsonSerializer.Serialize(tradingViewSymbol, JsonOptions);
        var chartInterval = JsonSerializer.Serialize(interval, JsonOptions);

        return $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <style>
    html, body, #chart { height: 100%; margin: 0; background: #0b1118; overflow: hidden; }
  </style>
</head>
<body>
  <div id="chart"></div>
  <script src="https://s3.tradingview.com/tv.js"></script>
  <script>
    new TradingView.widget({
      autosize: true,
      symbol: {{symbol}},
      interval: {{chartInterval}},
      timezone: "Asia/Kolkata",
      theme: "dark",
      style: "1",
      locale: "en",
      toolbar_bg: "#111A24",
      enable_publishing: false,
      allow_symbol_change: true,
      withdateranges: true,
      hide_side_toolbar: false,
      details: true,
      hotlist: false,
      calendar: false,
      container_id: "chart",
      studies: [
        "STD;VWAP",
        "STD;Supertrend",
        "STD;EMA",
        "STD;RSI",
        "STD;MACD"
      ],
      studies_overrides: {
        "moving average exponential.length": 20
      }
    });
  </script>
</body>
</html>
""";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
