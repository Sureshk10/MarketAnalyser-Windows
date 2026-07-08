using System.Windows;
using MarketAnalyser.App.ViewModels;

namespace MarketAnalyser.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try
            {
                await this.viewModel.StartAsync();
            }
            catch (Exception ex)
            {
                AppExceptionLogger.Log(ex);
                MessageBox.Show(ex.Message, "Market Analyser", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
    }
}
