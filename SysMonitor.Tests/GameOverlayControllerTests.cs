using SysMonitor.Models;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class GameOverlayControllerTests
{
    [Fact]
    public async Task HideDuringStart_CancelsWaitStopsProviderAndNeverStaleReshows()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var candidate = new ForegroundWindowCandidate(
            new nint(7), 77, started, "game", "GameWindow", true, true, false);
        var source = new RepeatingSource(candidate);
        var tracker = new ForegroundTargetTracker(
            source,
            currentProcessId: 999,
            delay: (_, _) => Task.CompletedTask);
        var provider = new BlockingFrameProvider();
        var monitor = new FakeMonitorService();
        var view = new FakeView();
        await using var controller = new GameOverlayController(provider, monitor, tracker, view);

        Task show = controller.ToggleFromHotkeyAsync();
        await provider.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task hide = controller.HideAsync();
        await Task.WhenAll(show, hide).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(view.Visible);
        Assert.Equal(0, view.ShowCalls);
        Assert.True(provider.StopCalls >= 1);
        Assert.False(controller.DesiredVisible);
    }

    [Fact]
    public async Task TrayToggleAlwaysStartsAvailableOverlay()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var candidate = new ForegroundWindowCandidate(
            new nint(8), 88, started, "game", "GameWindow", true, true, false);
        var tracker = new ForegroundTargetTracker(
            new RepeatingSource(candidate),
            999,
            delay: (_, _) => Task.CompletedTask);
        var provider = new ImmediateFrameProvider();
        var view = new FakeView();
        await using var controller = new GameOverlayController(
            provider,
            new FakeMonitorService(),
            tracker,
            view,
            action => action());

        await controller.ToggleFromTrayAsync();

        Assert.True(controller.DesiredVisible);
        Assert.True(view.Visible);
        Assert.Equal(1, provider.StartCalls);

        await controller.HideAsync();
    }

    [Fact]
    public async Task TargetInvalidated_KeepsOverlayVisibleWhenDesiredVisible()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var candidate = new ForegroundWindowCandidate(
            new nint(9), 99, started, "game", "GameWindow", true, true, false);
        var tracker = new ForegroundTargetTracker(
            new RepeatingSource(candidate),
            999,
            delay: (_, _) => Task.CompletedTask);
        var provider = new ImmediateFrameProvider();
        var view = new FakeView();
        await using var controller = new GameOverlayController(
            provider,
            new FakeMonitorService(),
            tracker,
            view,
            action => action());

        await controller.ToggleFromTrayAsync();
        Assert.True(view.Visible);

        // Raise target invalidated
        view.RaiseTargetInvalidated();

        // Overlay should remain visible showing waiting/system stats
        Assert.True(controller.DesiredVisible);
        Assert.True(view.Visible);

        await controller.HideAsync();
    }

    private sealed class RepeatingSource(ForegroundWindowCandidate? candidate)
        : IForegroundWindowSource
    {
        public ForegroundWindowCandidate? Capture() => candidate;
        public bool IsCurrentIdentity(ForegroundTarget target) => candidate is not null;
    }

    private sealed class BlockingFrameProvider : IGameOverlayFrameProvider
    {
        public TaskCompletionSource StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public GameOverlayFrameSnapshot Latest => GameOverlayFrameSnapshot.Unavailable;
        public event EventHandler<GameOverlayFrameSnapshot>? SnapshotUpdated
        {
            add { }
            remove { }
        }

        public async Task StartAsync(int processId, CancellationToken cancellationToken)
        {
            StartCalls++;
            StartEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task StopAsync()
        {
            StopCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateFrameProvider : IGameOverlayFrameProvider
    {
        public int StartCalls { get; private set; }
        public GameOverlayFrameSnapshot Latest { get; private set; } =
            GameOverlayFrameSnapshot.Unavailable;
        public event EventHandler<GameOverlayFrameSnapshot>? SnapshotUpdated
        {
            add { }
            remove { }
        }

        public Task StartAsync(int processId, CancellationToken cancellationToken)
        {
            StartCalls++;
            Latest = new GameOverlayFrameSnapshot(
                60,
                GameOverlayFrameStatus.Active,
                DateTimeOffset.UtcNow,
                FrameRateSource.RtssSharedMemory);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Latest = GameOverlayFrameSnapshot.Unavailable;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMonitorService : IMonitorService
    {
        public event EventHandler<MonitorSnapshot>? SnapshotUpdated
        {
            add { }
            remove { }
        }
        public MonitorSnapshot Latest => MonitorSnapshot.Empty;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeView : IGameOverlayView
    {
        public event EventHandler? TargetInvalidated;
        public bool OverlayVisible => Visible;
        public bool Visible { get; private set; }
        public int ShowCalls { get; private set; }
        public void SetTarget(ForegroundTarget? target) { }
        public void UpdateMetrics(
            MonitorSnapshot monitor,
            GameOverlayFrameSnapshot frame,
            double? currentFrequencyMegahertz = null) { }
        public void ShowWithoutActivation() { Visible = true; ShowCalls++; }
        public void HideOverlay() => Visible = false;
        public void RaiseTargetInvalidated() => TargetInvalidated?.Invoke(this, EventArgs.Empty);
    }
}
