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
    public void EmbeddedStyles_ConvertToChildAndRestorePopup()
    {
        const long child = 0x40000000L;
        const long popup = unchecked((long)0x80000000);
        long embedded = GameOverlayNativeStyles.ApplyEmbeddedChild(popup | 0x1000);

        Assert.NotEqual(0, embedded & child);
        Assert.Equal(0, embedded & popup);
        Assert.NotEqual(0, embedded & 0x1000);

        long restored = GameOverlayNativeStyles.RestoreTopLevel(embedded);
        Assert.Equal(0, restored & child);
        Assert.NotEqual(0, restored & popup);
        Assert.NotEqual(0, restored & 0x1000);
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
    [InlineData(GameOverlayFrameStatus.NoFrames, "未捕获到帧")]
    [InlineData(GameOverlayFrameStatus.Faulted, "采集失败")]
    public void HudDoesNotExposeEnglishUnavailableState(GameOverlayFrameStatus status, string expected)
    {
        Assert.Equal(expected, GameOverlayWindow.GetCompactFrameState(
            new GameOverlayFrameSnapshot(null, status, DateTimeOffset.UtcNow)));
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
