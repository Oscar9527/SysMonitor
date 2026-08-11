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
    public void Placement_UsesTargetMonitorDpiAndSupportsNegativeCoordinates()
    {
        var work = new OverlayPixelRect(-2560, -120, 0, 1320);

        OverlayPixelRect result = GameOverlayWindow.CalculatePlacement(
            work,
            widthDip: 304,
            heightDip: 176,
            dpi: 144,
            marginDip: 14);

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

        OverlayPixelRect result = GameOverlayWindow.CalculatePlacement(
            work,
            1000,
            1000,
            192);

        Assert.Equal(work, result);
    }
}
