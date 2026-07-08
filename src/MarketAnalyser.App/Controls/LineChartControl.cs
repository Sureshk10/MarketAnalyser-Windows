using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MarketAnalyser.App.ViewModels;
using MarketAnalyser.Core.Market;

namespace MarketAnalyser.App.Controls;

public sealed class LineChartControl : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series),
        typeof(IEnumerable<ChartSeriesViewModel>),
        typeof(LineChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(12, 17, 24));
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(36, 49, 65));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(139, 156, 176));
    private static readonly Pen GridPen = new(GridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) };
    private static readonly Typeface Typeface = new("Segoe UI");

    public IEnumerable<ChartSeriesViewModel>? Series
    {
        get => (IEnumerable<ChartSeriesViewModel>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(BackgroundBrush, null, bounds);

        if (bounds.Width < 80 || bounds.Height < 60)
        {
            return;
        }

        var series = Series?
            .Where(item => item.Points.Count > 0)
            .Select(item => item with { Points = item.Points.TakeLast(90).ToArray() })
            .ToArray() ?? [];

        if (series.Length == 0)
        {
            DrawText(drawingContext, "Waiting for chart data", 12, MutedBrush, new Point(16, bounds.Height / 2 - 8));
            return;
        }

        var plot = new Rect(56, 24, Math.Max(20, bounds.Width - 72), Math.Max(20, bounds.Height - 56));
        var allValues = series.SelectMany(item => item.Points).Select(point => point.Value).ToArray();
        var min = allValues.Min();
        var max = allValues.Max();
        var span = max - min;
        if (span == 0)
        {
            span = Math.Max(Math.Abs(max), 1);
            min -= span / 2;
            max += span / 2;
        }
        else
        {
            var padding = span * 0.08m;
            min -= padding;
            max += padding;
            span = max - min;
        }

        DrawGrid(drawingContext, plot, min, max);
        DrawLegend(drawingContext, series, plot);

        foreach (var item in series)
        {
            DrawSeries(drawingContext, plot, item, min, span);
        }

        var timeline = series.OrderByDescending(item => item.Points.Count).First().Points;
        DrawTimeLabels(drawingContext, timeline, plot);
    }

    private static void DrawGrid(DrawingContext drawingContext, Rect plot, decimal min, decimal max)
    {
        const int lines = 4;
        for (var i = 0; i <= lines; i++)
        {
            var y = plot.Bottom - (plot.Height * i / lines);
            drawingContext.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            var value = min + ((max - min) * i / lines);
            DrawText(drawingContext, FormatValue(value), 11, MutedBrush, new Point(6, y - 8));
        }
    }

    private static void DrawLegend(DrawingContext drawingContext, IReadOnlyList<ChartSeriesViewModel> series, Rect plot)
    {
        var x = plot.Left;
        foreach (var item in series.Take(3))
        {
            var pen = new Pen(item.Stroke, 2);
            drawingContext.DrawLine(pen, new Point(x, 11), new Point(x + 16, 11));
            DrawText(drawingContext, item.Name, 11, MutedBrush, new Point(x + 21, 3));
            x += Math.Min(130, item.Name.Length * 7 + 45);
        }
    }

    private static void DrawSeries(
        DrawingContext drawingContext,
        Rect plot,
        ChartSeriesViewModel series,
        decimal min,
        decimal span)
    {
        var points = series.Points;
        if (points.Count == 0)
        {
            return;
        }

        if (points.Count == 1)
        {
            var y = ToY(points[0].Value, min, span, plot);
            drawingContext.DrawEllipse(series.Stroke, null, new Point(plot.Left, y), 2.5, 2.5);
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < points.Count; i++)
            {
                var x = plot.Left + (plot.Width * i / (points.Count - 1));
                var y = ToY(points[i].Value, min, span, plot);
                if (i == 0)
                {
                    context.BeginFigure(new Point(x, y), false, false);
                }
                else
                {
                    context.LineTo(new Point(x, y), true, false);
                }
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(series.Stroke, 2.2), geometry);
    }

    private static void DrawTimeLabels(DrawingContext drawingContext, IReadOnlyList<ChartPoint> points, Rect plot)
    {
        if (points.Count == 0)
        {
            return;
        }

        var first = points.First().Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        var last = points.Last().Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        DrawText(drawingContext, first, 10, MutedBrush, new Point(plot.Left, plot.Bottom + 8));

        var lastText = CreateText(last, 10, MutedBrush);
        drawingContext.DrawText(lastText, new Point(plot.Right - lastText.Width, plot.Bottom + 8));
    }

    private static double ToY(decimal value, decimal min, decimal span, Rect plot)
    {
        var ratio = (double)((value - min) / span);
        return plot.Bottom - (plot.Height * ratio);
    }

    private static string FormatValue(decimal value)
    {
        var absolute = Math.Abs(value);
        if (absolute >= 10_000_000)
        {
            return $"{value / 10_000_000m:N1}Cr";
        }

        if (absolute >= 100_000)
        {
            return $"{value / 100_000m:N1}L";
        }

        return absolute >= 1000 ? value.ToString("N0", CultureInfo.CurrentCulture) : value.ToString("N2", CultureInfo.CurrentCulture);
    }

    private static void DrawText(DrawingContext drawingContext, string text, double size, Brush brush, Point point)
    {
        drawingContext.DrawText(CreateText(text, size, brush), point);
    }

    private static FormattedText CreateText(string text, double size, Brush brush)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface,
            size,
            brush,
            1);
    }
}
