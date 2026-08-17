using System.Diagnostics;
using System.Windows;
using SysMonitor.Models;

namespace SysMonitor.Services;

public enum GameOverlayFrameStatus
{
    Unavailable,
    WaitingForTarget,
    Starting,
    NoFrames,
    Active,
    Faulted
}

public sealed record GameOverlayFrameSnapshot(
    double? FramesPerSecond,
    GameOverlayFrameStatus Status,
    DateTimeOffset SampledAt,
    FrameRateSource Source = FrameRateSource.None,
    string? Detail = null)
{
    public static GameOverlayFrameSnapshot Unavailable { get; } =
        new(null, GameOverlayFrameStatus.Unavailable, DateTimeOffset.MinValue);
}

public interface IGameOverlayFrameProvider
{
    GameOverlayFrameSnapshot Latest { get; }
    event EventHandler<GameOverlayFrameSnapshot>? SnapshotUpdated;
    Task StartAsync(int processId, CancellationToken cancellationToken);
    Task StopAsync();
}

public interface IGameOverlayView
{
    event EventHandler? TargetInvalidated;
    bool OverlayVisible { get; }
    void SetTarget(ForegroundTarget? target);
    void UpdateMetrics(
        MonitorSnapshot monitor,
        GameOverlayFrameSnapshot frame,
        double? currentFrequencyMegahertz = null);
    void ShowWithoutActivation();
    void HideOverlay();
}

public enum GameOverlayActivationSource
{
    Hotkey,
    Tray
}

public sealed class GameOverlayController : IAsyncDisposable
{
    private readonly IGameOverlayFrameProvider _frameProvider;
    private readonly IMonitorService _monitorService;
    private readonly ForegroundTargetTracker _targetTracker;
    private readonly IGameOverlayView _view;
    private readonly UiRefreshScheduler _uiRefreshScheduler;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _targetCancellation;
    private bool _desiredVisible;
    private bool _disposed;
    private long _generation;

    public GameOverlayController(
        IGameOverlayFrameProvider frameProvider,
        IMonitorService monitorService,
        ForegroundTargetTracker targetTracker,
        IGameOverlayView view)
    {
        _frameProvider = frameProvider ?? throw new ArgumentNullException(nameof(frameProvider));
        _monitorService = monitorService ?? throw new ArgumentNullException(nameof(monitorService));
        _targetTracker = targetTracker ?? throw new ArgumentNullException(nameof(targetTracker));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _frameProvider.SnapshotUpdated += OnFrameUpdated;
        _monitorService.SnapshotUpdated += OnMonitorUpdated;
        _view.TargetInvalidated += OnTargetInvalidated;
        _uiRefreshScheduler = new UiRefreshScheduler(
            dispatch: RunOnUi,
            isActive: () => DesiredVisible && _view.OverlayVisible,
            callback: () => _view.UpdateMetrics(_monitorService.Latest, _frameProvider.Latest));
    }

    public event EventHandler? StateChanged;

    public bool DesiredVisible
    {
        get { lock (_stateGate) { return _desiredVisible; } }
    }

    public bool IsVisible => _view.OverlayVisible;

    public ForegroundTarget? CurrentTarget => _targetTracker.LastQualified;

    public Task ToggleFromHotkeyAsync()
    {
        ForegroundTarget? triggerBefore = _targetTracker.SnapshotTriggerCandidate();
        return ToggleAsync(GameOverlayActivationSource.Hotkey, triggerBefore);
    }

    public Task ToggleFromTrayAsync() => ToggleAsync(GameOverlayActivationSource.Tray, null);

    public Task ShowForTargetAsync(ForegroundTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _targetTracker.SetManualTarget(target);
        return RequestVisibilityAsync(true, GameOverlayActivationSource.Tray, null);
    }

    public Task HideAsync() =>
        RequestVisibilityAsync(false, GameOverlayActivationSource.Tray, null);

    private Task ToggleAsync(GameOverlayActivationSource source, ForegroundTarget? triggerBefore)
    {
        bool requested;
        lock (_stateGate)
        {
            requested = !_desiredVisible;
        }

        return RequestVisibilityAsync(requested, source, triggerBefore);
    }

    private Task RequestVisibilityAsync(
        bool visible,
        GameOverlayActivationSource source,
        ForegroundTarget? triggerBefore)
    {
        CancellationTokenSource cancellation;
        long generation;
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _desiredVisible = visible;
            generation = ++_generation;
            _targetCancellation?.Cancel();
            _targetCancellation?.Dispose();
            _targetCancellation = cancellation = new CancellationTokenSource();
        }

        if (!visible)
        {
            RunOnUi(_view.HideOverlay);
        }

