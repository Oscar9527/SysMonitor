using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using SysMonitor.Models;
using SysMonitor.Services;
using Xunit;

namespace SysMonitor.Tests;

public sealed class FrameRateBugfixTests
{
    [Fact]
    public void GlobalHotkeyService_ProcessMessage_HandlesHigh64BitWParamAndMatchesId()
    {
        using var service = new GlobalHotkeyService(new FakeHotkeyNative(), createWindow: false);
        int pressedCount = 0;
        service.Pressed += (_, _) => pressedCount++;

        Assert.True(service.ProcessMessage(0x0312, new nint(GlobalHotkeyService.HotkeyId)));
        Assert.Equal(1, pressedCount);

        if (Environment.Is64BitProcess)
        {
            Assert.False(service.ProcessMessage(0x0312, unchecked((nint)0x1_0000_534DL)));
            Assert.Equal(1, pressedCount);
        }
    }

    [Fact]
    public void OverlayMonitorIdentityText_BuildFallbackId_UsesInvariantCulture()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            string fallbackId = OverlayMonitorIdentityText.BuildFallbackId(
                @"\\.\DISPLAY2",
                new ScreenPixelBounds(-1920, -1080, 0, 0));

            Assert.Equal(@"fallback:gdi=\\.\DISPLAY2;bounds=-1920,-1080,0,0", fallbackId);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void PresentMonHelperHost_TryGetRequest_ParsesStrictInvariantProcessId()
    {
        string pipeName = $"SysMonitor.PresentMon.{Guid.Empty:N}";
        string sessionName = $"SysMonitor-1-{Guid.Empty:N}";
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");

            Assert.True(PresentMonHelperHost.TryGetRequest(
                ["--presentmon-helper", pipeName, "1234", sessionName],
                out string parsedPipe,
                out int parsedPid,
                out string parsedSession));
            Assert.Equal(pipeName, parsedPipe);
            Assert.Equal(1234, parsedPid);
            Assert.Equal(sessionName, parsedSession);

            Assert.False(PresentMonHelperHost.TryGetRequest(
                ["--presentmon-helper", pipeName, "+1234", sessionName],
                out _,
                out _,
                out _));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void FrameRateAggregator_Add_RejectsNonFiniteAndNegativeFrames()
    {
        var aggregator = new FrameRateAggregator();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.False(aggregator.Add(new PresentMonFrame(100, 1, double.NaN, 16.0, "game.exe"), now));
        Assert.False(aggregator.Add(new PresentMonFrame(100, 1, double.PositiveInfinity, 16.0, "game.exe"), now));
        Assert.False(aggregator.Add(new PresentMonFrame(100, 1, 1.0, double.NaN, "game.exe"), now));
        Assert.False(aggregator.Add(new PresentMonFrame(100, 1, 1.0, double.PositiveInfinity, "game.exe"), now));
        Assert.False(aggregator.Add(new PresentMonFrame(100, 1, -0.01, 16.0, "game.exe"), now));
        Assert.False(aggregator.Add(new PresentMonFrame(100, 1, 1.0, -16.0, "game.exe"), now));
        Assert.False(aggregator.HasReceivedFrames);
        Assert.Equal(0, aggregator.ChainCount);

        Assert.True(aggregator.Add(new PresentMonFrame(100, 1, 0.016, 16.0, "game.exe"), now));
        Assert.True(aggregator.Add(new PresentMonFrame(100, 1, 0.032, 16.0, "game.exe"), now));
        Assert.Equal(62.5d, aggregator.Read(now)!.Value, 3);
    }

    [Fact]
    public async Task GameOverlayFrameProviderAdapter_MapsStaleStatusToNoFrames()
    {
        var inner = new StubFrameRateProvider(
            new FrameRateSnapshot(null, FrameRateStatus.Stale, 123, DateTimeOffset.UtcNow));
        await using var adapter = new GameOverlayFrameProviderAdapter(inner);

        Assert.Equal(GameOverlayFrameStatus.NoFrames, adapter.Latest.Status);
        Assert.Null(adapter.Latest.FramesPerSecond);

        inner.Emit(new FrameRateSnapshot(60, FrameRateStatus.Stale, 123, DateTimeOffset.UtcNow));
        Assert.Equal(GameOverlayFrameStatus.NoFrames, adapter.Latest.Status);
        Assert.Null(adapter.Latest.FramesPerSecond);
    }

    [Fact]
    public async Task AdaptiveFrameRateProvider_ConcurrentDisposeAndStop_DoNotThrow()
    {
        var provider = new AdaptiveFrameRateProvider(
            new MissingRtssSource(),
            new StubFrameRateProvider(FrameRateSnapshot.Disabled));

        await Task.WhenAll(
            provider.DisposeAsync().AsTask(),
            provider.DisposeAsync().AsTask(),
            provider.StopAsync());
    }

    [Fact]
    public async Task GameOverlayController_ShowForTargetAfterDispose_ThrowsWithoutMutatingTracker()
    {
        var tracker = new ForegroundTargetTracker(
            new MutableSource(null),
            999,
            delay: (_, _) => Task.CompletedTask);
        var controller = new GameOverlayController(
            new ImmediateFrameProvider(),
            new FakeMonitorService(),
            tracker,
            new FakeView(),
            action => action());
        await controller.DisposeAsync();

        var target = new ForegroundTarget(
            new nint(10),
            100,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.ShowForTargetAsync(target));
        Assert.Null(tracker.LastQualified);
    }

    [Fact]
    public async Task GameOverlayController_ConcurrentDispose_DoesNotThrow()
    {
        var tracker = new ForegroundTargetTracker(
            new MutableSource(null),
            999,
            delay: (_, _) => Task.CompletedTask);
        var controller = new GameOverlayController(
            new ImmediateFrameProvider(),
            new FakeMonitorService(),
            tracker,
            new FakeView(),
            action => action());

        await Task.WhenAll(
            controller.DisposeAsync().AsTask(),
            controller.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task GameOverlayController_RetargetAfterExit_SupersededDoesNotCallStopAgain()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var target1Candidate = new ForegroundWindowCandidate(
            new nint(10), 100, started, "game", "GameWindow", true, true, false);
        var target2 = new ForegroundTarget(
            new nint(20), 200, started.AddSeconds(10), DateTimeOffset.UtcNow);
        var target2Candidate = new ForegroundWindowCandidate(
            target2.WindowHandle, target2.ProcessId, target2.ProcessStartedAt, "game2", "GameWindow2", true, true, false);

        var source = new MutableSource(target1Candidate);
        var tracker = new ForegroundTargetTracker(source, 999, delay: (_, _) => Task.CompletedTask);
        var provider = new RecordingBlockingFirstStartProvider();
        var view = new FakeView();
        await using var controller = new GameOverlayController(
            provider, new FakeMonitorService(), tracker, view, action => action());

        Task show1 = controller.ToggleFromTrayAsync();
        await provider.FirstStartEntered.Task;

        view.RaiseTargetInvalidated();
        source.CurrentCandidate = target2Candidate;
        Task show2 = controller.ShowForTargetAsync(target2);

        provider.ReleaseFirstStart.SetResult();
        await Task.WhenAll(show1, show2);

        Assert.Equal(["start:100", "stop", "start:200"], provider.Events.ToArray());
    }

    [Fact]
    public async Task GameOverlayController_MonitorForegroundTargets_ReShowsManualTargetAndHidesOnPidReuse()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var explorer = new ForegroundWindowCandidate(
            new nint(50), 500, started, "explorer", "CabinetWClass", true, true, false);
        var other = new ForegroundWindowCandidate(
            new nint(60), 600, started, "notepad", "Notepad", true, true, false);
        var reusedPid = explorer with { ProcessStartedAt = started.AddSeconds(20) };
        var manualTarget = new ForegroundTarget(
            explorer.WindowHandle, explorer.ProcessId, explorer.ProcessStartedAt, DateTimeOffset.UtcNow);

        var source = new MutableSource(explorer);
        var tracker = new ForegroundTargetTracker(source, 999, delay: (_, _) => Task.CompletedTask);
        var provider = new ImmediateFrameProvider();
        var view = new FakeView();
        await using var controller = new GameOverlayController(
            provider, new FakeMonitorService(), tracker, view, action => action());

        await controller.ShowForTargetAsync(manualTarget);
        await WaitUntilAsync(() => view.Visible);

        source.CurrentCandidate = other;
        await WaitUntilAsync(() => !view.Visible);

        source.CurrentCandidate = explorer;
        await WaitUntilAsync(() => view.Visible);

        source.CurrentCandidate = reusedPid;
        await WaitUntilAsync(() => !view.Visible);
    }

    [Fact]
    public async Task RtssLegacyCompatibilityService_ConcurrentEnable_SerializesWithoutConflict()
    {
        string root = Path.Combine(Path.GetTempPath(), "rtss-concurrent-" + Guid.NewGuid().ToString("N"));
        string profiles = Path.Combine(root, "Profiles");
        string backup = Path.Combine(root, "Backup");
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(backup);
        string exe = Path.Combine(root, "Game.exe");
        await File.WriteAllBytesAsync(exe, [0x4D, 0x5A]);

        try
        {
            var service = new RtssLegacyCompatibilityService(profiles, backup);
            using var gate = new ManualResetEventSlim(false);
            Task<RtssCompatibilityResult>[] tasks = Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() =>
                {
                    gate.Wait();
                    return service.Enable(exe);
                }))
                .ToArray();

            gate.Set();
            RtssCompatibilityResult[] results = await Task.WhenAll(tasks);

            Assert.All(results, result =>
            {
                Assert.True(result.Success);
                Assert.True(result.Code is RtssCompatibilityCode.Success or RtssCompatibilityCode.AlreadyEnabled);
            });
            Assert.Single(Directory.GetFiles(backup, "*.json"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class FakeHotkeyNative : IGlobalHotkeyNative
    {
        public bool Register(nint windowHandle, int id, uint modifiers, uint virtualKey) => true;
        public bool Unregister(nint windowHandle, int id) => true;
        public int GetLastError() => 0;
    }

    private sealed class MissingRtssSource : IRtssFrameSource
    {
        public SharedMemoryValue Read(int processId) => SharedMemoryValue.Missing("none");
    }

    private sealed class StubFrameRateProvider : IFrameRateProvider
    {
        private FrameRateSnapshot _latest;

        public StubFrameRateProvider(FrameRateSnapshot initial) => _latest = initial;

        public FrameRateSnapshot Latest => _latest;
        public event EventHandler<FrameRateSnapshot>? SnapshotUpdated;

        public Task StartAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Emit(FrameRateSnapshot snapshot)
        {
            _latest = snapshot;
            SnapshotUpdated?.Invoke(this, snapshot);
        }
    }

    private sealed class MutableSource : IForegroundWindowSource
    {
        private ForegroundWindowCandidate? _current;

        public MutableSource(ForegroundWindowCandidate? initial) => _current = initial;

        public ForegroundWindowCandidate? CurrentCandidate
        {
            get => Volatile.Read(ref _current);
            set => Volatile.Write(ref _current, value);
        }

        public ForegroundWindowCandidate? Capture() => CurrentCandidate;

        public bool IsCurrentIdentity(ForegroundTarget target)
        {
            ForegroundWindowCandidate? current = CurrentCandidate;
            return current is not null &&
                   current.WindowHandle == target.WindowHandle &&
                   current.ProcessId == target.ProcessId &&
                   current.ProcessStartedAt == target.ProcessStartedAt;
        }
    }

    private sealed class ImmediateFrameProvider : IGameOverlayFrameProvider
    {
        public GameOverlayFrameSnapshot Latest { get; private set; } =
            GameOverlayFrameSnapshot.Unavailable;

        public event EventHandler<GameOverlayFrameSnapshot>? SnapshotUpdated
        {
            add { }
            remove { }
        }

        public Task StartAsync(int processId, CancellationToken cancellationToken)
        {
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

    private sealed class RecordingBlockingFirstStartProvider : IGameOverlayFrameProvider
    {
        private int _startCalls;

        public ConcurrentQueue<string> Events { get; } = new();
        public TaskCompletionSource FirstStartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstStart { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GameOverlayFrameSnapshot Latest { get; private set; } =
            GameOverlayFrameSnapshot.Unavailable;

        public event EventHandler<GameOverlayFrameSnapshot>? SnapshotUpdated
        {
            add { }
            remove { }
        }

        public async Task StartAsync(int processId, CancellationToken cancellationToken)
        {
            Events.Enqueue($"start:{processId}");
            if (Interlocked.Increment(ref _startCalls) == 1)
            {
                FirstStartEntered.TrySetResult();
                await ReleaseFirstStart.Task;
            }

            Latest = new GameOverlayFrameSnapshot(
                60,
                GameOverlayFrameStatus.Active,
                DateTimeOffset.UtcNow,
                FrameRateSource.RtssSharedMemory);
        }

        public Task StopAsync()
        {
            Events.Enqueue("stop");
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
        public void SetTarget(ForegroundTarget? target) { }
        public void UpdateMetrics(
            MonitorSnapshot monitor,
            GameOverlayFrameSnapshot frame,
            double? currentFrequencyMegahertz = null) { }
        public void ShowWithoutActivation() => Visible = true;
        public void HideOverlay() => Visible = false;
        public void RaiseTargetInvalidated() => TargetInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not satisfied within the timeout.");
            }

            await Task.Delay(10);
        }
    }
}
