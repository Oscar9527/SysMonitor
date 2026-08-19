using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class ForegroundTargetTrackerTests
{
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Theory]
    [InlineData("explorer", "GameWindow")]
    [InlineData("dwm.exe", "GameWindow")]
    [InlineData("ShellExperienceHost", "GameWindow")]
    [InlineData("game", "Shell_TrayWnd")]
    [InlineData("game", "Progman")]
    [InlineData("game", "WorkerW")]
    public void Policy_ExcludesShellProcessesAndClasses(string processName, string windowClass)
    {
        ForegroundWindowCandidate candidate = Candidate(processName: processName, windowClass: windowClass);

        Assert.False(ForegroundTargetPolicy.IsQualified(candidate, currentProcessId: 99));
    }

    [Fact]
    public void Policy_ExcludesCurrentProcessAndInvalidOrExitedTargets()
    {
        Assert.False(ForegroundTargetPolicy.IsQualified(Candidate(processId: 42), 42));
        Assert.False(ForegroundTargetPolicy.IsQualified(Candidate(isWindow: false), 99));
        Assert.False(ForegroundTargetPolicy.IsQualified(Candidate(hasExited: true), 99));
        Assert.False(ForegroundTargetPolicy.IsQualified(Candidate(windowHandle: nint.Zero), 99));
    }

    [Fact]
    public void ManualTargetCanBeSetWithoutGameNameHeuristic()
    {
        var tracker = new ForegroundTargetTracker(
            new FakeSource(),
            99,
            () => DateTimeOffset.UtcNow);
        var target = new ForegroundTarget(new nint(7), 123, Started, DateTimeOffset.MinValue);

        tracker.SetManualTarget(target);

        Assert.Equal(ForegroundTargetState.Ready, tracker.State);
        Assert.NotNull(tracker.LastQualified);
        Assert.Equal(123, tracker.LastQualified!.ProcessId);
    }

    [Fact]
    public async Task Stabilize_RequiresThreeMatchingSamplesAcrossFiveHundredMilliseconds()
    {
        ForegroundWindowCandidate candidate = Candidate(executablePath: @"C:\Games\LegacyGame.exe");
        var source = new FakeSource(candidate, candidate, candidate, candidate);
        var delays = new List<TimeSpan>();
        var tracker = new ForegroundTargetTracker(
            source,
            99,
            () => DateTimeOffset.UtcNow,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        ForegroundTarget? trigger = tracker.SnapshotTriggerCandidate();
        ForegroundTarget? result = await tracker.StabilizeTriggerCandidateAsync(trigger, default);

        Assert.NotNull(result);
        Assert.Equal(@"C:\Games\LegacyGame.exe", result!.ExecutablePath);
        Assert.Equal(2, delays.Count);
        Assert.All(delays, delay => Assert.Equal(TimeSpan.FromMilliseconds(250), delay));
        Assert.Equal(ForegroundTargetState.Ready, tracker.State);
    }

    [Fact]
    public async Task Stabilize_RejectsChangedForegroundIdentity()
    {
        ForegroundWindowCandidate first = Candidate(windowHandle: new nint(1));
        ForegroundWindowCandidate changed = Candidate(windowHandle: new nint(2));
        var source = new FakeSource(first, first, changed);
        var tracker = new ForegroundTargetTracker(
            source,
            99,
            delay: (_, _) => Task.CompletedTask);

        ForegroundTarget? result = await tracker.StabilizeTriggerCandidateAsync(
            tracker.SnapshotTriggerCandidate(),
            default);

        Assert.Null(result);
        Assert.Null(tracker.LastQualified);
    }

    [Fact]
    public async Task RecentTarget_RejectsStaleAndPidReuse()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ForegroundWindowCandidate candidate = Candidate();
        var source = new FakeSource(candidate, candidate, candidate, candidate) { IdentityValid = true };
        var tracker = new ForegroundTargetTracker(
            source,
            99,
            () => now,
            (_, _) => Task.CompletedTask);
        await tracker.StabilizeTriggerCandidateAsync(tracker.SnapshotTriggerCandidate(), default);
        Assert.NotNull(tracker.TryGetRecentTarget());

        source.IdentityValid = false;
        Assert.Null(tracker.TryGetRecentTarget());

        source.IdentityValid = true;
        now += TimeSpan.FromSeconds(11);
        Assert.Null(tracker.TryGetRecentTarget());
    }

    [Fact]
    public async Task WaitingForTarget_IsCancellableWithoutSelectingBackgroundProcess()
    {
        var source = new FakeSource();
        var tracker = new ForegroundTargetTracker(source, 99);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tracker.WaitForTargetAsync(cancellation.Token));

        Assert.Equal(ForegroundTargetState.Idle, tracker.State);
        Assert.Equal(0, source.CaptureCalls);
    }

    private static ForegroundWindowCandidate Candidate(
        nint? windowHandle = null,
        int processId = 42,
        string processName = "game",
        string windowClass = "GameWindow",
        bool isWindow = true,
        bool hasExited = false,
        string? executablePath = null) =>
        new(windowHandle ?? new nint(1), processId, Started, processName, windowClass,
            isWindow, IsVisible: true, HasExited: hasExited, ExecutablePath: executablePath);

    private sealed class FakeSource(params ForegroundWindowCandidate?[] candidates)
        : IForegroundWindowSource
    {
        private readonly Queue<ForegroundWindowCandidate?> _candidates = new(candidates);
        public bool IdentityValid { get; set; } = true;
        public int CaptureCalls { get; private set; }

        public ForegroundWindowCandidate? Capture()
        {
            CaptureCalls++;
            return _candidates.TryDequeue(out ForegroundWindowCandidate? value) ? value : null;
        }

        public bool IsCurrentIdentity(ForegroundTarget target) => IdentityValid;
    }
}
