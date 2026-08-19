using System.Text.Json;
using SysMonitor.Models;
using SysMonitor.Services;
using SysMonitor.UI;

namespace SysMonitor.Tests;

public sealed class GameOverlayNativeTests
{
    [Fact]
    public void ApplyNoActivateStyles_AddsRequiredStylesAndRemovesAppWindow()
    {
        long unrelated = 0x1000;
        long result = GameOverlayWindow.ApplyNoActivateStyles(
            unrelated | GameOverlayNativeStyles.AppWindow);

        Assert.NotEqual(0, result & GameOverlayNativeStyles.ToolWindow);
        Assert.NotEqual(0, result & GameOverlayNativeStyles.NoActivate);
        Assert.NotEqual(0, result & GameOverlayNativeStyles.Transparent);
        Assert.Equal(0, result & GameOverlayNativeStyles.AppWindow);
        Assert.NotEqual(0, result & unrelated);
    }

    [Fact]
    public void ZOrder_SynchronizesWithTargetWindowTierAndOwnedRelationship()
    {
        OverlayZOrderDecision decisionNoTarget = GameOverlayWindow.ResolveZOrder(
            new nint(10), nint.Zero, nint.Zero, targetTopmost: false);
        Assert.True(decisionNoTarget.Topmost);
        Assert.Equal(new nint(-1), decisionNoTarget.InsertAfter);
        Assert.False(decisionNoTarget.PreserveZOrder);

        OverlayZOrderDecision decisionWithTarget = GameOverlayWindow.ResolveZOrder(
            new nint(10), new nint(20), new nint(30), targetTopmost: false);
        Assert.False(decisionWithTarget.Topmost);
        Assert.Equal(new nint(-2), decisionWithTarget.InsertAfter);
        Assert.True(decisionWithTarget.PreserveZOrder);

        OverlayZOrderDecision decisionTopmostTarget = GameOverlayWindow.ResolveZOrder(
            new nint(10), new nint(20), nint.Zero, targetTopmost: true);
        Assert.True(decisionTopmostTarget.Topmost);
        Assert.Equal(new nint(-1), decisionTopmostTarget.InsertAfter);
        Assert.True(decisionTopmostTarget.PreserveZOrder);
    }

    [Fact]
    public void Placement_UsesTargetMonitorDpiAndSupportsNegativeCoordinates()
    {
        var work = new OverlayPixelRect(-2560, -120, 0, 1320);

        OverlayPixelRect result = GameOverlayWindow.CalculatePlacement(
            work, widthDip: 304, heightDip: 176, dpi: 144, marginDip: 14);

        Assert.Equal(456, result.Width);
        Assert.Equal(264, result.Height);
        Assert.Equal(-21, result.Right);
        Assert.Equal(-99, result.Top);
        Assert.True(result.Left >= work.Left);
        Assert.True(result.Bottom <= work.Bottom);
    }

    [Fact]
    public void Placement_ClampsOversizedOverlayToWorkingArea()
    {
        var work = new OverlayPixelRect(100, 200, 300, 300);
        OverlayPixelRect result = GameOverlayWindow.CalculatePlacement(work, 1000, 1000, 192);
        Assert.Equal(work, result);
    }

    [Fact]
    public void Placement_UsesWindowClientAreaAsItsCoordinateSpace()
    {
        OverlayPixelRect gameClientArea = new(420, 280, 1420, 880);

        OverlayPixelRect result = GameOverlayWindow.CalculatePlacement(
            gameClientArea, widthDip: 300, heightDip: 70, dpi: 96,
            marginDip: 4, horizontalPositionPercent: 0);

        Assert.Equal(424, result.Left);
        Assert.Equal(284, result.Top);
        Assert.True(result.Right <= gameClientArea.Right);
        Assert.True(result.Bottom <= gameClientArea.Bottom);
    }

