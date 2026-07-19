using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using MarketAnalyser.App.News;

namespace MarketAnalyser.App;

public partial class NewsWindow : Window, INotifyPropertyChanged
{
    private readonly MarketNewsService newsService = new();
    private readonly IReadOnlyList<string> favoriteSymbols;

    public NewsWindow(IReadOnlyList<string> favoriteSymbols)
    {
        InitializeComponent();
        DataContext = this;
        this.favoriteSymbols = favoriteSymbols;
        FavoriteText = favoriteSymbols.Count == 0
            ? "No favorites selected"
            : $"Watching: {string.Join(", ", favoriteSymbols.Take(8))}";
        Loaded += async (_, _) => await RefreshAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MarketNewsItem> FavoriteNews { get; } = [];

    public ObservableCollection<MarketNewsItem> MarketNews { get; } = [];

    public ObservableCollection<MarketNewsItem> GlobalNews { get; } = [];

    public string StatusText { get; private set; } = "Loading news...";

    public string FavoriteText { get; private set; } = string.Empty;

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            StatusText = "Fetching news...";
            OnPropertyChanged(nameof(StatusText));

            var items = await newsService.GetNewsAsync(favoriteSymbols, CancellationToken.None);

            FavoriteNews.Clear();
            MarketNews.Clear();
            GlobalNews.Clear();

            foreach (var item in items)
            {
                switch (item.Category)
                {
                    case "favorites":
                        FavoriteNews.Add(item);
                        break;
                    case "market":
                        MarketNews.Add(item);
                        break;
                    default:
                        GlobalNews.Add(item);
                        break;
                }
            }

            StatusText = $"Loaded {FavoriteNews.Count + MarketNews.Count + GlobalNews.Count:N0} news items";
            OnPropertyChanged(nameof(StatusText));
        }
        catch (Exception ex)
        {
            StatusText = $"News load failed: {ex.Message}";
            OnPropertyChanged(nameof(StatusText));
            MessageBox.Show(
                ex.Message,
                "Market Analyser",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
