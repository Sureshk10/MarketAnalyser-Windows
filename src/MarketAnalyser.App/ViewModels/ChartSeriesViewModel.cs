using System.Windows.Media;
using MarketAnalyser.Core.Market;

namespace MarketAnalyser.App.ViewModels;

public sealed record ChartSeriesViewModel(
    string Name,
    IReadOnlyList<ChartPoint> Points,
    Brush Stroke);
