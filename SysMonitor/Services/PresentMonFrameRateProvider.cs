using System.Diagnostics;
using System.IO;
using SysMonitor.Models;

namespace SysMonitor.Services;

internal interface IFrameRateProvider : IAsyncDisposable
{
    FrameRateSnapshot Latest { get; }
    event EventHandler<FrameRateSnapshot>? SnapshotUpdated;
    Task StartAsync(int processId, CancellationToken cancellationToken = default);
    Task StopAsync();
}

internal sealed class PresentMonFrameRateProvider : IFrameRateProvider
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StopCommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CollectorExitTimeout = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _publishLock = new();
    private FrameRateSnapshot _latest = FrameRateSnapshot.Disabled;
    private DateTimeOffset _lastTelemetryPublishedAt = DateTimeOffset.MinValue;
    private Process? _collector;
    private ChildProcessJob? _collectorJob;
    private CancellationTokenSource? _readCancellation;
    private Task? _worker;
    private string? _executablePath;
    private string? _sessionName;
    private int _generation;
    private bool _disposed;

    public FrameRateSnapshot Latest => Volatile.Read(ref _latest);

    public event EventHandler<FrameRateSnapshot>? SnapshotUpdated;

    public async Task StartAsync(int processId, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_collector is { HasExited: false } && Latest.TargetProcessId == processId)
            {
                return;
            }

            await StopCoreAsync(publishDisabled: false).ConfigureAwait(false);
            if (!IsLiveProcess(processId))
            {
                Publish(new FrameRateSnapshot(
                    null,
                    FrameRateStatus.NoTarget,
                    processId > 0 ? processId : null,
                    DateTimeOffset.UtcNow));
                return;
            }

            int generation = unchecked(++_generation);
            Publish(new FrameRateSnapshot(
                null,
                FrameRateStatus.Starting,
                processId,
                DateTimeOffset.UtcNow));
            string executablePath;
            try
            {
                executablePath = await PresentMonBinaryManager
                    .GetExecutablePathAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Publish(new FrameRateSnapshot(
                    null,
                    FrameRateStatus.ProviderExited,
                    processId,
                    DateTimeOffset.UtcNow,
                    $"PresentMon runtime unavailable ({exception.GetType().Name})."));
                return;
            }

            string? staleSession = PresentMonSessionState.ReadOwnedSession();
            if (staleSession is not null)
            {
                await TerminateOwnedSessionAsync(executablePath, staleSession).ConfigureAwait(false);
                PresentMonSessionState.Clear(staleSession);
            }

            string sessionName = $"SysMonitor-{Environment.ProcessId}-{Guid.NewGuid():N}";
            PresentMonSessionState.Register(sessionName);
            var job = new ChildProcessJob();
            Process? collector = null;
            try
            {
                collector = Process.Start(PresentMonProcessSupport.CreateCollectorStartInfo(
                    executablePath,
                    processId,
                    sessionName)) ?? throw new InvalidOperationException("PresentMon did not start.");
                job.Assign(collector);
            }
            catch (Exception exception)
            {
                if (collector is not null)
                {
                    await TerminateOwnedSessionAsync(executablePath, sessionName).ConfigureAwait(false);
                    if (!await WaitForExitAsync(collector, TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                    {
                        TryKill(collector);
                    }
                }

                collector?.Dispose();
                job.Dispose();
                PresentMonSessionState.Clear(sessionName);
                Publish(new FrameRateSnapshot(
                    null,
                    FrameRateStatus.ProviderExited,
                    processId,
                    DateTimeOffset.UtcNow,
                    $"PresentMon failed to start ({exception.GetType().Name})."));
                return;
            }

            _collector = collector;
            _collectorJob = job;
            _readCancellation = new CancellationTokenSource();
            _executablePath = executablePath;
            _sessionName = sessionName;
            Publish(new FrameRateSnapshot(
                null,
                FrameRateStatus.WaitingForFrames,
                processId,
                DateTimeOffset.UtcNow));
            _worker = CollectAsync(
                generation,
                processId,
                executablePath,
                sessionName,
                collector,
                _readCancellation.Token);
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
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopCoreAsync(bool publishDisabled)
    {
        Process? collector = _collector;
        Task? worker = _worker;
        ChildProcessJob? job = _collectorJob;
        CancellationTokenSource? readCancellation = _readCancellation;
        string? executablePath = _executablePath;
        string? sessionName = _sessionName;
        if (collector is null && worker is null && sessionName is null)
        {
            if (publishDisabled)
            {
                Publish(FrameRateSnapshot.Disabled with { SampledAt = DateTimeOffset.UtcNow });
            }

            return;
        }

        unchecked
        {
            _generation++;
        }

        Publish(new FrameRateSnapshot(
            null,
            FrameRateStatus.Stopping,
            Latest.TargetProcessId,
            DateTimeOffset.UtcNow));

        if (executablePath is not null && sessionName is not null)
        {
            await TerminateOwnedSessionAsync(executablePath, sessionName).ConfigureAwait(false);
        }

        bool exited = await WaitForExitAsync(collector, CollectorExitTimeout).ConfigureAwait(false);
        if (!exited)
        {
            job?.Dispose();
            job = null;
            readCancellation?.Cancel();
            await WaitForExitAsync(collector, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        if (worker is not null)
        {
            try
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                readCancellation?.Cancel();
            }
        }

        if (executablePath is not null && sessionName is not null)
        {
            await TerminateOwnedSessionAsync(executablePath, sessionName).ConfigureAwait(false);
            PresentMonSessionState.Clear(sessionName);
        }

        job?.Dispose();
        readCancellation?.Cancel();
        readCancellation?.Dispose();
        collector?.Dispose();
        _collector = null;
        _collectorJob = null;
        _readCancellation = null;
        _worker = null;
        _executablePath = null;
        _sessionName = null;
        if (publishDisabled)
        {
            Publish(FrameRateSnapshot.Disabled with { SampledAt = DateTimeOffset.UtcNow });
        }
    }

    private async Task CollectAsync(
        int generation,
        int processId,
        string executablePath,
        string sessionName,
        Process collector,
        CancellationToken cancellationToken)
    {
        Task<string> stderrTask = PresentMonProcessSupport.CaptureBoundedAsync(
            collector.StandardError,
            CancellationToken.None);
        var reader = new PresentMonBoundedLineReader(collector.StandardOutput);
        var aggregator = new FrameRateAggregator();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        bool headerReceived = false;
        FrameRateStatus? terminalStatus = null;
        string? detail = null;
        try
        {
            Task<string?> lineTask = reader.ReadLineAsync(cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                Task completed = await Task.WhenAny(
                        lineTask,
                        Task.Delay(PollInterval, cancellationToken))
                    .ConfigureAwait(false);
                if (completed != lineTask)
                {
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    double? fps = aggregator.Read(now);
                    if (aggregator.HasReceivedFrames)
                    {
                        PublishIfCurrent(generation, new FrameRateSnapshot(
                            fps,
                            fps is null ? FrameRateStatus.Stale : FrameRateStatus.Active,
                            processId,
                            now), throttle: fps is not null);
                    }
                    else if (now - startedAt >= TimeSpan.FromSeconds(2))
                    {
                        PublishIfCurrent(generation, new FrameRateSnapshot(
                            null,
                            FrameRateStatus.NoPresentEvents,
                            processId,
                            now));
                    }

                    continue;
                }

                string? line = await lineTask.ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!headerReceived)
                {
                    if (!PresentMonCsvParser.IsExpectedHeader(line))
                    {
                        terminalStatus = FrameRateStatus.IncompatibleOutput;
                        detail = "PresentMon emitted an unexpected CSV header.";
                        break;
                    }

                    headerReceived = true;
                    lineTask = reader.ReadLineAsync(cancellationToken);
                    continue;
                }

                if (!PresentMonCsvParser.TryParseFrame(line, processId, out PresentMonFrame frame) ||
                    !aggregator.Add(frame, DateTimeOffset.UtcNow))
                {
                    terminalStatus = FrameRateStatus.IncompatibleOutput;
                    detail = "PresentMon emitted an invalid or non-monotonic CSV row.";
                    break;
                }

                DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
                double? currentFps = aggregator.Read(receivedAt);
                PublishIfCurrent(generation, new FrameRateSnapshot(
                    currentFps,
                    currentFps is null ? FrameRateStatus.WaitingForFrames : FrameRateStatus.Active,
                    processId,
                    receivedAt), throttle: currentFps is not null);
                lineTask = reader.ReadLineAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (InvalidDataException exception)
        {
            terminalStatus = FrameRateStatus.IncompatibleOutput;
            detail = exception.Message;
        }
        catch (Exception exception)
        {
            terminalStatus = FrameRateStatus.ProviderExited;
            detail = $"PresentMon output failed ({exception.GetType().Name}).";
        }

        if (terminalStatus is not null)
        {
            await TerminateOwnedSessionAsync(executablePath, sessionName).ConfigureAwait(false);
            if (!await WaitForExitAsync(collector, TimeSpan.FromSeconds(2)).ConfigureAwait(false))
            {
                TryKill(collector);
            }
        }

        try
        {
            await collector.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch
        {
        }

        string stderr;
        try
        {
            stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            stderr = string.Empty;
        }

        if (terminalStatus is null)
        {
            (terminalStatus, detail) = ClassifyExit(
                headerReceived,
                aggregator.HasReceivedFrames,
                SafeExitCode(collector),
                stderr);
        }

        PublishIfCurrent(generation, new FrameRateSnapshot(
            null,
            terminalStatus.Value,
            processId,
            DateTimeOffset.UtcNow,
            detail));
    }

    private void PublishIfCurrent(int generation, FrameRateSnapshot snapshot, bool throttle = false)
    {
        if (Volatile.Read(ref _generation) == generation)
        {
            Publish(snapshot, throttle);
        }
    }

    private void Publish(FrameRateSnapshot snapshot, bool throttle = false)
    {
        EventHandler<FrameRateSnapshot>? handler;
        lock (_publishLock)
        {
            if (throttle && snapshot.SampledAt - _lastTelemetryPublishedAt < PollInterval)
            {
                return;
            }

            FrameRateSnapshot previous = Volatile.Read(ref _latest);
            if (snapshot.Status == previous.Status &&
                snapshot.TargetProcessId == previous.TargetProcessId &&
                Nullable.Equals(snapshot.PresentFps, previous.PresentFps) &&
                string.Equals(snapshot.Detail, previous.Detail, StringComparison.Ordinal))
            {
                return;
            }

            Volatile.Write(ref _latest, snapshot);
            if (throttle)
            {
                _lastTelemetryPublishedAt = snapshot.SampledAt;
            }

            handler = SnapshotUpdated;
        }

        try
        {
            handler?.Invoke(this, snapshot);
        }
        catch (Exception exception)
        {
            BandDiagnostics.LogRateLimited(
                "presentmon-snapshot-subscriber",
                $"presentmon subscriber error={exception.GetType().Name}",
                TimeSpan.FromSeconds(30));
        }
    }

    private static async Task TerminateOwnedSessionAsync(string executablePath, string sessionName)
    {
        Process? terminator = null;
        try
        {
            terminator = Process.Start(PresentMonProcessSupport.CreateTerminateStartInfo(
                executablePath,
                sessionName));
            if (terminator is null)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(StopCommandTimeout);
            Task<string> stdout = PresentMonProcessSupport.CaptureBoundedAsync(
                terminator.StandardOutput,
                timeout.Token);
            Task<string> stderr = PresentMonProcessSupport.CaptureBoundedAsync(
                terminator.StandardError,
                timeout.Token);
            await terminator.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch
        {
            TryKill(terminator);
        }
        finally
        {
            terminator?.Dispose();
        }
    }

    private static async Task<bool> WaitForExitAsync(Process? process, TimeSpan timeout)
    {
        if (process is null)
        {
            return true;
        }

        try
        {
            if (process.HasExited)
            {
                return true;
            }

            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (FrameRateStatus Status, string? Detail) ClassifyExit(
        bool headerReceived,
        bool framesReceived,
        int? exitCode,
        string stderr)
    {
        string normalized = stderr.ToUpperInvariant();
        if (normalized.Contains("ACCESS DENIED", StringComparison.Ordinal) ||
            normalized.Contains("0X80070005", StringComparison.Ordinal) ||
            normalized.Contains("PERFORMANCE LOG USERS", StringComparison.Ordinal))
        {
            return (FrameRateStatus.PermissionDenied, "PresentMon was denied access to the trace session.");
        }

        if (normalized.Contains("ALREADY EXISTS", StringComparison.Ordinal) ||
            normalized.Contains("SESSION CONFLICT", StringComparison.Ordinal))
        {
            return (FrameRateStatus.SessionConflict, "The owned PresentMon session name was unavailable.");
        }

        if (!headerReceived)
        {
            return (FrameRateStatus.IncompatibleOutput, "PresentMon exited before its CSV header was received.");
        }

        if (!framesReceived && exitCode == 0)
        {
            return (FrameRateStatus.NoPresentEvents, "No presentation events were received for the target.");
        }

        return (
            FrameRateStatus.ProviderExited,
            exitCode is { } code
                ? $"PresentMon exited with code {code}."
                : "PresentMon exited unexpectedly.");
    }

    private static bool IsLiveProcess(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }
        }
        catch
        {
        }
    }
}
