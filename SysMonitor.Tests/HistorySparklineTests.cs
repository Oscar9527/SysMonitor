using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SysMonitor.Models;
using SysMonitor.UI;

namespace SysMonitor.Tests;

public sealed class HistorySparklineTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(2.5);

    [Fact]
    public void EmptyAndZeroSizedInputsProduceNoGeometry()
    {
        HistorySparklineGeometry empty = Build(ImmutableArray<MetricHistoryPoint>.Empty);
        HistorySparklineGeometry zeroWidth = Build(Series(Point(1, 10, 50, 50)), width: 0);
        HistorySparklineGeometry zeroHeight = Build(Series(Point(1, 10, 50, 50)), height: 0);
        HistorySparklineGeometry nonFinite = Build(Series(Point(1, 10, 50, 50)), width: double.NaN);

        Assert.Empty(empty.Segments);
        Assert.Null(empty.LatestValidPoint);
        Assert.Empty(zeroWidth.Segments);
        Assert.Empty(zeroHeight.Segments);
        Assert.Empty(nonFinite.Segments);
    }

    [Fact]
    public void SinglePointUsesExactSixtySecondDomainAndFixedPercentScale()
    {
        HistorySparklineGeometry geometry = Build(Series(Point(1, 100, 25, null)));

        ImmutableArray<System.Windows.Point> segment = Assert.Single(geometry.Segments);
        System.Windows.Point point = Assert.Single(segment);
        Assert.Equal(120, point.X, 8);
        Assert.Equal(60, point.Y, 8);
        Assert.Equal(point, geometry.LatestValidPoint);
    }

    [Fact]
    public void NullAndLargeGapCreateIndependentSegments()
    {
        ImmutableArray<MetricHistoryPoint> samples = Series(
            Point(1, 100, 10, 10),
            Point(2, 101, 20, 20),
            Point(3, 102, null, null),
            Point(4, 103, 30, 30),
            Point(5, 106, 40, 40),
            Point(6, 108, 50, 50));

        HistorySparklineGeometry geometry = Build(samples);

        Assert.Equal(new[] { 2, 1, 2 }, geometry.Segments.Select(segment => segment.Length));
    }

    [Fact]
    public void GapAtThresholdRemainsConnected()
    {
        HistorySparklineGeometry geometry = Build(
            Series(Point(1, 100, 10, 10), Point(2, 125, 20, 20)),
            frequency: 10);

        Assert.Single(geometry.Segments);
        Assert.Equal(2, geometry.Segments[0].Length);
    }

    [Fact]
    public void GpuZeroIsAValidBaselinePointWhileNullBreaksTheLine()
    {
        HistorySparklineGeometry geometry = Build(
            Series(
                Point(1, 10, 70, 0),
                Point(2, 11, 70, null),
                Point(3, 12, 70, 100)),
            metric: MetricHistorySeries.Gpu,
            height: 80);

        Assert.Equal(2, geometry.Segments.Length);
        Assert.Equal(80, geometry.Segments[0][0].Y, 8);
        Assert.Equal(0, geometry.Segments[1][0].Y, 8);
    }

    [Fact]
    public void NonFiniteValuesAreSkippedAndFiniteOutOfRangeValuesClamp()
    {
        HistorySparklineGeometry geometry = Build(
            Series(
                Point(1, 10, double.NaN, 0),
                Point(2, 11, -20, 0),
                Point(3, 12, double.PositiveInfinity, 0),
                Point(4, 13, 120, 0)));

        Assert.Equal(2, geometry.Segments.Length);
        Assert.Equal(80, geometry.Segments[0][0].Y, 8);
        Assert.Equal(0, geometry.Segments[1][0].Y, 8);
    }

    [Fact]
    public void SamplesOlderThanWindowAreExcluded()
    {
        HistorySparklineGeometry geometry = Build(
            Series(Point(1, 1, 10, 10), Point(2, 40, 20, 20), Point(3, 100, 30, 30)));

        Assert.Equal(2, geometry.Segments.Length);
        Assert.Single(geometry.Segments[0]);
        Assert.Single(geometry.Segments[1]);
        Assert.Equal(0, geometry.Segments[0][0].X, 8);
        Assert.Equal(120, geometry.Segments[1][0].X, 8);
    }

    [Fact]
    public void BrushPropertiesInvalidateRenderingAndUpdateSeriesKeepsImmutableSnapshot()
    {
        RunSta(() =>
        {
            Assert.True(
                ((FrameworkPropertyMetadata)HistorySparkline.StrokeProperty.GetMetadata(typeof(HistorySparkline)))
                .AffectsRender);
            Assert.True(
                ((FrameworkPropertyMetadata)HistorySparkline.FillProperty.GetMetadata(typeof(HistorySparkline)))
                .AffectsRender);
            Assert.True(
                ((FrameworkPropertyMetadata)HistorySparkline.GridLineProperty.GetMetadata(typeof(HistorySparkline)))
                .AffectsRender);

            var control = new HistorySparkline();
            ImmutableArray<MetricHistoryPoint> series = Series(Point(1, 1, 10, 20));
            control.UpdateSeries(series);

            Assert.Equal(series, control.Series);
            control.UpdateSeries(default);
            Assert.False(control.Series.IsDefault);
            Assert.Empty(control.Series);
        });
    }

    [Fact]
    public void UpdateSeriesRejectsCallsFromNonOwnerThread()
    {
        RunSta(() =>
        {
            var control = new HistorySparkline();
            Exception? observed = null;
            var thread = new Thread(() =>
            {
                try
                {
                    control.UpdateSeries(Series(Point(1, 1, 10, 20)));
                }
                catch (Exception exception)
                {
                    observed = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            thread.Join();

            Assert.IsType<InvalidOperationException>(observed);
        });
    }

    [Fact]
    public void CreatesAutomationPeerForAssistiveTechnology()
    {
        RunSta(() =>
        {
            var control = new HistorySparkline();
            var peer = (AutomationPeer?)typeof(HistorySparkline)
                .GetMethod(
                    "OnCreateAutomationPeer",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .Invoke(control, null);

            Assert.IsType<FrameworkElementAutomationPeer>(peer);
        });
    }

    [Fact]
    public void StaRenderSmokeHandlesSegmentsNullsAndSinglePoints()
    {
        RunSta(() =>
        {
            var control = new HistorySparkline
            {
                Width = 240,
                Height = 100,
                Metric = MetricHistorySeries.Gpu,
                Stroke = Brushes.LimeGreen,
                Fill = new SolidColorBrush(Color.FromArgb(30, 50, 205, 50)),
                GridLine = Brushes.Gray,
            };
            control.UpdateSeries(Series(
                Point(1, 100, 10, 0),
                Point(2, 101, 20, 50),
                Point(3, 102, 30, null),
                Point(4, 106, 40, 100)));
            control.Measure(new Size(240, 100));
            control.Arrange(new Rect(0, 0, 240, 100));

            var bitmap = new RenderTargetBitmap(240, 100, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(control);

            Assert.Equal(240, bitmap.PixelWidth);
            Assert.Equal(100, bitmap.PixelHeight);
        });
    }

    private static HistorySparklineGeometry Build(
        ImmutableArray<MetricHistoryPoint> samples,
        MetricHistorySeries metric = MetricHistorySeries.Cpu,
        double width = 120,
        double height = 80,
        long frequency = 1) =>
        HistorySparklineGeometry.Build(samples, metric, width, height, frequency, Window, Gap);

    private static ImmutableArray<MetricHistoryPoint> Series(params MetricHistoryPoint[] points) =>
        ImmutableArray.Create(points);

    private static MetricHistoryPoint Point(
        long sequence,
        long timestamp,
        double? cpu,
        double? gpu) =>
        new(1, sequence, timestamp, cpu, gpu);

    private static void RunSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
