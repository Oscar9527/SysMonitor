using System.Collections.Immutable;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using SysMonitor.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace SysMonitor.UI;

public enum MetricHistorySeries
{
    Cpu,
    Gpu,
}

public sealed class HistorySparkline : FrameworkElement
{
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(2.5);

    private static readonly Brush DefaultStroke = CreateFrozenBrush(Color.FromRgb(75, 174, 244));
    private static readonly Brush DefaultFill = CreateFrozenBrush(Color.FromArgb(42, 75, 174, 244));
    private static readonly Brush DefaultGridLine = CreateFrozenBrush(Color.FromArgb(52, 128, 128, 128));

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series),
        typeof(ImmutableArray<MetricHistoryPoint>),
        typeof(HistorySparkline),
        new FrameworkPropertyMetadata(
            ImmutableArray<MetricHistoryPoint>.Empty,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric),
        typeof(MetricHistorySeries),
        typeof(HistorySparkline),
        new FrameworkPropertyMetadata(
            MetricHistorySeries.Cpu,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(HistorySparkline),
        new FrameworkPropertyMetadata(
            DefaultStroke,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(HistorySparkline),
        new FrameworkPropertyMetadata(
            DefaultFill,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridLineProperty = DependencyProperty.Register(
        nameof(GridLine),
        typeof(Brush),
        typeof(HistorySparkline),
        new FrameworkPropertyMetadata(
            DefaultGridLine,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public HistorySparkline()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public ImmutableArray<MetricHistoryPoint> Series
    {
        get
        {
            var value = (ImmutableArray<MetricHistoryPoint>)GetValue(SeriesProperty);
            return value.IsDefault ? ImmutableArray<MetricHistoryPoint>.Empty : value;
        }
        set => SetValue(SeriesProperty, value.IsDefault ? ImmutableArray<MetricHistoryPoint>.Empty : value);
    }

    public MetricHistorySeries Metric
    {
        get => (MetricHistorySeries)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush? GridLine
    {
        get => (Brush?)GetValue(GridLineProperty);
        set => SetValue(GridLineProperty, value);
    }

    public void UpdateSeries(ImmutableArray<MetricHistoryPoint> series)
    {
        VerifyAccess();
        series = series.IsDefault ? ImmutableArray<MetricHistoryPoint>.Empty : series;
        if (Series == series)
        {
            return;
        }

        SetCurrentValue(SeriesProperty, series);
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        DrawGrid(drawingContext, width, height, dpi);

        HistorySparklineGeometry geometry = HistorySparklineGeometry.Build(
            Series,
            Metric,
            width,
            height,
            Stopwatch.Frequency,
            HistoryWindow,
            GapThreshold);
        if (geometry.Segments.IsDefaultOrEmpty)
        {
            return;
        }

        Pen? strokePen = CreateFrozenPen(Stroke, 1 / dpi.DpiScaleY);
        foreach (ImmutableArray<Point> segment in geometry.Segments)
        {
            if (segment.Length >= 2)
            {
                DrawSegmentFill(drawingContext, segment, height, Fill);
                DrawSegmentLine(drawingContext, segment, strokePen);
            }
        }

        if (geometry.LatestValidPoint is Point latest && Stroke is Brush markerBrush)
        {
            double radius = Math.Max(2, 2 / Math.Min(dpi.DpiScaleX, dpi.DpiScaleY));
            drawingContext.DrawEllipse(markerBrush, null, latest, radius, radius);
        }
    }

    private void DrawGrid(DrawingContext drawingContext, double width, double height, DpiScale dpi)
    {
        double thickness = 1 / dpi.DpiScaleY;
        Pen? pen = CreateFrozenPen(GridLine, thickness);
        if (pen is null)
        {
            return;
        }

        for (int index = 1; index <= 3; index++)
        {
            double y = AlignOnePixelLine(height * index / 4, dpi.DpiScaleY);
            drawingContext.DrawLine(pen, new Point(0, y), new Point(width, y));
        }
    }

    private static void DrawSegmentFill(
        DrawingContext drawingContext,
        ImmutableArray<Point> segment,
        double baseline,
        Brush? fill)
    {
        if (fill is null)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(segment[0].X, baseline), true, true);
            context.LineTo(segment[0], true, false);
            for (int index = 1; index < segment.Length; index++)
            {
                context.LineTo(segment[index], true, false);
            }

            context.LineTo(new Point(segment[^1].X, baseline), true, false);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(fill, null, geometry);
    }

    private static void DrawSegmentLine(
        DrawingContext drawingContext,
        ImmutableArray<Point> segment,
        Pen? pen)
    {
        if (pen is null)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(segment[0], false, false);
            for (int index = 1; index < segment.Length; index++)
            {
                context.LineTo(segment[index], true, false);
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private static Pen? CreateFrozenPen(Brush? brush, double thickness)
    {
        if (brush is null)
        {
            return null;
        }

        Brush penBrush = brush.IsFrozen ? brush : brush.CloneCurrentValue();
        var pen = new Pen(penBrush, thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static double AlignOnePixelLine(double value, double dpiScale) =>
        (Math.Round(value * dpiScale) + 0.5) / dpiScale;
}
