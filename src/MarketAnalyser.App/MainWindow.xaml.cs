using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        Closing += (_, _) => this.viewModel.StopBackgroundWork();
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

    private void StrikeRowsGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { DataContext: OptionChainRowViewModel strikeRow })
        {
            viewModel.ShowStrikeDetails(strikeRow.Snapshot);
        }
    }

    private void LiveScanList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { DataContext: LiveScanHitViewModel scanHit })
        {
            viewModel.SelectInstrument(scanHit.Symbol);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
