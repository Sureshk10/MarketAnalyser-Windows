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

        ChartWebView.CoreWebView2.Navigate(BuildTradingViewUrl(CurrentInterval()));
    }

    private string CurrentInterval()
    {
        return (IntervalCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1";
    }

    private string BuildTradingViewUrl(string interval)
    {
        var symbol = Uri.EscapeDataString(tradingViewSymbol);
        var chartInterval = Uri.EscapeDataString(interval);
        return $"https://www.tradingview.com/chart/?symbol={symbol}&interval={chartInterval}";
    }
}
