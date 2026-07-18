using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using MarketAnalyser.App.ViewModels;

namespace MarketAnalyser.App;

public partial class ReplaySummaryWindow : Window
{
    private readonly ObservableCollection<MainWindowViewModel.ReplayCallSummaryRow> fullRows = [];
    private readonly ObservableCollection<MainWindowViewModel.ReplayTradeSummaryRow> summaryRows = [];

    public ReplaySummaryWindow(
        string symbol,
        DateOnly fromDate,
        DateOnly toDate,
        IReadOnlyList<MainWindowViewModel.ReplayCallSummaryRow> fullDataRows,
        IReadOnlyList<MainWindowViewModel.ReplayTradeSummaryRow> tradeSummaryRows,
        string initialDetail)
    {
        InitializeComponent();
        Title = $"{symbol} Summary";
        TitleText.Text = $"{symbol} selected day summary";
        SubtitleText.Text = $"{fromDate:yyyy-MM-dd} to {toDate:yyyy-MM-dd}";
        SummaryGrid.ItemsSource = fullRows;
        TradeSummaryGrid.ItemsSource = summaryRows;
        foreach (var row in fullDataRows)
        {
            fullRows.Add(row);
        }

        foreach (var row in tradeSummaryRows.Select((item, index) => item with { RowNo = index + 1 }))
        {
            summaryRows.Add(row);
        }

        if (fullRows.Count > 0)
        {
            SummaryGrid.SelectedItem = fullRows[0];
        }

        if (!string.IsNullOrWhiteSpace(initialDetail))
        {
            SummaryBox.Text = initialDetail;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SummaryBox.Text))
        {
            Clipboard.SetText(SummaryBox.Text);
        }
    }

    private void FullDataModeButton_Click(object sender, RoutedEventArgs e)
    {
        SummaryGrid.Visibility = Visibility.Visible;
        TradeSummaryGrid.Visibility = Visibility.Collapsed;
        if (SummaryGrid.Items.Count > 0)
        {
            SummaryGrid.SelectedIndex = 0;
        }
    }

    private void SummaryModeButton_Click(object sender, RoutedEventArgs e)
    {
        SummaryGrid.Visibility = Visibility.Collapsed;
        TradeSummaryGrid.Visibility = Visibility.Visible;
        if (TradeSummaryGrid.Items.Count > 0)
        {
            TradeSummaryGrid.SelectedIndex = 0;
        }
    }

    private void SummaryGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SummaryGrid.SelectedItem is MainWindowViewModel.ReplayCallSummaryRow row)
        {
            SummaryBox.Text = row.Detail;
        }
    }

    private void TradeSummaryGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TradeSummaryGrid.SelectedItem is MainWindowViewModel.ReplayTradeSummaryRow row)
        {
            SummaryBox.Text = row.Detail;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
