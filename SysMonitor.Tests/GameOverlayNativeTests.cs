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
    public void ZOrder_NoTargetDemotesOverlayFromTopmostTier()
    {
        OverlayZOrderDecision decision = GameOverlayWindow.ResolveZOrder(
            new nint(10), nint.Zero, nint.Zero, targetTopmost: false);

        Assert.False(decision.Topmost);
        Assert.Equal(new nint(-2), decision.InsertAfter);
        Assert.False(decision.PreserveZOrder);
    }

    [Fact]
    public void ZOrder_AlreadyImmediatelyAboveTargetPreservesOrder()
    {
        var overlay = new nint(10);
        OverlayZOrderDecision decision = GameOverlayWindow.ResolveZOrder(
            overlay, new nint(20), overlay, targetTopmost: false);

        Assert.False(decision.Topmost);
        Assert.True(decision.PreserveZOrder);
    }

    [Fact]
    public void ZOrder_UsesExistingTargetPredecessor()
    {
        OverlayZOrderDecision decision = GameOverlayWindow.ResolveZOrder(
            new nint(10), new nint(20), new nint(30), targetTopmost: false);

        Assert.Equal(new nint(30), decision.InsertAfter);
        Assert.False(decision.PreserveZOrder);
    }

    [Fact]
    public void ZOrder_TopmostTargetWithoutPredecessorUsesTopmostTier()
    {
        OverlayZOrderDecision decision = GameOverlayWindow.ResolveZOrder(
            new nint(10), new nint(20), nint.Zero, targetTopmost: true);

        Assert.True(decision.Topmost);
        Assert.Equal(new nint(-1), decision.InsertAfter);
        Assert.False(decision.PreserveZOrder);
    }

    [Fact]
    public void ZOrder_NonTopmostTargetWithoutPredecessorUsesNormalTop()
    {
        OverlayZOrderDecision decision = GameOverlayWindow.ResolveZOrder(
            new nint(10), new nint(20), nint.Zero, targetTopmost: false);

        Assert.False(decision.Topmost);
        Assert.Equal(nint.Zero, decision.InsertAfter);
        Assert.False(decision.PreserveZOrder);
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
    public void HudHidesFrameRateRowWhenNoFramesAreAvailable()
    {
        var frame = new GameOverlayFrameSnapshot(
            null,
            GameOverlayFrameStatus.NoFrames,
            DateTimeOffset.UtcNow);

        Assert.False(GameOverlayWindow.ShouldShowFrameRate(frame, configured: true));
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
}
