using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MarketAnalyser.App.ViewModels;

namespace MarketAnalyser.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private string? centeredStrikeSymbol;

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

    private void StrikeRowsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        var selectedSymbol = viewModel.SelectedInstrument?.Symbol;
        if (string.IsNullOrWhiteSpace(selectedSymbol) ||
            string.Equals(centeredStrikeSymbol, selectedSymbol, StringComparison.OrdinalIgnoreCase) ||
            grid.SelectedItem is not OptionChainRowViewModel)
        {
            return;
        }

        grid.Dispatcher.BeginInvoke(() =>
        {
            var currentSymbol = viewModel.SelectedInstrument?.Symbol;
            if (string.IsNullOrWhiteSpace(currentSymbol) ||
                string.Equals(centeredStrikeSymbol, currentSymbol, StringComparison.OrdinalIgnoreCase) ||
                grid.SelectedItem is null)
            {
                return;
            }

            CenterSelectedStrike(grid);
            centeredStrikeSymbol = currentSymbol;
        }, DispatcherPriority.Background);
    }

    private void ResetCenteredStrike()
    {
        centeredStrikeSymbol = null;
    }

    private void LiveScanList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { DataContext: LiveScanHitViewModel scanHit })
        {
            viewModel.SelectInstrument(scanHit.Symbol);
        }
    }

    private static void CenterSelectedStrike(DataGrid grid)
    {
        if (grid.SelectedItem is null)
        {
            return;
        }

        grid.ScrollIntoView(grid.SelectedItem);
        grid.UpdateLayout();

        var scrollViewer = FindDescendant<ScrollViewer>(grid);
        if (scrollViewer is null)
        {
            return;
        }

        var selectedIndex = grid.Items.IndexOf(grid.SelectedItem);
        if (selectedIndex < 0)
        {
            return;
        }

        if (scrollViewer.CanContentScroll)
        {
            var viewportItems = Math.Max(1.0, scrollViewer.ViewportHeight);
            var targetOffset = Math.Max(0.0, selectedIndex - (viewportItems / 2.0));
            scrollViewer.ScrollToVerticalOffset(targetOffset);
            return;
        }

        var row = grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem) as DataGridRow;
        var rowHeight = row?.ActualHeight > 0 ? row.ActualHeight : grid.RowHeight > 0 ? grid.RowHeight : 30.0;
        var targetPixelOffset = Math.Max(0.0, (selectedIndex * rowHeight) - (scrollViewer.ViewportHeight / 2.0) + (rowHeight / 2.0));
        scrollViewer.ScrollToVerticalOffset(targetPixelOffset);
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

    private static T? FindDescendant<T>(DependencyObject current) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(current);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(current, i);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
