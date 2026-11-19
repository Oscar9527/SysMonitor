using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace SysMonitor.Services;

/// <summary>
/// Elevated, UI-free bridge for PresentMon. PresentMon needs ETW privileges;
/// keeping the elevation in this short-lived helper lets the main app remain
/// unelevated while preserving a redirected CSV stream and a hidden window.
/// </summary>
internal static class PresentMonHelperHost
{
    private const string HelperArgument = "--presentmon-helper";
    private const string PipePrefix = "SysMonitor.PresentMon.";

    internal static bool TryGetRequest(
        IReadOnlyList<string> arguments,
        out string pipeName,
        out int processId,
        out string sessionName)
    {
        pipeName = string.Empty;
        processId = 0;
        sessionName = string.Empty;
        if (arguments.Count != 4 ||
            !string.Equals(arguments[0], HelperArgument, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out processId) ||
            processId <= 0)
        {
            return false;
        }

        pipeName = arguments[1];
        sessionName = arguments[3];
        string suffix = pipeName.StartsWith(PipePrefix, StringComparison.Ordinal)
            ? pipeName[PipePrefix.Length..]
            : string.Empty;
        return suffix.Length == 32 &&
               Guid.TryParseExact(suffix, "N", out _) &&
               PresentMonSessionState.IsOwnedName(sessionName);
    }

    internal static async Task RunAsync(
        string pipeName,
        int processId,
        string sessionName)
    {
        BandDiagnostics.Log($"presentmon helper connecting pipe={pipeName} targetPid={processId}");
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        // A helper that cannot connect promptly is stale; terminate only the
        // process/job/session owned by this request and let the caller retry.
        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(connectionTimeout.Token).ConfigureAwait(false);
        BandDiagnostics.Log($"presentmon helper pipe connected targetPid={processId}");

        using var extractionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string executablePath = await PresentMonBinaryManager
            .GetExecutablePathAsync(extractionTimeout.Token)
            .ConfigureAwait(false);
        // This helper is elevated while the desktop app deliberately is not.
        // Owning the collector job here guarantees that an elevated PresentMon
        // process is released if the pipe closes or the helper exits unexpectedly.
        using var collectorJob = new ChildProcessJob();
        using var watcherCts = new CancellationTokenSource();
        Task? disconnectWatcher = null;
        Task? terminateTask = null;
        object terminateGate = new();

        Task EnsureSessionTerminatedAsync()
        {
            lock (terminateGate)
            {
                return terminateTask ??= TerminateOwnedSessionAsync(executablePath, sessionName);
            }
        }

        Process? collector = null;
        try
        {
            collector = Process.Start(PresentMonProcessSupport.CreateCollectorStartInfo(
                executablePath,
                processId,
                sessionName,
                requestElevation: false));
            if (collector is null)
            {
                return;
            }
            collectorJob.Assign(collector);
            BandDiagnostics.Log($"presentmon collector started pid={collector.Id} targetPid={processId}");

            disconnectWatcher = WatchParentDisconnectAsync(
                pipe,
                collector,
                processId,
                EnsureSessionTerminatedAsync,
                watcherCts.Token);

            Task<string> stderr = PresentMonProcessSupport.CaptureBoundedAsync(
                collector.StandardError,
                CancellationToken.None);
            using var reader = collector.StandardOutput;
            try
            {
                using var writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4096,
                    leaveOpen: true)
                {
                    AutoFlush = true,
                };
                while (true)
                {
                    string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }

                await collector.WaitForExitAsync().ConfigureAwait(false);
                string diagnostic = await stderr.ConfigureAwait(false);
                if (collector.ExitCode != 0)
                {
                    await writer.WriteLineAsync($"#SYSMONITOR-ERROR {collector.ExitCode} {diagnostic.ReplaceLineEndings(" ")}")
                        .ConfigureAwait(false);
                }
                BandDiagnostics.Log($"presentmon collector exited code={collector.ExitCode} targetPid={processId} stderr={diagnostic}");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
        finally
        {
            watcherCts.Cancel();
            if (disconnectWatcher is not null)
            {
                try
                {
                    await disconnectWatcher.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (collector is { HasExited: false })
            {
                await EnsureSessionTerminatedAsync().ConfigureAwait(false);
                try
                {
                    await collector.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(2))
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }

            try
            {
                if (collector is { HasExited: false })
                {
                    collector.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            collector?.Dispose();
        }
    }

    private static async Task WatchParentDisconnectAsync(
        NamedPipeClientStream pipe,
        Process collector,
        int processId,
        Func<Task> ensureSessionTerminatedAsync,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await pipe.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        BandDiagnostics.Log($"presentmon helper detected parent disconnect targetPid={processId}");
        await ensureSessionTerminatedAsync().ConfigureAwait(false);
        try
        {
            if (!collector.HasExited)
            {
                await collector.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
        }

        try
        {
            if (!collector.HasExited)
            {
                collector.Kill(entireProcessTree: true);
            }
        }
        catch
        {
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

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
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
            try
            {
                if (terminator is { HasExited: false })
                {
                    terminator.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
        finally
        {
            terminator?.Dispose();
        }
    }

}
