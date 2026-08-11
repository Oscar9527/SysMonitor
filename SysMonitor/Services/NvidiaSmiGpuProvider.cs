using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace SysMonitor.Services;

internal readonly record struct NvidiaSmiRow(
    string TimestampKey,
    DateTimeOffset SampledAt,
    int Index,
    string Name,
    string? Uuid,
    string? PciBusId,
    double? UsagePercent,
    double? TemperatureCelsius,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? CoreClockMhz,
    double? MemoryClockMhz);

internal static class NvidiaSmiCsv
{
    internal static bool TryParseRow(string line, out NvidiaSmiRow row)
    {
        row = default;
        List<string> fields = Parse(line);
        if (fields.Count != 11)
        {
            return false;
        }

        string timestampKey = fields[0].Trim();
        if (!DateTimeOffset.TryParse(
                timestampKey,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out DateTimeOffset sampledAt) ||
            !int.TryParse(fields[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
            index < 0)
        {
            return false;
        }

        double? usage = ParseMetric(fields[5]);
        if (usage is not null)
        {
            usage = Math.Clamp(usage.Value, 0d, 100d);
        }

        double? temperature = ParseMetric(fields[6]);
        if (temperature is not (>= 1d and <= 150d))
        {
            temperature = null;
        }

        string? uuid = ParseIdentity(fields[2]);
        string? pciBusId = ParseIdentity(fields[3]);
        string name = fields[4].Trim();
        if (string.IsNullOrWhiteSpace(name) || IsUnavailable(name))
        {
            name = "NVIDIA graphics adapter";
        }

        row = new NvidiaSmiRow(
            timestampKey,
            sampledAt,
            index,
            name,
            uuid,
            pciBusId,
            usage,
            temperature,
            GpuSensorSelector.MiBToBytes(ParseMetric(fields[7])),
            GpuSensorSelector.MiBToBytes(ParseMetric(fields[8])),
            PositiveMetric(fields[9]),
            PositiveMetric(fields[10]));
        return true;
    }

    internal static List<string> Parse(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int index = 0; index < line.Length; index++)
        {
            char current = line[index];
            if (current == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
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

        if (quoted)
        {
            return new List<string>();
        }

        fields.Add(field.ToString());
        return fields;
    }

    private static double? ParseMetric(string value)
    {
        string trimmed = value.Trim();
        return !IsUnavailable(trimmed) &&
               double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
               double.IsFinite(parsed)
            ? parsed
            : null;
    }

    private static double? PositiveMetric(string value)
    {
        double? metric = ParseMetric(value);
        return metric is > 0d ? metric : null;
    }

    private static string? ParseIdentity(string value)
    {
        string trimmed = value.Trim();
        return string.IsNullOrEmpty(trimmed) || IsUnavailable(trimmed)
            ? null
            : trimmed.ToUpperInvariant();
    }

    private static bool IsUnavailable(string value) =>
        value.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("[N/A]", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("NA", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("NOT SUPPORTED", StringComparison.OrdinalIgnoreCase);
}

internal sealed class NvidiaSmiCycleAccumulator
{
    private readonly Dictionary<int, NvidiaSmiRow> _rows = new();
    private string? _timestampKey;
    private DateTimeOffset _sampledAt;
    private long _monotonicTimestamp;

    internal bool PushLine(
        string line,
        long monotonicTimestamp,
        out GpuProviderCycle? completedCycle)
    {
        completedCycle = null;
        if (!NvidiaSmiCsv.TryParseRow(line, out NvidiaSmiRow row))
        {
            return false;
        }

        if (_timestampKey is not null &&
            !string.Equals(_timestampKey, row.TimestampKey, StringComparison.Ordinal))
        {
            completedCycle = BuildCycle();
            _rows.Clear();
            _timestampKey = null;
        }

        if (_timestampKey is null)
        {
            _timestampKey = row.TimestampKey;
            _sampledAt = row.SampledAt;
            _monotonicTimestamp = monotonicTimestamp;
        }

        if (_rows.ContainsKey(row.Index))
        {
            return false;
        }

        _rows.Add(row.Index, row);
        return true;
    }

    private GpuProviderCycle? BuildCycle()
    {
        if (_rows.Count == 0)
        {
            return null;
        }

        GpuProviderSample[] samples = _rows.Values
            .OrderBy(row => row.Index)
            .Select(row => new GpuProviderSample(
                StableId(row),
                row.Index,
                row.Name,
                GpuVendor.Nvidia,
                GpuTelemetrySource.NvidiaSmi,
                null,
                null,
                row.PciBusId,
                row.Uuid,
                row.UsagePercent,
                row.TemperatureCelsius,
                row.MemoryUsedBytes,
                row.MemoryTotalBytes,
                row.SampledAt,
                _monotonicTimestamp)
            {
                CoreClockMhz = row.CoreClockMhz,
                MemoryClockMhz = row.MemoryClockMhz,
            })
            .ToArray();
        return new GpuProviderCycle(
            GpuTelemetrySource.NvidiaSmi,
            _sampledAt,
            _monotonicTimestamp,
            samples);
    }

    private static string StableId(NvidiaSmiRow row)
    {
        string identity = row.Uuid ?? row.PciBusId ?? row.Index.ToString(CultureInfo.InvariantCulture);
        return $"nvidia-smi:{identity}";
    }
}

internal sealed class NvidiaSmiGpuProvider : IGpuTelemetryProvider
{
    private static readonly TimeSpan OutputTimeout = TimeSpan.FromSeconds(4);
    private static readonly int[] RetrySeconds = { 1, 2, 4, 8, 15, 30 };
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private Process? _process;
    private GpuProviderCycle? _latestCycle;
    private bool _disposed;

    public GpuProviderCycle? LatestCycle => Volatile.Read(ref _latestCycle);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { IsCompleted: false })
            {
                return;
            }

            _cancellation?.Dispose();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _worker = Task.Run(() => RunAsync(_cancellation.Token), CancellationToken.None);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        Task? worker;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _cancellation?.Cancel();
            TryTerminate(_process);
            worker = _worker;
        }
        finally
        {
            _lifecycle.Release();
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

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(worker, _worker))
            {
                _worker = null;
                _process = null;
                _cancellation?.Dispose();
                _cancellation = null;
                Volatile.Write(ref _latestCycle, null);
            }
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
        }
        finally
        {
            _lifecycle.Release();
        }

        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        int retryIndex = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            bool published = false;
            Process? process = null;
            try
            {
                process = StartProcess();
                _process = process;
                published = await ReadSessionAsync(process, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                BandDiagnostics.LogRateLimited(
                    "gpu-nvidia-smi-worker",
                    $"gpu source=nvidia-smi error={exception.GetType().Name}",
                    TimeSpan.FromSeconds(30));
            }
            finally
            {
                TryTerminate(process);
                process?.Dispose();
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (published)
            {
                retryIndex = 0;
            }

            int delay = RetrySeconds[retryIndex];
            if (!published)
            {
                retryIndex = Math.Min(retryIndex + 1, RetrySeconds.Length - 1);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> ReadSessionAsync(Process process, CancellationToken cancellationToken)
    {
        Task stderrDrain = DrainStderrAsync(process.StandardError);
        var accumulator = new NvidiaSmiCycleAccumulator();
        int consecutiveCorruptRows = 0;
        bool published = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await process.StandardOutput.ReadLineAsync()
                    .WaitAsync(OutputTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    throw new EndOfStreamException("nvidia-smi output ended.");
                }

                if (!accumulator.PushLine(
                        line,
                        GpuMonotonicClock.GetTimestamp(),
                        out GpuProviderCycle? completedCycle))
                {
                    consecutiveCorruptRows++;
                    BandDiagnostics.LogRateLimited(
                        "gpu-nvidia-smi-row",
                        "gpu source=nvidia-smi corrupt-row",
                        TimeSpan.FromSeconds(30));
                    if (consecutiveCorruptRows >= 3)
                    {
                        throw new InvalidDataException("Repeated corrupt nvidia-smi rows.");
                    }

                    continue;
                }

                consecutiveCorruptRows = 0;
                if (completedCycle is not null)
                {
                    Volatile.Write(ref _latestCycle, completedCycle);
                    published = true;
                }
            }
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
        startInfo.ArgumentList.Add("--query-gpu=timestamp,index,uuid,pci.bus_id,name,utilization.gpu,temperature.gpu,memory.used,memory.total,clocks.current.graphics,clocks.current.memory");
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
        }
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
}
