using System.Diagnostics;
using System.Globalization;
using System.IO;
using SysMonitor.Models;

namespace SysMonitor.Services;

internal sealed class GpuStreamReader : IAsyncDisposable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan OutputTimeout = TimeSpan.FromSeconds(4);
    private static readonly int[] RetrySeconds = { 1, 2, 4, 8, 15 };

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private Process? _process;
    private PublishedGpu? _latest;
    private bool _disposed;

    internal GpuSnapshot? LatestGpu
    {
        get
        {
            PublishedGpu? published = Volatile.Read(ref _latest);
            return published is not null &&
                   Stopwatch.GetElapsedTime(published.Timestamp) <= StaleAfter
                ? published.Snapshot
                : null;
        }
    }

    internal Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            _cancellation?.Dispose();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _worker = Task.Run(() => RunAsync(_cancellation.Token), CancellationToken.None);
            return Task.CompletedTask;
        }
    }

    internal async Task StopAsync()
    {
        Task? worker;
        lock (_lifecycleLock)
        {
            _cancellation?.Cancel();
            TryTerminate(_process);
            worker = _worker;
        }

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

        lock (_lifecycleLock)
        {
            _worker = null;
            _cancellation?.Dispose();
            _cancellation = null;
            _process = null;
            Volatile.Write(ref _latest, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        int retryIndex = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            bool receivedValidOutput = false;
            Process? process = null;
            try
            {
                process = StartProcess();
                lock (_lifecycleLock)
                {
                    _process = process;
                }

                receivedValidOutput = await ReadSessionAsync(process, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A missing/failed nvidia-smi is retried with bounded backoff.
            }
            finally
            {
                TryTerminate(process);
                process?.Dispose();
                lock (_lifecycleLock)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                    }
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (receivedValidOutput)
            {
                retryIndex = 0;
            }

            int seconds = RetrySeconds[retryIndex];
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!receivedValidOutput)
            {
                retryIndex = Math.Min(retryIndex + 1, RetrySeconds.Length - 1);
            }
        }
    }

    private async Task<bool> ReadSessionAsync(Process process, CancellationToken cancellationToken)
    {
        Task stderrDrain = DrainStderrAsync(process.StandardError);
        var round = new Dictionary<int, GpuSnapshot>();
        HashSet<int>? expectedIndices = null;
        bool published = false;
        int consecutiveIncompleteRounds = 0;
        int consecutiveInvalidLines = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Task<string?> readTask = process.StandardOutput.ReadLineAsync();
                string? line = await readTask.WaitAsync(OutputTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!TryParseSnapshot(line, out GpuSnapshot snapshot))
                {
                    consecutiveInvalidLines++;
                    if (consecutiveInvalidLines >= 3)
                    {
                        throw new InvalidDataException("nvidia-smi repeatedly returned invalid GPU samples.");
                    }

                    continue;
                }

                consecutiveInvalidLines = 0;
                int index = snapshot.Index;
                if (expectedIndices is null)
                {
                    if (round.ContainsKey(index))
                    {
                        expectedIndices = round.Keys.ToHashSet();
                        PublishRound(round);
                        published = true;
                        round.Clear();
                    }

                    round[index] = snapshot;
                    if (expectedIndices?.Count == 1)
                    {
                        PublishRound(round);
                        published = true;
                        round.Clear();
                    }

                    continue;
                }

                if (!expectedIndices.Contains(index) || round.ContainsKey(index))
                {
                    consecutiveIncompleteRounds++;
                    round.Clear();
                    if (consecutiveIncompleteRounds >= 3)
                    {
                        throw new InvalidDataException("nvidia-smi repeatedly returned incomplete GPU rounds.");
                    }
                }

                if (!expectedIndices.Contains(index))
                {
                    continue;
                }

                round[index] = snapshot;
                if (expectedIndices.All(round.ContainsKey))
                {
                    PublishRound(round);
                    published = true;
                    consecutiveIncompleteRounds = 0;
                    round.Clear();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Timeout, process exit races, and incomplete rounds all trigger a restart.
        }
        finally
        {
            TryTerminate(process);
            try
            {
                await stderrDrain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        return published;
    }

    private void PublishRound(Dictionary<int, GpuSnapshot> round)
    {
        if (round.Count == 0)
        {
            return;
        }

        GpuSnapshot selected = round.Values
            .OrderByDescending(snapshot => snapshot.UsagePercent)
            .ThenBy(snapshot => snapshot.Index)
            .First();
        Volatile.Write(ref _latest, new PublishedGpu(selected, Stopwatch.GetTimestamp()));
    }

    private static Process StartProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--query-gpu=index,name,utilization.gpu,temperature.gpu,memory.used,memory.total");
        startInfo.ArgumentList.Add("--format=csv,noheader,nounits");
        startInfo.ArgumentList.Add("--loop=1");

        return Process.Start(startInfo) ??
               throw new InvalidOperationException("Unable to start nvidia-smi.");
    }

    private static async Task DrainStderrAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        }
        catch
        {
            // Process shutdown can close the redirected stream mid-read.
        }
    }

    private static bool TryParseSnapshot(string line, out GpuSnapshot snapshot)
    {
        snapshot = null!;
        List<string> fields = ParseCsv(line);
        if (fields.Count != 6 ||
            !int.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
            !double.TryParse(fields[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double usage) ||
            !double.TryParse(fields[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double temperature) ||
            !double.TryParse(fields[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double memoryUsedMiB) ||
            !double.TryParse(fields[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double memoryTotalMiB) ||
            !double.IsFinite(usage) ||
            !double.IsFinite(temperature) ||
            !double.IsFinite(memoryUsedMiB) ||
            !double.IsFinite(memoryTotalMiB))
        {
            return false;
        }

        const double bytesPerMiB = 1024d * 1024d;
        snapshot = new GpuSnapshot(
            index,
            fields[1].Trim(),
            Math.Clamp(usage, 0, 100),
            temperature,
            ToNonNegativeInt64(memoryUsedMiB * bytesPerMiB),
            ToNonNegativeInt64(memoryTotalMiB * bytesPerMiB),
            DateTimeOffset.UtcNow);
        return true;
    }

    private static List<string> ParseCsv(string line)
    {
        var fields = new List<string>(6);
        var field = new System.Text.StringBuilder();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char current = line[i];
            if (current == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (current == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(current);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }

    private static long ToNonNegativeInt64(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return 0;
        }

        return value >= long.MaxValue ? long.MaxValue : (long)Math.Round(value);
    }

    private static void TryTerminate(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private sealed record PublishedGpu(GpuSnapshot Snapshot, long Timestamp);
}
