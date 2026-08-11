using SysMonitor.Models;

namespace SysMonitor.Services;

internal sealed class AdaptiveFrameRateProvider : IFrameRateProvider
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FallbackDelay = TimeSpan.FromSeconds(1);
    private readonly IRtssFrameSource _rtss;
    private readonly IFrameRateProvider _fallback;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _publishLock = new();
    private FrameRateSnapshot _latest = FrameRateSnapshot.Disabled;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private int _targetProcessId;
    private int _generation;
    private bool _fallbackEnabled;
    private bool _disposed;

    internal AdaptiveFrameRateProvider()
        : this(new RtssSharedMemoryReader(), new PresentMonFrameRateProvider())
    {
    }

    internal AdaptiveFrameRateProvider(IRtssFrameSource rtss, IFrameRateProvider fallback)
    {
        _rtss = rtss ?? throw new ArgumentNullException(nameof(rtss));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _fallback.SnapshotUpdated += OnFallbackSnapshotUpdated;
    }

    public FrameRateSnapshot Latest => Volatile.Read(ref _latest);

    public event EventHandler<FrameRateSnapshot>? SnapshotUpdated;

    public async Task StartAsync(int processId, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { IsCompleted: false } && _targetProcessId == processId)
            {
                return;
            }

            await StopCoreAsync(publishDisabled: false).ConfigureAwait(false);
            int generation = unchecked(++_generation);
            _targetProcessId = processId;
            if (processId <= 0)
            {
                Publish(new FrameRateSnapshot(
                    null,
                    FrameRateStatus.NoTarget,
                    null,
                    DateTimeOffset.UtcNow,
                    "No frame-rate target process."));
                return;
            }

            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Publish(new FrameRateSnapshot(
                null,
                FrameRateStatus.Starting,
                processId,
                DateTimeOffset.UtcNow,
                "Waiting for RTSS shared memory; PresentMon fallback starts after one second."));
            _worker = PollAsync(generation, processId, _cancellation.Token);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(publishDisabled: true).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopCoreAsync(publishDisabled: true).ConfigureAwait(false);
            _fallback.SnapshotUpdated -= OnFallbackSnapshotUpdated;
            await _fallback.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }

        _lifecycle.Dispose();
    }

    private async Task StopCoreAsync(bool publishDisabled)
    {
        unchecked
        {
            _generation++;
        }

        _fallbackEnabled = false;
        _cancellation?.Cancel();
        Task? worker = _worker;
        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _fallback.StopAsync().ConfigureAwait(false);
        _worker = null;
        _cancellation?.Dispose();
        _cancellation = null;
        _targetProcessId = 0;
        if (publishDisabled)
        {
            Publish(FrameRateSnapshot.Disabled with { SampledAt = DateTimeOffset.UtcNow });
        }
    }

    private async Task PollAsync(int generation, int processId, CancellationToken cancellationToken)
    {
        DateTimeOffset? unavailableSince = DateTimeOffset.UtcNow;
        bool fallbackStarted = false;
        bool rtssActive = false;
        int recoverySamples = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   Volatile.Read(ref _generation) == generation)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                SharedMemoryValue result = _rtss.Read(processId);
                if (result.Value is double fps)
                {
                    recoverySamples++;
                    bool recoveryConfirmed = !fallbackStarted || recoverySamples >= 2;
                    if (recoveryConfirmed)
                    {
                        rtssActive = true;
                        unavailableSince = null;
                        PublishIfCurrent(generation, new FrameRateSnapshot(
                            fps,
                            FrameRateStatus.Active,
                            processId,
                            now,
                            result.Reason,
                            FrameRateSource.RtssSharedMemory));
                        if (fallbackStarted)
                        {
                            _fallbackEnabled = false;
                            await _fallback.StopAsync().ConfigureAwait(false);
                            fallbackStarted = false;
                        }
                    }
                }
                else
                {
                    recoverySamples = 0;
                    unavailableSince ??= now;
                    if (rtssActive && now - unavailableSince.Value < FallbackDelay)
                    {
                        await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    rtssActive = false;
                    if (now - unavailableSince.Value >= FallbackDelay && !fallbackStarted)
                    {
                        _fallbackEnabled = true;
                        fallbackStarted = true;
                        await _fallback.StartAsync(processId, cancellationToken).ConfigureAwait(false);
                    }

                    if (!fallbackStarted)
                    {
                        PublishIfCurrent(generation, new FrameRateSnapshot(
                            null,
                            FrameRateStatus.WaitingForFrames,
                            processId,
                            now,
                            result.Reason));
                    }
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishIfCurrent(generation, new FrameRateSnapshot(
                null,
                FrameRateStatus.ProviderExited,
                processId,
                DateTimeOffset.UtcNow,
                $"Adaptive frame provider failed ({exception.GetType().Name})."));
        }
        finally
        {
            _fallbackEnabled = false;
            if (fallbackStarted)
            {
                await _fallback.StopAsync().ConfigureAwait(false);
            }
        }
    }

    private void OnFallbackSnapshotUpdated(object? sender, FrameRateSnapshot snapshot)
    {
        int generation = Volatile.Read(ref _generation);
        if (!_fallbackEnabled || snapshot.TargetProcessId != _targetProcessId)
        {
            return;
        }

        PublishIfCurrent(generation, snapshot with
        {
            Source = FrameRateSource.PresentMon,
            Detail = string.IsNullOrWhiteSpace(snapshot.Detail)
                ? "PresentMon fallback"
                : $"PresentMon fallback: {snapshot.Detail}"
        });
    }

    private void PublishIfCurrent(int generation, FrameRateSnapshot snapshot)
    {
        if (Volatile.Read(ref _generation) == generation)
        {
            Publish(snapshot);
        }
    }

    private void Publish(FrameRateSnapshot snapshot)
    {
        EventHandler<FrameRateSnapshot>? handlers;
        lock (_publishLock)
        {
            FrameRateSnapshot previous = Volatile.Read(ref _latest);
            if (snapshot.Status == previous.Status &&
                snapshot.TargetProcessId == previous.TargetProcessId &&
                snapshot.Source == previous.Source &&
                Nullable.Equals(snapshot.PresentFps, previous.PresentFps) &&
                string.Equals(snapshot.Detail, previous.Detail, StringComparison.Ordinal))
            {
                return;
            }

            Volatile.Write(ref _latest, snapshot);
            handlers = SnapshotUpdated;
        }

        try
        {
            handlers?.Invoke(this, snapshot);
        }
        catch (Exception exception)
        {
            BandDiagnostics.LogRateLimited(
                "adaptive-frame-snapshot-subscriber",
                $"adaptive frame subscriber error={exception.GetType().Name}",
                TimeSpan.FromSeconds(30));
        }
    }
}