    [Theory]
    [InlineData(0, 104)]
    [InlineData(50, 650)]
    [InlineData(100, 1196)]
    public void PlacementSupportsConfiguredTopHorizontalPosition(double position, int expectedLeft)
    {
        OverlayPixelRect result = GameOverlayWindow.CalculatePlacement(
            new OverlayPixelRect(100, 200, 2000, 1000),
            widthDip: 800, heightDip: 40, dpi: 96, marginDip: 4,
            horizontalPositionPercent: position);

        Assert.Equal(expectedLeft, result.Left);
        Assert.Equal(204, result.Top);
    }

    [Theory]
    [InlineData(96, 300, 100)]
    [InlineData(144, 450, 150)]
    [InlineData(192, 600, 200)]
    public void ExactPlacementUsesPhysicalCoordinatesAndScalesSizeOnce(uint dpi, int expectedWidth, int expectedHeight)
    {
        var screen = new OverlayPixelRect(-2560, 0, 0, 1440);

        OverlayPixelRect result = GameOverlayWindow.CalculateExactPlacement(
            screen, 300, 100, dpi, requestedX: -2500, requestedY: 100);

        Assert.Equal(-2500, result.Left);
        Assert.Equal(100, result.Top);
        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
    }

    [Fact]
    public void ExactPlacementClampsRightBottomAndOversizedHud()
    {
        var screen = new OverlayPixelRect(1920, -200, 2920, 400);
        OverlayPixelRect clamped = GameOverlayWindow.CalculateExactPlacement(
            screen, 300, 100, 144, 2900, 390);
        OverlayPixelRect oversized = GameOverlayWindow.CalculateExactPlacement(
            screen, 5000, 5000, 192, 9999, 9999);

        Assert.Equal(2470, clamped.Left);
        Assert.Equal(250, clamped.Top);
        Assert.Equal(screen, oversized);
    }

    [Fact]
    public void ExactPositionMatchesStableMonitorAndRejectsDuplicates()
    {
        OverlayMonitorIdentity identity = OverlayMonitorIdentity.CreateStable(
            "monitor-path", @"\\.\DISPLAY1", "Display", new ScreenPixelBounds(0, 0, 1920, 1080));
        var first = new GameOverlayMonitorPositionSettings
        {
            StableMonitorId = identity.StableMonitorId,
            GdiDeviceName = identity.GdiDeviceName,
            Left = 0, Top = 0, Right = 1920, Bottom = 1080, X = 20, Y = 30
        };

        Assert.True(GameOverlayWindow.TryFindExactPosition(identity, [first], out var match));
        Assert.Equal(20, match!.X);
        Assert.False(GameOverlayWindow.TryFindExactPosition(identity, [first, first], out _));
    }

    [Fact]
    public void PreviewPositionClonesMapPreservesOtherMonitorsAndNeverMutatesBaseline()
    {
        OverlayMonitorIdentity selected = OverlayMonitorIdentity.CreateStable(
            "monitor-a", @"\\.\DISPLAY1", "A", new ScreenPixelBounds(0, 0, 1920, 1080));
        var selectedPosition = new GameOverlayMonitorPositionSettings
        {
            StableMonitorId = selected.StableMonitorId,
            GdiDeviceName = selected.GdiDeviceName,
            Left = 0, Top = 0, Right = 1920, Bottom = 1080, X = 10, Y = 20
        };
        var otherPosition = new GameOverlayMonitorPositionSettings
        {
            StableMonitorId = "MONITOR-B",
            GdiDeviceName = @"\\.\DISPLAY2",
            Left = 1920, Top = 0, Right = 3840, Bottom = 1080, X = 2000, Y = 30
        };
        GameOverlayMonitorPositionSettings[] baseline = [selectedPosition, otherPosition];
        string before = JsonSerializer.Serialize(baseline);

        IReadOnlyList<GameOverlayMonitorPositionSettings> preview =
            GameOverlayWindow.BuildPreviewMonitorPositions(baseline, selected, true, 300, 400);
        IReadOnlyList<GameOverlayMonitorPositionSettings> reset =
            GameOverlayWindow.BuildPreviewMonitorPositions(baseline, selected, false, 0, 0);

        Assert.Equal(before, JsonSerializer.Serialize(baseline));
        Assert.Equal(2, preview.Count);
        Assert.Contains(preview, item => item.StableMonitorId == "MONITOR-B" && item.X == 2000);
        Assert.Contains(preview, item => item.StableMonitorId == selected.StableMonitorId && item.X == 300 && item.Y == 400);
        Assert.Single(reset);
        Assert.Equal("MONITOR-B", reset[0].StableMonitorId);
        Assert.NotSame(otherPosition, preview.Single(item => item.StableMonitorId == "MONITOR-B"));
    }

