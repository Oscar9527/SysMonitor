using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class GameOverlayWindowTrackerTests
{
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void LocationChange_RequiresTopLevelTargetWindowObject()
    {
        nint target = new(42);
        nint overlay = new(99);

        Assert.True(GameOverlayWindowTracker.IsRelevantWinEvent(
            GameOverlayWindowTracker.EventObjectLocationChange,
            target,
            GameOverlayWindowTracker.ObjIdWindow,
            0,
            target,
            overlay));
        Assert.False(GameOverlayWindowTracker.IsRelevantWinEvent(
            GameOverlayWindowTracker.EventObjectLocationChange,
            target,
            GameOverlayWindowTracker.ObjIdWindow,
            1,
            target,
            overlay));
        Assert.False(GameOverlayWindowTracker.IsRelevantWinEvent(
            GameOverlayWindowTracker.EventObjectLocationChange,
            target,
            1,
            0,
            target,
            overlay));
    }

    [Fact]
    public void MoveAndMinimizeEvents_RequireExactTargetAndIgnoreOverlay()
    {
        nint target = new(42);
        nint overlay = new(99);

        Assert.True(GameOverlayWindowTracker.IsRelevantWinEvent(
            GameOverlayWindowTracker.EventSystemMoveSizeStart,
            target,
            0,
            0,
            target,
            overlay));
        Assert.False(GameOverlayWindowTracker.IsRelevantWinEvent(
            GameOverlayWindowTracker.EventSystemMoveSizeStart,
            new nint(43),
            0,
            0,
            target,
            overlay));
        Assert.False(GameOverlayWindowTracker.IsRelevantWinEvent(
            GameOverlayWindowTracker.EventSystemMinimizeStart,
            overlay,
            0,
            0,
            target,
            overlay));
    }

    [Fact]
    public void ForegroundEvents_AreRelevantForRevalidationButNoTargetIsNot()
    {
        Assert.True(GameOverlayWindowTracker.IsRelevantWinEvent(
            GameOverlayWindowTracker.EventSystemForeground,
            new nint(123),
            0,
            0,
            new nint(42),
            new nint(99)));
        Assert.False(GameOverlayWindowTracker.IsRelevantWinEvent(
            GameOverlayWindowTracker.EventSystemForeground,
            new nint(123),
            0,
            0,
            nint.Zero,
            new nint(99)));
    }

    [Fact]
    public void Coalescing_PreservesAllWorkKindsForOneRenderPass()
    {
        GameOverlayWindowTracker.TrackerWork current = GameOverlayWindowTracker.TrackerWork.MoveStart;
        GameOverlayWindowTracker.TrackerWork incoming =
            GameOverlayWindowTracker.TrackerWork.Refresh | GameOverlayWindowTracker.TrackerWork.MoveTick;

        GameOverlayWindowTracker.TrackerWork combined =
            GameOverlayWindowTracker.CoalesceWork(current, incoming);

        Assert.Equal(
            GameOverlayWindowTracker.TrackerWork.MoveStart |
            GameOverlayWindowTracker.TrackerWork.Refresh |
            GameOverlayWindowTracker.TrackerWork.MoveTick,
            combined);
    }

    [Fact]
    public void EventClassification_DistinguishesTemporaryMinimizeFromPermanentDestroy()
    {
        Assert.Equal(
            GameOverlayWindowTracker.TrackerWork.MinimizeStart,
            GameOverlayWindowTracker.ClassifyWinEvent(GameOverlayWindowTracker.EventSystemMinimizeStart));
        Assert.Equal(
            GameOverlayWindowTracker.TrackerWork.MinimizeEnd,
            GameOverlayWindowTracker.ClassifyWinEvent(GameOverlayWindowTracker.EventSystemMinimizeEnd));
        Assert.Equal(
            GameOverlayWindowTracker.TrackerWork.Invalidate,
            GameOverlayWindowTracker.ClassifyWinEvent(GameOverlayWindowTracker.EventObjectDestroy));
    }

    [Fact]
    public void Identity_MatchesHwndPidAndProcessGeneration()
    {
        var identity = new GameOverlayWindowTracker.GameOverlayTargetIdentity(new nint(42), 123, Started);

        Assert.True(identity.Matches(new nint(42), 123, Started));
        Assert.False(identity.Matches(new nint(43), 123, Started));
        Assert.False(identity.Matches(new nint(42), 124, Started));
        Assert.False(identity.Matches(new nint(42), 123, Started.AddSeconds(1)));
    }
}
