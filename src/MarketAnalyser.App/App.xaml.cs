using System.IO;
using System.Net.Http;
using System.Windows;
using MarketAnalyser.App.Session;
using MarketAnalyser.App.ViewModels;
using MarketAnalyser.Core.Configuration;
using MarketAnalyser.Core.Dhan;
using MarketAnalyser.Core.Market;
using MarketAnalyser.Core.Orders;

namespace MarketAnalyser.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppExceptionLogger.Log(args.Exception);
            args.Handled = true;
            MessageBox.Show(
                args.Exception.Message,
                "Market Analyser",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppExceptionLogger.Log(exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppExceptionLogger.Log(args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);

            var optionsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var options = AppOptions.Load(optionsPath);
            var catalog = new InstrumentCatalog(options.Instruments);
            var dataSource = CreateDataSource(options, catalog);
            var historicalDataSource = CreateHistoricalDataSource(options, catalog);
            var orderBroker = CreateOrderBroker(options);
            var viewModel = new MainWindowViewModel(
                dataSource,
                new AppPreferencesStore(),
                new MarketSessionStore(),
                new MovementTimelineStore(),
                historicalDataSource,
                orderBroker,
                options.Orders);
            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            AppExceptionLogger.Log(ex);
            MessageBox.Show(ex.Message, "Market Analyser startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static IMarketDataSource CreateDataSource(AppOptions options, InstrumentCatalog catalog)
    {
        if (string.Equals(options.DataSource.Mode, "Rest", StringComparison.OrdinalIgnoreCase))
        {
            return new RestMarketDataSource(new HttpClient
            {
                BaseAddress = new Uri(options.DataSource.RestBaseUrl.TrimEnd('/') + "/")
            });
        }

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.Dhan.RestBaseUrl.TrimEnd('/') + "/")
        };
        var dhanClient = new DhanClient(httpClient, options.Dhan);
        return new EmbeddedMarketDataSource(catalog, dhanClient, options.Dhan);
    }

    private static IHistoricalMarketDataSource CreateHistoricalDataSource(AppOptions options, InstrumentCatalog catalog)
    {
        if (options.Dhan is null || string.IsNullOrWhiteSpace(options.Dhan.ClientId) || string.IsNullOrWhiteSpace(options.Dhan.AccessToken))
        {
            return new EmptyHistoricalMarketDataSource();
        }

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.Dhan.RestBaseUrl.TrimEnd('/') + "/")
        };
        var dhanClient = new DhanClient(httpClient, options.Dhan);
        return new DhanHistoricalMarketDataSource(catalog, dhanClient);
    }

    private static IOrderBroker CreateOrderBroker(AppOptions options)
    {
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.Dhan.RestBaseUrl.TrimEnd('/') + "/")
        };

        return OrderBrokerFactory.Create(options, httpClient);
    }
}
