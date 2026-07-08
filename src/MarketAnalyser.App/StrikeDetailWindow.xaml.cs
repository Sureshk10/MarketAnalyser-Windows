using System.Windows;
using System.Windows.Input;

namespace MarketAnalyser.App;

public partial class StrikeDetailWindow : Window
{
    public StrikeDetailWindow(string symbol, string strikeLabel, string detailText)
    {
        InitializeComponent();
        Title = $"{symbol} Strike Details";
        TitleText.Text = $"{symbol} strike details";
        SubtitleText.Text = strikeLabel;
        DetailBox.Text = detailText;
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
        if (!string.IsNullOrWhiteSpace(DetailBox.Text))
        {
            Clipboard.SetText(DetailBox.Text);
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
