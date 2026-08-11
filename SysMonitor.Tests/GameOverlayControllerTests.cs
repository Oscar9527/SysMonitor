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
    public async Task CompatibilityMode_MakesOverlayUnavailableAndRejectsShow()
    {
        var tracker = new ForegroundTargetTracker(new RepeatingSource(null), 999);
        var provider = new BlockingFrameProvider();
        var view = new FakeView();
        await using var controller = new GameOverlayController(
            provider,
            new FakeMonitorService(),
            tracker,
            view);

        controller.SetCompatibilityMode(true);
        await controller.ToggleFromTrayAsync();

        Assert.False(controller.IsOverlayAvailable);
        Assert.False(controller.DesiredVisible);
        Assert.Equal(0, provider.StartCalls);
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
        public event EventHandler? TargetInvalidated
        {
            add { }
            remove { }
        }
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
    }
}