    [Fact]
    public void CoordinateContextDetectsTargetMovingToAnotherMonitor()
    {
        var original = new OverlaySettingsCoordinateContext("A", "One", 0, 0, 1920, 1080, 20, 20, true);
        var same = original with { CurrentX = 300, CurrentY = 200 };
        var moved = new OverlaySettingsCoordinateContext("B", "Two", 1920, 0, 3840, 1080, 2000, 20, false);

        Assert.True(GameOverlayWindow.CoordinateContextMatches(original, same));
        Assert.False(GameOverlayWindow.CoordinateContextMatches(original, moved));
    }

    [Theory]
    [InlineData("42%", "71°C", "42%  71°C")]
    [InlineData("--", "", "--  --")]
    public void HorizontalMetricContainsOnlyUsageAndTemperature(string usage, string temperature, string expected)
    {
        Assert.Equal(expected, GameOverlayWindow.BuildHorizontalMetricValue(usage, temperature));
    }

    [Fact]
    public void HudUsesShortLocalizedEtwDiagnostic()
    {
        string state = GameOverlayWindow.GetCompactFrameState(new GameOverlayFrameSnapshot(
            null, GameOverlayFrameStatus.Faulted, DateTimeOffset.UtcNow,
            Detail: "The PresentMon ETW session name is already in use."));

        string text = GameOverlayWindow.BuildOverlayText("--", state, "97%", "90°C", "3%", "55°C", "48%");

        Assert.Equal("帧率采集被占用", state);
        Assert.DoesNotContain("PresentMon could not allocate", text);
        Assert.Contains("CPU 97%", text);
    }

    [Fact]
    public void HudUsesShortLocalizedEtwResourceDiagnostic()
    {
        string state = GameOverlayWindow.GetCompactFrameState(new GameOverlayFrameSnapshot(
            null, GameOverlayFrameStatus.Faulted, DateTimeOffset.UtcNow,
            Detail: "Windows ETW resources are unavailable (1450)."));

        Assert.Equal("ETW \u8D44\u6E90\u4E0D\u8DB3", state);
    }

