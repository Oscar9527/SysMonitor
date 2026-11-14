using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class TaskbarPlacementStabilizerTests
{
    [Theory]
    [InlineData(2560, 96)]
    [InlineData(3840, 144)]
    [InlineData(1280, 120)]
    public void MonitorSwitchRestoresNearRightPercentage(int newWidth, uint newDpi)
    {
        TaskbarSafeConstraint original = new(new(1, 1920, 40, 96), 500, 1800);
        TaskbarBandRect originalBand = TaskbarPlacementStabilizer.Decide(
            original, 40, 400, 34, 98, null, true).Rect;
        TaskbarSafeConstraint switched = new(new(1, newWidth, 40, newDpi), 300, newWidth - 120);

        TaskbarPlacementDecision actual = TaskbarPlacementStabilizer.Decide(
            switched, 40, 400, 34, 98, originalBand,
            TaskbarPlacementStabilizer.RequiresReanchor(original, switched));
        TaskbarPlacementDecision expected = TaskbarPlacementStabilizer.Decide(
            switched, 40, 400, 34, 98, null, true);

        Assert.Equal(expected.Rect, actual.Rect);
    }

    [Fact]
    public void NearRightAnchorReturnsAfterTransientNarrowRegionExpands()
    {
        TaskbarSafeConstraint original = Constraint(100, 900);
        TaskbarSafeConstraint narrowed = Constraint(300, 700);
        TaskbarBandRect clamped = TaskbarPlacementStabilizer.Decide(
            narrowed, 40, 400, 34, 98, new TaskbarBandRect(492, 3, 400, 34), false).Rect;

        TaskbarPlacementDecision restored = TaskbarPlacementStabilizer.Decide(
            original, 40, 400, 34, 98, clamped,
            TaskbarPlacementStabilizer.RequiresReanchor(narrowed, original));

        Assert.Equal(492, restored.Rect.X);
    }

    [Theory]
    [InlineData(100, 900)]
    [InlineData(99, 901)]
    [InlineData(98, 902)]
    public void UnchangedLayoutAndSmallBoundaryJitterDoNotRequestReanchor(int left, int right)
    {
        Assert.False(TaskbarPlacementStabilizer.RequiresReanchor(
            Constraint(100, 900), Constraint(left, right)));
    }

    [Fact]
    public void DpiOrTaskbarIdentityChangeReanchorsEvenWithMatchingPixelBounds()
    {
        TaskbarSafeConstraint original = Constraint(100, 900);
        Assert.True(TaskbarPlacementStabilizer.RequiresReanchor(null, original));
        Assert.True(TaskbarPlacementStabilizer.RequiresReanchor(
            original, original with { Key = original.Key with { Dpi = 144 } }));
        Assert.True(TaskbarPlacementStabilizer.RequiresReanchor(
            original, original with { Key = original.Key with { TaskbarHandle = 2 } }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1040)]
    public void HorizontalTaskbarUsesParentRelativeCenteredY(int taskbarTop)
    {
        TaskbarRegionSnapshot snapshot = Snapshot(
            handle: 1,
            top: taskbarTop,
            width: 1920,
            height: 40,
            safeLeft: 300,
            safeRight: 1700);
        var tracker = new TaskbarSafeConstraintTracker();
        TaskbarSafeConstraint constraint = tracker.Observe(snapshot)!.Value;

        TaskbarPlacementDecision decision = TaskbarPlacementStabilizer.Decide(
            constraint,
            taskbarHeight: 40,
            desiredWidth: 400,
            desiredHeight: 34,
            positionPercent: 50,
            current: null,
            explicitLayoutChange: true);

        Assert.False(decision.HideRequested);
        Assert.Equal(3, decision.Rect.Y);
    }

    [Fact]
    public void VerticalTaskbarRequestsHide()
    {
        TaskbarRegionSnapshot snapshot = Snapshot(
            handle: 1,
            top: 0,
            width: 48,
            height: 1080,
            safeLeft: 0,
            safeRight: 48);
        var tracker = new TaskbarSafeConstraintTracker();
        TaskbarSafeConstraint constraint = tracker.Observe(snapshot)!.Value;

        TaskbarPlacementDecision decision = TaskbarPlacementStabilizer.Decide(
            constraint, 1080, 34, 400, 50, null, true);

        Assert.True(decision.HideRequested);
    }

    [Fact]
    public void ConstraintContractsImmediatelyAndExpandsAfterTwoMatches()
    {
        var tracker = new TaskbarSafeConstraintTracker();
        AssertBounds(tracker.Observe(Snapshot(generation: 1, safeLeft: 100, safeRight: 900)), 100, 900);
        AssertBounds(tracker.Observe(Snapshot(generation: 2, safeLeft: 120, safeRight: 880)), 120, 880);

        AssertBounds(tracker.Observe(Snapshot(generation: 3, safeLeft: 100, safeRight: 900)), 120, 880);
        Assert.True(tracker.HasPendingExpansion);
        AssertBounds(tracker.Observe(Snapshot(generation: 4, safeLeft: 100, safeRight: 900)), 100, 900);
        Assert.False(tracker.HasPendingExpansion);
    }

    [Fact]
    public void ConstraintStabilizesLeftAndRightIndependently()
    {
        var tracker = new TaskbarSafeConstraintTracker();
        _ = tracker.Observe(Snapshot(generation: 1, safeLeft: 100, safeRight: 900));

        AssertBounds(tracker.Observe(Snapshot(generation: 2, safeLeft: 120, safeRight: 920)), 120, 900);
        Assert.True(tracker.HasPendingExpansion);
        AssertBounds(tracker.Observe(Snapshot(generation: 3, safeLeft: 120, safeRight: 920)), 120, 920);
        Assert.False(tracker.HasPendingExpansion);
    }

    [Fact]
    public void FailedProbeDoesNotExpandAndBreaksConsecutiveConfirmation()
    {
        var tracker = new TaskbarSafeConstraintTracker();
        _ = tracker.Observe(Snapshot(generation: 1, safeLeft: 100, safeRight: 900));
        _ = tracker.Observe(Snapshot(generation: 2, safeLeft: 80, safeRight: 920));
        Assert.True(tracker.HasPendingExpansion);

        AssertBounds(tracker.Observe(Snapshot(generation: 3, valid: false)), 100, 900);
        Assert.False(tracker.HasPendingExpansion);
        AssertBounds(tracker.Observe(Snapshot(generation: 4, safeLeft: 80, safeRight: 920)), 100, 900);
        Assert.True(tracker.HasPendingExpansion);
    }

    [Fact]
    public void RootGenerationChangeResetsConstraintAndFailedNewRootHasNoConstraint()
    {
        var tracker = new TaskbarSafeConstraintTracker();
        _ = tracker.Observe(Snapshot(generation: 1, handle: 1, safeLeft: 100, safeRight: 900));

        Assert.Null(tracker.Observe(Snapshot(generation: 2, handle: 2, valid: false)));
        AssertBounds(
            tracker.Observe(Snapshot(generation: 3, handle: 2, safeLeft: 200, safeRight: 800)),
            200,
            800);
    }

    [Fact]
    public void CachedSnapshotCannotConfirmPendingExpansion()
    {
        var tracker = new TaskbarSafeConstraintTracker();
        _ = tracker.Observe(Snapshot(generation: 1, safeLeft: 100, safeRight: 900));
        _ = tracker.Observe(Snapshot(generation: 2, safeLeft: 80, safeRight: 920));

        AssertBounds(
            tracker.Observe(Snapshot(generation: 2, safeLeft: 80, safeRight: 920)),
            100,
            900);
        Assert.True(tracker.HasPendingExpansion);

        AssertBounds(
            tracker.Observe(Snapshot(generation: 3, safeLeft: 80, safeRight: 920)),
            80,
            920);
        Assert.False(tracker.HasPendingExpansion);
    }

    [Fact]
    public void RejectedConfirmationClearsPendingWithoutDiscardingSafeConstraint()
    {
        var tracker = new TaskbarSafeConstraintTracker();
        _ = tracker.Observe(Snapshot(generation: 1, safeLeft: 100, safeRight: 900));
        _ = tracker.Observe(Snapshot(generation: 2, safeLeft: 80, safeRight: 920));

        tracker.RejectPendingExpansion(observedGeneration: 3);

        Assert.False(tracker.HasPendingExpansion);
        AssertBounds(tracker.Current, 100, 900);
        AssertBounds(
            tracker.Observe(Snapshot(generation: 3, safeLeft: 80, safeRight: 920)),
            100,
            900);
    }

    [Fact]
    public void TrustedUnsafeObservationImmediatelyDiscardsOldConstraint()
    {
        var tracker = new TaskbarSafeConstraintTracker();
        _ = tracker.Observe(Snapshot(generation: 1, safeLeft: 100, safeRight: 900));

        TaskbarSafeConstraint? constraint = tracker.Observe(
            Snapshot(
                generation: 2,
                safeLeft: 600,
                safeRight: 500,
                valid: false,
                hasTrustedBounds: true));

        Assert.Null(constraint);
        Assert.Null(tracker.Current);
        Assert.False(tracker.HasPendingExpansion);
    }

    [Fact]
    public void MatchingConfirmationExpandsConstraintWithoutMovingContainedBand()
    {
        var tracker = new TaskbarSafeConstraintTracker();
        _ = tracker.Observe(Snapshot(generation: 1, safeLeft: 100, safeRight: 900));
        TaskbarSafeConstraint firstCandidate = tracker.Observe(
            Snapshot(generation: 2, safeLeft: 80, safeRight: 920))!.Value;
        var current = new TaskbarBandRect(300, 3, 400, 34);

        TaskbarPlacementDecision beforeConfirmation = TaskbarPlacementStabilizer.Decide(
            firstCandidate, 40, 400, 34, 50, current, false);
        TaskbarSafeConstraint confirmed = tracker.Observe(
            Snapshot(generation: 3, safeLeft: 80, safeRight: 920))!.Value;
        TaskbarPlacementDecision afterConfirmation = TaskbarPlacementStabilizer.Decide(
            confirmed, 40, 400, 34, 50, current, false);

        Assert.False(beforeConfirmation.SetWindowPosition);
        Assert.False(afterConfirmation.SetWindowPosition);
        Assert.Equal(current, afterConfirmation.Rect);
    }

    [Theory]
    [InlineData(99, 901)]
    [InlineData(98, 902)]
    public void SafeBoundaryJitterDoesNotMoveContainedBand(int left, int right)
    {
        TaskbarSafeConstraint constraint = Constraint(left, right);
        var current = new TaskbarBandRect(300, 3, 400, 34);

        TaskbarPlacementDecision decision = TaskbarPlacementStabilizer.Decide(
            constraint, 40, 400, 34, 50, current, false);

        Assert.False(decision.SetWindowPosition);
        Assert.Equal(current, decision.Rect);
    }

    [Fact]
    public void OutOfBoundsBandIsClampedByMinimumDistance()
    {
        TaskbarSafeConstraint constraint = Constraint(120, 900);

        TaskbarPlacementDecision decision = TaskbarPlacementStabilizer.Decide(
            constraint,
            40,
            400,
            34,
            50,
            new TaskbarBandRect(110, 3, 400, 34),
            false);

        Assert.True(decision.SetWindowPosition);
        Assert.Equal(120, decision.Rect.X);
    }

    [Fact]
    public void OneHundredPercentUsesNewFeasibleMaximumAfterWidthGrowth()
    {
        TaskbarSafeConstraint constraint = Constraint(100, 900);

        TaskbarPlacementDecision decision = TaskbarPlacementStabilizer.Decide(
            constraint,
            40,
            500,
            34,
            100,
            new TaskbarBandRect(500, 3, 400, 34),
            true);

        Assert.Equal(400, decision.Rect.X);
        Assert.Equal(500, decision.Rect.Width);
    }

    [Fact]
    public void NoFeasibleIntervalRequestsHide()
    {
        TaskbarPlacementDecision decision = TaskbarPlacementStabilizer.Decide(
            Constraint(100, 400), 40, 301, 34, 50, null, true);

        Assert.True(decision.HideRequested);
    }

    [Fact]
    public void SubLegacyMinimumSingleMetricWidthCanBePlaced()
    {
        TaskbarPlacementDecision decision = TaskbarPlacementStabilizer.Decide(
            Constraint(100, 400), 40, 72, 34, 50, null, true);

        Assert.False(decision.HideRequested);
        Assert.Equal(72, decision.Rect.Width);
    }

    [Fact]
    public void ExplicitLayoutBypassesPositionDeadZoneButRemainsConstrained()
    {
        TaskbarSafeConstraint constraint = Constraint(250, 900);
        var current = new TaskbarBandRect(300, 3, 400, 34);

        TaskbarPlacementDecision decision = TaskbarPlacementStabilizer.Decide(
            constraint, 40, 400, 34, 0, current, true);

        Assert.True(decision.SetWindowPosition);
        Assert.Equal(250, decision.Rect.X);
    }

    [Fact]
    public void RecoveryReanchorRestoresConfiguredInteriorPositionAfterTemporaryClamp()
    {
        var current = new TaskbarBandRect(500, 3, 400, 34);
        TaskbarPlacementDecision clamped = TaskbarPlacementStabilizer.Decide(
            Constraint(300, 700), 40, 400, 34, 50, current, false);

        Assert.True(clamped.SetWindowPosition);
        Assert.Equal(300, clamped.Rect.X);

        TaskbarSafeConstraint restored = Constraint(100, 1000);
        TaskbarPlacementDecision withoutRecoveryReanchor =
            TaskbarPlacementStabilizer.Decide(
                restored, 40, 400, 34, 50, clamped.Rect, false);
        TaskbarPlacementDecision withRecoveryReanchor =
            TaskbarPlacementStabilizer.Decide(
                restored, 40, 400, 34, 50, clamped.Rect, true);

        Assert.False(withoutRecoveryReanchor.SetWindowPosition);
        Assert.Equal(300, withoutRecoveryReanchor.Rect.X);
        Assert.True(withRecoveryReanchor.SetWindowPosition);
        Assert.Equal(350, withRecoveryReanchor.Rect.X);
    }

    [Fact]
    public void RightEdgeAnchorReturnsAfterTwoPhaseConstraintExpansion()
    {
        var tracker = new TaskbarSafeConstraintTracker();
        _ = tracker.Observe(
            Snapshot(generation: 1, safeLeft: 100, safeRight: 900));
        var current = new TaskbarBandRect(500, 3, 400, 34);

        TaskbarSafeConstraint contracted = tracker.Observe(
            Snapshot(generation: 2, safeLeft: 300, safeRight: 700))!.Value;
        TaskbarPlacementDecision clamped = TaskbarPlacementStabilizer.Decide(
            contracted, 40, 400, 34, 100, current, false);
        Assert.Equal(300, clamped.Rect.X);

        TaskbarSafeConstraint pendingExpansion = tracker.Observe(
            Snapshot(generation: 3, safeLeft: 100, safeRight: 900))!.Value;
        TaskbarPlacementDecision stillClamped = TaskbarPlacementStabilizer.Decide(
            pendingExpansion, 40, 400, 34, 100, clamped.Rect, false);
        Assert.Equal(300, stillClamped.Rect.X);

        TaskbarSafeConstraint confirmedExpansion = tracker.Observe(
            Snapshot(generation: 4, safeLeft: 100, safeRight: 900))!.Value;
        TaskbarPlacementDecision restored = TaskbarPlacementStabilizer.Decide(
            confirmedExpansion, 40, 400, 34, 100, stillClamped.Rect, false);

        Assert.True(restored.SetWindowPosition);
        Assert.Equal(500, restored.Rect.X);
    }

    private static TaskbarSafeConstraint Constraint(int left, int right) =>
        new(new TaskbarConstraintKey(1, 1000, 40, 96), left, right);

    private static void AssertBounds(
        TaskbarSafeConstraint? constraint,
        int expectedLeft,
        int expectedRight)
    {
        Assert.NotNull(constraint);
        Assert.Equal(expectedLeft, constraint.Value.Left);
        Assert.Equal(expectedRight, constraint.Value.Right);
    }

    private static TaskbarRegionSnapshot Snapshot(
        long generation = 1,
        nint handle = default,
        int top = 0,
        int width = 1000,
        int height = 40,
        int safeLeft = 100,
        int safeRight = 900,
        bool valid = true,
        bool? hasTrustedBounds = null) =>
        new(
            generation,
            handle == nint.Zero ? 1 : handle,
            0,
            top,
            width,
            top + height,
            safeLeft,
            safeRight,
            96,
            valid,
            hasTrustedBounds ?? valid);
}
