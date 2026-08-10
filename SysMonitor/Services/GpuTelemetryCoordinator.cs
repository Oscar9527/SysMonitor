using System.Diagnostics;
using SysMonitor.Models;

namespace SysMonitor.Services;

internal sealed class GpuTelemetryCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan NvidiaSmiFreshness = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan LibreHardwareMonitorFreshness = TimeSpan.FromSeconds(3.5);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly IGpuTelemetryProvider _nvidiaSmi;
    private readonly IGpuTelemetryProvider _libreHardwareMonitor;
    private readonly Dictionary<GpuTelemetrySource, GpuProviderCycle> _acceptedCycles = new();
    private string? _currentStableId;
    private string? _challengerStableId;
    private string? _loggedSelectionKey;
    private int _challengerTicks;
    private bool _started;
    private bool _disposed;

    internal GpuTelemetryCoordinator()
        : this(new NvidiaSmiGpuProvider(), new LibreHardwareMonitorGpuProvider())
    {
    }

    internal GpuTelemetryCoordinator(
        IGpuTelemetryProvider nvidiaSmi,
        IGpuTelemetryProvider libreHardwareMonitor)
    {
        _nvidiaSmi = nvidiaSmi;
        _libreHardwareMonitor = libreHardwareMonitor;
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            await _nvidiaSmi.StartAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _libreHardwareMonitor.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _nvidiaSmi.StopAsync().ConfigureAwait(false);
                throw;
            }

            _started = true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return;
            }

            await _nvidiaSmi.StopAsync().ConfigureAwait(false);
            await _libreHardwareMonitor.StopAsync().ConfigureAwait(false);
            _acceptedCycles.Clear();
            ResetSelection();
            _started = false;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal GpuSnapshot? Read() => Read(GpuMonotonicClock.GetTimestamp());

    internal GpuSnapshot? Read(long nowTimestamp)
    {
        AcceptNewest(_nvidiaSmi.LatestCycle);
        AcceptNewest(_libreHardwareMonitor.LatestCycle);

        GpuProviderCycle? nvidiaCycle = FreshCycle(
            GpuTelemetrySource.NvidiaSmi,
            nowTimestamp,
            NvidiaSmiFreshness);
        GpuProviderCycle? lhmCycle = FreshCycle(
            GpuTelemetrySource.LibreHardwareMonitor,
            nowTimestamp,
            LibreHardwareMonitorFreshness);

        IReadOnlyList<GpuProviderSample> candidates = BuildCandidates(nvidiaCycle, lhmCycle);
        GpuProviderSample? selected = Select(candidates);
        if (selected is not null)
        {
            LogSelection(selected);
        }

        return selected is null
            ? null
            : new GpuSnapshot(
                selected.Index,
                selected.Name,
                Finite(selected.UsagePercent),
                Finite(selected.TemperatureCelsius),
                NonNegative(selected.DedicatedMemoryUsedBytes),
                NonNegative(selected.DedicatedMemoryTotalBytes),
                selected.SampledAt);
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
        await _nvidiaSmi.DisposeAsync().ConfigureAwait(false);
        await _libreHardwareMonitor.DisposeAsync().ConfigureAwait(false);
    }

    private void AcceptNewest(GpuProviderCycle? cycle)
    {
        if (cycle is null)
        {
            return;
        }

        if (_acceptedCycles.TryGetValue(cycle.Source, out GpuProviderCycle? accepted) &&
            cycle.MonotonicTimestamp <= accepted.MonotonicTimestamp)
        {
            if (cycle.MonotonicTimestamp < accepted.MonotonicTimestamp)
            {
                BandDiagnostics.LogRateLimited(
                    $"gpu-out-of-order-{cycle.Source}",
                    $"gpu source={cycle.Source} rejected=out-of-order",
                    TimeSpan.FromSeconds(30));
            }

            return;
        }

        _acceptedCycles[cycle.Source] = cycle;
    }

    private GpuProviderCycle? FreshCycle(
        GpuTelemetrySource source,
        long nowTimestamp,
        TimeSpan freshness)
    {
        if (!_acceptedCycles.TryGetValue(source, out GpuProviderCycle? cycle))
        {
            return null;
        }

        TimeSpan age = GpuMonotonicClock.Elapsed(cycle.MonotonicTimestamp, nowTimestamp);
        return age >= TimeSpan.Zero && age <= freshness ? cycle : null;
    }

    private static IReadOnlyList<GpuProviderSample> BuildCandidates(
        GpuProviderCycle? nvidiaCycle,
        GpuProviderCycle? lhmCycle)
    {
        var candidates = new List<GpuProviderSample>();
        GpuProviderSample[] nvidiaSamples = nvidiaCycle?.Samples
            .Where(sample => sample.Vendor == GpuVendor.Nvidia)
            .ToArray() ?? Array.Empty<GpuProviderSample>();
        GpuProviderSample[] lhmSamples = lhmCycle?.Samples.ToArray() ??
                                         Array.Empty<GpuProviderSample>();
        bool nvidiaSmiOwnsNvidia = nvidiaSamples.Length > 0;

        foreach (GpuProviderSample sample in nvidiaSamples)
        {
            GpuProviderSample? exactMatch = lhmSamples
                .Where(candidate => candidate.Vendor == GpuVendor.Nvidia)
                .Where(candidate => HasExactComparableIdentity(sample, candidate))
                .OrderBy(candidate => candidate.StableId, StringComparer.Ordinal)
                .FirstOrDefault();
            candidates.Add(exactMatch is null ? sample : FillMissing(sample, exactMatch));
        }

        foreach (GpuProviderSample sample in lhmSamples)
        {
            if (sample.Vendor == GpuVendor.Nvidia && nvidiaSmiOwnsNvidia)
            {
                continue;
            }

            candidates.Add(sample);
        }

        return candidates;
    }

    private GpuProviderSample? Select(IReadOnlyList<GpuProviderSample> candidates)
    {
        if (candidates.Count == 0)
        {
            ResetSelection();
            return null;
        }

        GpuProviderSample? current = _currentStableId is null
            ? null
            : candidates.FirstOrDefault(
                sample => string.Equals(sample.StableId, _currentStableId, StringComparison.Ordinal));
        GpuProviderSample? bestWithUsage = candidates
            .Where(sample => Finite(sample.UsagePercent) is not null)
            .OrderByDescending(sample => sample.UsagePercent!.Value)
            .ThenBy(sample => sample.StableId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (current is null)
        {
            GpuProviderSample selected = bestWithUsage ?? candidates
                .OrderBy(sample => sample.StableId, StringComparer.Ordinal)
                .First();
            SetCurrent(selected.StableId);
            return selected;
        }

        if (bestWithUsage is null)
        {
            ClearChallenger();
            return current;
        }

        double? currentUsage = Finite(current.UsagePercent);
        if (currentUsage is null)
        {
            SetCurrent(bestWithUsage.StableId);
            return bestWithUsage;
        }

        if (string.Equals(bestWithUsage.StableId, current.StableId, StringComparison.Ordinal) ||
            bestWithUsage.UsagePercent!.Value < currentUsage.Value + 5d)
        {
            ClearChallenger();
            return current;
        }

        if (string.Equals(_challengerStableId, bestWithUsage.StableId, StringComparison.Ordinal))
        {
            _challengerTicks++;
        }
        else
        {
            _challengerStableId = bestWithUsage.StableId;
            _challengerTicks = 1;
        }

        if (_challengerTicks < 2)
        {
            return current;
        }

        SetCurrent(bestWithUsage.StableId);
        return bestWithUsage;
    }

    private static bool HasExactComparableIdentity(
        GpuProviderSample first,
        GpuProviderSample second)
    {
        return EqualPresent(first.Uuid, second.Uuid) ||
               EqualPresent(first.PciBusId, second.PciBusId);
    }

    private static bool EqualPresent(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) &&
        !string.IsNullOrWhiteSpace(second) &&
        string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);

    private static GpuProviderSample FillMissing(
        GpuProviderSample primary,
        GpuProviderSample secondary) => primary with
    {
        UsagePercent = primary.UsagePercent ?? secondary.UsagePercent,
        TemperatureCelsius = primary.TemperatureCelsius ?? secondary.TemperatureCelsius,
        DedicatedMemoryUsedBytes = primary.DedicatedMemoryUsedBytes ?? secondary.DedicatedMemoryUsedBytes,
        DedicatedMemoryTotalBytes = primary.DedicatedMemoryTotalBytes ?? secondary.DedicatedMemoryTotalBytes,
    };

    private static double? Finite(double? value) =>
        value is { } actual && double.IsFinite(actual) ? actual : null;

    private static long? NonNegative(long? value) => value is >= 0 ? value : null;

    private void LogSelection(GpuProviderSample selected)
    {
        string key = $"{selected.Source}:{selected.StableId}";
        if (string.Equals(_loggedSelectionKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _loggedSelectionKey = key;
        string usage = Finite(selected.UsagePercent) is { } usageValue
            ? usageValue.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";
        string temperature = Finite(selected.TemperatureCelsius) is { } temperatureValue
            ? temperatureValue.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";
        BandDiagnostics.Log(
            $"gpu selected source={selected.Source} vendor={selected.Vendor} " +
            $"device=\"{selected.Name}\" usage={usage}% coreTemp={temperature}C");
    }

    private void SetCurrent(string stableId)
    {
        _currentStableId = stableId;
        ClearChallenger();
    }

    private void ClearChallenger()
    {
        _challengerStableId = null;
        _challengerTicks = 0;
    }

    private void ResetSelection()
    {
        _currentStableId = null;
        _loggedSelectionKey = null;
        ClearChallenger();
    }
}
