using System.Windows.Media;

namespace MarketAnalyser.App.ViewModels;

public sealed record LiveScanHitViewModel(
    string Symbol,
    string SignalLabel,
    Brush SignalForeground);
