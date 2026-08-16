using SysMonitor.UI;

namespace SysMonitor.Tests;

public sealed class GameOverlaySettingsLogicTests
{
    [Fact]
    public void UnrelatedApplyDoesNotCreateExactPosition()
    {
        bool valid = GameOverlaySettingsWindow.TryResolvePositionRequest(
            hasContext: true,
            dirty: false,
            exactChecked: true,
            "100",
            "200",
            out GameOverlayPositionChange change,
            out int? x,
            out int? y);

        Assert.True(valid);
        Assert.Equal(GameOverlayPositionChange.None, change);
        Assert.Null(x);
        Assert.Null(y);
    }

    [Fact]
    public void ExplicitCoordinatesProduceSetRequest()
    {
        bool valid = GameOverlaySettingsWindow.TryResolvePositionRequest(
            true, true, true, "-1920", "48",
            out GameOverlayPositionChange change, out int? x, out int? y);

        Assert.True(valid);
        Assert.Equal(GameOverlayPositionChange.Set, change);
        Assert.Equal(-1920, x);
        Assert.Equal(48, y);
    }

    [Fact]
    public void ResetAndInvalidTextAreDistinct()
    {
        Assert.True(GameOverlaySettingsWindow.TryResolvePositionRequest(
            true, true, false, "bad", "bad",
            out GameOverlayPositionChange reset, out _, out _));
        Assert.Equal(GameOverlayPositionChange.Reset, reset);

        Assert.False(GameOverlaySettingsWindow.TryResolvePositionRequest(
            true, true, true, "1.5", "20",
            out _, out _, out _));
    }

    [Fact]
    public void PreviewSessionFinalizationIsIdempotent()
    {
        bool finalized = false;
        bool active = true;

        Assert.True(GameOverlaySettingsWindow.TryFinalizePreviewSessionState(ref finalized, ref active));
        Assert.True(finalized);
        Assert.False(active);
        Assert.False(GameOverlaySettingsWindow.TryFinalizePreviewSessionState(ref finalized, ref active));
    }
}
