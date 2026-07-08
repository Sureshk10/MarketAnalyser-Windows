using System.Windows;
using System.Windows.Input;

namespace MarketAnalyser.App;

public partial class ReplaySummaryWindow : Window
{
    public ReplaySummaryWindow(string symbol, DateOnly date, DateTimeOffset startFrom, string summaryText)
    {
        InitializeComponent();
        Title = $"{symbol} Summary";
        TitleText.Text = $"{symbol} selected day summary";
        SubtitleText.Text = $"{date:yyyy-MM-dd} from {startFrom.ToLocalTime():HH:mm}";
        SummaryBox.Text = summaryText;
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
