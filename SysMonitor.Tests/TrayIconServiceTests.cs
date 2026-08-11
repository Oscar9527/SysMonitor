using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class TrayIconServiceTests
{
    [Theory]
    [InlineData(false, true, "TrayShowGameOverlay")]
    [InlineData(true, true, "TrayHideGameOverlay")]
    [InlineData(false, false, "TrayGameOverlayUnavailableCompatibility")]
    [InlineData(true, false, "TrayGameOverlayUnavailableCompatibility")]
    public void GameOverlayText_ReflectsVisibilityAndCompatibilityAvailability(
        bool visible,
        bool available,
        string expected)
    {
        Assert.Equal(expected, TrayIconService.GetGameOverlayResourceKey(visible, available));
    }
}
