using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
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
    private CancellationTokenSource? _readCancellation;
    private Task? _worker;
    private string? _executablePath;
    private string? _sessionName;
    private NamedPipeServerStream? _collectorPipe;
    private int _generation;
    private bool _disposed;

    public FrameRateSnapshot Latest => Volatile.Read(ref _latest);

    public event EventHandler<FrameRateSnapshot>? SnapshotUpdated;

    public async Task StartAsync(int processId, CancellationToken cancellationToken = default)
    {
        BandDiagnostics.Log($"presentmon start targetPid={processId}");
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
            string pipeName = $"SysMonitor.PresentMon.{Guid.NewGuid():N}";
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
            Process? collector = null;
            try
            {
                string corePath = Environment.ProcessPath ??
                    throw new InvalidOperationException("SysMonitor core path is unavailable.");
                collector = Process.Start(PresentMonProcessSupport.CreateElevatedHelperStartInfo(
                    corePath,
                    pipeName,
                    processId,
                    sessionName)) ?? throw new InvalidOperationException("PresentMon helper did not start.");
            }
            catch (Exception exception)
            {
                pipe.Dispose();
                if (collector is not null)
                {
                    await TerminateOwnedSessionAsync(executablePath, sessionName).ConfigureAwait(false);
                    if (!await WaitForExitAsync(collector, TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                    {
                        TryKill(collector);
                    }
                }

                collector?.Dispose();
                PresentMonSessionState.Clear(sessionName);
                Publish(new FrameRateSnapshot(
                    null,
                    FrameRateStatus.ProviderExited,
                    processId,
                    DateTimeOffset.UtcNow,
                    $"PresentMon helper failed to start ({exception.GetType().Name})."));
                return;
            }

            _collectorPipe = pipe;
            _collector = collector;
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
                pipe,
                _readCancellation.Token);
            BandDiagnostics.Log($"presentmon helper launched pid={collector.Id} targetPid={processId} session={sessionName}");
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
        NamedPipeServerStream? pipe = _collectorPipe;
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

        readCancellation?.Cancel();
        pipe?.Dispose();

        if (executablePath is not null && sessionName is not null)
        {
            await TerminateOwnedSessionAsync(executablePath, sessionName).ConfigureAwait(false);
        }

        bool exited = await WaitForExitAsync(collector, CollectorExitTimeout).ConfigureAwait(false);
        if (!exited)
        {
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

        readCancellation?.Dispose();
        collector?.Dispose();
        pipe?.Dispose();
        _collector = null;
        _collectorPipe = null;
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
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        var reader = new PresentMonBoundedLineReader(new StreamReader(
            pipe,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true));
        var aggregator = new FrameRateAggregator();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        bool headerReceived = false;
        FrameRateStatus? terminalStatus = null;
        string? detail = null;
        try
        {
            using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectionTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await pipe.WaitForConnectionAsync(connectionTimeout.Token).ConfigureAwait(false);
            BandDiagnostics.Log($"presentmon pipe connected targetPid={processId}");
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
                    if (TryClassifyHelperError(line, out FrameRateStatus helperStatus, out string? helperDetail))
                    {
                        terminalStatus = helperStatus;
                        detail = helperDetail;
                        break;
                    }

                    if (!PresentMonCsvParser.IsExpectedHeader(line))
                    {
                        terminalStatus = FrameRateStatus.IncompatibleOutput;
                        detail = "PresentMon emitted an unexpected CSV header.";
                        break;
                    }

                    headerReceived = true;
                    BandDiagnostics.Log($"presentmon csv header accepted targetPid={processId}");
                    lineTask = reader.ReadLineAsync(cancellationToken);
                    continue;
                }

                if (TryClassifyHelperError(line, out FrameRateStatus streamErrorStatus, out string? streamErrorDetail))
                {
                    terminalStatus = streamErrorStatus;
                    detail = streamErrorDetail;
                    break;
                }

                if (!PresentMonCsvParser.TryParseFrame(line, processId, out PresentMonFrame frame))
                {
                    terminalStatus = FrameRateStatus.IncompatibleOutput;
                    detail = "PresentMon emitted an invalid CSV row.";
                    break;
                }

                if (!aggregator.Add(frame, DateTimeOffset.UtcNow))
                {
                    lineTask = reader.ReadLineAsync(cancellationToken);
                    continue;
                }

                DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
                double? currentFps = aggregator.Read(receivedAt);
                PublishIfCurrent(generation, new FrameRateSnapshot(
                    currentFps,
                    currentFps is null ? FrameRateStatus.WaitingForFrames : FrameRateStatus.Active,
                    processId,
                    receivedAt), throttle: currentFps is not null);
                if (currentFps is double loggedFps)
                {
                    BandDiagnostics.LogRateLimited(
                        $"presentmon-active-{processId}",
                        $"presentmon active targetPid={processId} fps={loggedFps:0.0}",
                        TimeSpan.FromSeconds(10));
                }
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

        if (terminalStatus is null)
        {
            (terminalStatus, detail) = ClassifyExit(
                headerReceived,
                aggregator.HasReceivedFrames,
                SafeExitCode(collector),
                string.Empty);
        }

        PublishIfCurrent(generation, new FrameRateSnapshot(
            null,
            terminalStatus.Value,
            processId,
            DateTimeOffset.UtcNow,
            detail));
        BandDiagnostics.Log($"presentmon stopped targetPid={processId} status={terminalStatus} detail={detail}");
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
            BandDiagnostics.LogRateLimited(
                $"presentmon-status-{snapshot.TargetProcessId}-{snapshot.Status}",
                $"presentmon publish targetPid={snapshot.TargetProcessId} status={snapshot.Status} fps={snapshot.PresentFps:0.0} detail={snapshot.Detail}",
                TimeSpan.FromSeconds(10));
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
            if (exitCode == 6)
            {
                return (
                    FrameRateStatus.ProviderExited,
                    "PresentMon could not start its ETW collector (code 6).");
            }

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

    private static bool TryClassifyHelperError(
        string line,
        out FrameRateStatus status,
        out string detail)
    {
        const string prefix = "#SYSMONITOR-ERROR ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            status = default;
            detail = string.Empty;
            return false;
        }

        string diagnostic = line[prefix.Length..];
        if (diagnostic.Contains("1450", StringComparison.Ordinal))
        {
            status = FrameRateStatus.ProviderExited;
            detail = "Windows ETW resources are unavailable (1450). A stale elevated PresentMon or another recorder may still be running.";
            return true;
        }

        if (diagnostic.Contains("TRACE SESSION", StringComparison.OrdinalIgnoreCase))
        {
            status = FrameRateStatus.SessionConflict;
            detail = "The PresentMon ETW session name is already in use.";
            return true;
        }

        if (diagnostic.Contains("ACCESS DENIED", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("0X80070005", StringComparison.OrdinalIgnoreCase))
        {
            status = FrameRateStatus.PermissionDenied;
            detail = "PresentMon was denied access to the trace session.";
            return true;
        }

        status = FrameRateStatus.ProviderExited;
        detail = $"PresentMon collector failed: {diagnostic}";
        return true;
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