        RaiseStateChanged();
        return ProcessRequestAsync(visible, source, triggerBefore, generation, cancellation.Token);
    }

    private async Task ProcessRequestAsync(
        bool visible,
        GameOverlayActivationSource source,
        ForegroundTarget? triggerBefore,
        long generation,
        CancellationToken cancellationToken)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsCurrent(generation, visible))
            {
                return;
            }

            if (!visible)
            {
                _targetTracker.ResetState();
                await _frameProvider.StopAsync().ConfigureAwait(false);
                return;
            }

            ForegroundTarget? target = source == GameOverlayActivationSource.Tray
                ? _targetTracker.TryGetRecentTarget()
                : await _targetTracker.StabilizeTriggerCandidateAsync(
                    triggerBefore,
                    cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                RunOnUi(() =>
                {
                    if (IsCurrent(generation, visible: true))
                    {
                        _view.SetTarget(null);
                        _view.UpdateMetrics(
                            _monitorService.Latest,
                            new GameOverlayFrameSnapshot(
                                null,
                                GameOverlayFrameStatus.WaitingForTarget,
                                DateTimeOffset.UtcNow));
                        _view.ShowWithoutActivation();
                    }
                });
                target = await _targetTracker.WaitForTargetAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();

            await _frameProvider.StartAsync(target.ProcessId, cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(generation, visible: true) || cancellationToken.IsCancellationRequested)
            {
                await _frameProvider.StopAsync().ConfigureAwait(false);
                return;
            }

            RunOnUi(() =>
            {
                if (!IsCurrent(generation, visible: true))
                {
                    return;
                }

                _view.SetTarget(target);
                _view.UpdateMetrics(_monitorService.Latest, _frameProvider.Latest);
                _view.ShowWithoutActivation();
            });
            RaiseStateChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer generation owns visibility and provider state.
        }
        finally
        {
            _operations.Release();
        }
    }

    private bool IsCurrent(long generation, bool visible)
    {
        lock (_stateGate)
        {
            return !_disposed &&
                generation == _generation &&
                _desiredVisible == visible;
        }
    }

    private void OnFrameUpdated(object? sender, GameOverlayFrameSnapshot snapshot) =>
        RequestUiUpdate();

    private void OnMonitorUpdated(object? sender, MonitorSnapshot snapshot) => RequestUiUpdate();

    private void RequestUiUpdate()
    {
        if (DesiredVisible)
        {
            _uiRefreshScheduler.Request();
        }
    }

    private void OnTargetInvalidated(object? sender, EventArgs e)
    {
        _targetTracker.MarkTargetExited();
        RunOnUi(() =>
        {
            _view.SetTarget(null);
            _view.UpdateMetrics(
                _monitorService.Latest,
                new GameOverlayFrameSnapshot(
                    null,
                    GameOverlayFrameStatus.WaitingForTarget,
                    DateTimeOffset.UtcNow));
        });
        CancellationTokenSource cancellation;
        long generation;
        lock (_stateGate)
        {
            if (_disposed || !_desiredVisible)
            {
                return;
            }

            generation = ++_generation;
            _targetCancellation?.Cancel();
            _targetCancellation?.Dispose();
            _targetCancellation = cancellation = new CancellationTokenSource();
        }

        _ = RetargetAfterExitAsync(generation, cancellation.Token);
    }

    private async Task RetargetAfterExitAsync(long generation, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            await _frameProvider.StopAsync().ConfigureAwait(false);
            if (!IsCurrent(generation, visible: true))
            {
                return;
            }

            ForegroundTarget target = await _targetTracker
                .WaitForTargetAsync(cancellationToken).ConfigureAwait(false);
            await _frameProvider.StartAsync(target.ProcessId, cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(generation, visible: true))
            {
                await _frameProvider.StopAsync().ConfigureAwait(false);
                return;
            }

            RunOnUi(() =>
            {
                if (IsCurrent(generation, visible: true))
                {
                    _view.SetTarget(target);
                    _view.UpdateMetrics(_monitorService.Latest, _frameProvider.Latest);
                    _view.ShowWithoutActivation();
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _operations.Release();
        }
    }

    private static void RunOnUi(Action action)
    {
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }

    private void RaiseStateChanged() =>
        RunOnUi(() => StateChanged?.Invoke(this, EventArgs.Empty));

    public async ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _desiredVisible = false;
            _generation++;
            _targetCancellation?.Cancel();
        }

        _uiRefreshScheduler.Dispose();
        _frameProvider.SnapshotUpdated -= OnFrameUpdated;
        _monitorService.SnapshotUpdated -= OnMonitorUpdated;
        _view.TargetInvalidated -= OnTargetInvalidated;
        RunOnUi(_view.HideOverlay);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            await _frameProvider.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
            _operations.Dispose();
            _targetCancellation?.Dispose();
            _targetCancellation = null;
        }
    }
}