    [Theory]
    [InlineData(GameOverlayFrameStatus.Unavailable, "未启用")]
    [InlineData(GameOverlayFrameStatus.WaitingForTarget, "未选择目标")]
    [InlineData(GameOverlayFrameStatus.Starting, "正在采集")]
    [InlineData(GameOverlayFrameStatus.NoFrames, "")]
    [InlineData(GameOverlayFrameStatus.Faulted, "采集失败")]
    public void HudDoesNotExposeEnglishUnavailableState(GameOverlayFrameStatus status, string expected)
    {
        Assert.Equal(expected, GameOverlayWindow.GetCompactFrameState(
            new GameOverlayFrameSnapshot(null, status, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void HudSilentlyShowsPlaceholderWhenNoFramesAreAvailable()
    {
        var frame = new GameOverlayFrameSnapshot(
            null,
            GameOverlayFrameStatus.NoFrames,
            DateTimeOffset.UtcNow);

        string state = GameOverlayWindow.GetCompactFrameState(frame);
        string text = GameOverlayWindow.BuildOverlayText(
            "--",
            state,
            "40%",
            "72°C",
            "55%",
            "61°C",
            "48%");

        Assert.Equal(string.Empty, state);
        Assert.Contains("FPS --", text);
        Assert.DoesNotContain("未捕获到帧", text);
    }

    [Fact]
    public void HudKeepsFrameRateRowWhenNoFramesAreTemporarilyAvailable()
    {
        var frame = new GameOverlayFrameSnapshot(
            null,
            GameOverlayFrameStatus.NoFrames,
            DateTimeOffset.UtcNow);

        Assert.True(GameOverlayWindow.ShouldShowFrameRate(frame, configured: true));
        Assert.False(GameOverlayWindow.HasUsableFrameRate(frame));
    }

    [Fact]
    public void HudShowsFrameRateRowOnlyForFiniteActiveValue()
    {
        var frame = new GameOverlayFrameSnapshot(
            143.8,
            GameOverlayFrameStatus.Active,
            DateTimeOffset.UtcNow);

        Assert.True(GameOverlayWindow.ShouldShowFrameRate(frame, configured: true));
        Assert.False(GameOverlayWindow.ShouldShowFrameRate(frame, configured: false));
        Assert.True(GameOverlayWindow.HasUsableFrameRate(frame));
    }

    [Fact]
    public void RivatunerLayoutUsesRowsAndOnlySelectedMetrics()
    {
        string text = GameOverlayWindow.BuildOverlayText(
            "144", "", "40%", "72°C", "55%", "61°C", "48%", "1.2M", "90K",
            "rivatuner", new GameOverlayMetricVisibility(true, true, false, false, true));

        Assert.Equal(
            "CPU 40%  72°C" + Environment.NewLine +
            "FPS 144" + Environment.NewLine +
            "NET ↓ 1.2M  ↑ 90K",
            text);
    }

    [Fact]
    public void DetailedLayoutIncludesConfiguredMemoryFrequency()
    {
        string text = GameOverlayWindow.BuildOverlayText(
            "144", "", "40%", "72°C", "55%", "61°C", "48%",
            preset: "detailed",
            metrics: new GameOverlayMetricVisibility(false, false, false, true, false),
            memoryFrequency: "3200 MHz");

        Assert.Equal("RAM 48%  3200 MHz", text);
    }

    [Fact]
    public void HorizontalLayoutIncludesEveryEnabledMetricIncludingMemory()
    {
        var metrics = new GameOverlayMetricVisibility(
            FrameRate: true,
            Cpu: true,
            Gpu: false,
            Memory: true,
            Network: true)
        {
            Order = ["memory", "cpu", "fps", "network", "gpu"]
        };

        IReadOnlyList<string> visible = GameOverlayWindow.BuildVisibleMetricOrder(
            metrics,
            frameRateVisible: true);

        Assert.Equal(["memory", "cpu", "fps", "network"], visible);
    }

    [Fact]
    public void HorizontalLayoutRemovesOnlyUnavailableFrameRate()
    {
        var metrics = new GameOverlayMetricVisibility(
            FrameRate: true,
            Cpu: false,
            Gpu: false,
            Memory: true,
            Network: false);

        IReadOnlyList<string> visible = GameOverlayWindow.BuildVisibleMetricOrder(
            metrics,
            frameRateVisible: false);

        Assert.Equal(["memory"], visible);
    }

    [Fact]
    public void MetricOrder_DefaultPlacesCpuGpuMemoryFpsInOrder()
    {
        Assert.Equal(["cpu", "gpu", "memory", "fps", "network"], GameOverlayMetricOrder.Default);

        var metrics = new GameOverlayMetricVisibility();
        IReadOnlyList<string> visible = GameOverlayWindow.BuildVisibleMetricOrder(metrics, frameRateVisible: true);
        Assert.Equal(["cpu", "gpu", "memory", "fps"], visible);
    }

    [Fact]
    public void MetricOrder_LegacyDefaultsAutoUpgradeToCpuGpuMemFps()
    {
        IReadOnlyList<string> upgraded1 = GameOverlayMetricOrder.Normalize(["gpu", "cpu", "fps", "memory", "network"]);
        Assert.Equal(["cpu", "gpu", "memory", "fps", "network"], upgraded1);

        IReadOnlyList<string> upgraded2 = GameOverlayMetricOrder.Normalize(["fps", "gpu", "cpu", "memory", "network"]);
        Assert.Equal(["cpu", "gpu", "memory", "fps", "network"], upgraded2);
    }
}
