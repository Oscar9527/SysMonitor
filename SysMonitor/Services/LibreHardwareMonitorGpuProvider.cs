using LibreHardwareMonitor.Hardware;

namespace SysMonitor.Services;

internal sealed class LibreHardwareMonitorGpuProvider : IGpuTelemetryProvider
{
    private static readonly int[] RetrySeconds = { 1, 2, 4, 8, 15, 30 };
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
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
            try
            {
                var computer = new Computer { IsGpuEnabled = true };
                try
                {
                    computer.Open();
                    retryIndex = 0;
                    await SampleUntilCancelledAsync(computer, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        computer.Close();
                    }
                    catch (Exception exception)
                    {
                        BandDiagnostics.LogRateLimited(
                            "gpu-lhm-close",
                            $"gpu source=lhm close error={exception.GetType().Name}",
                            TimeSpan.FromSeconds(30));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                BandDiagnostics.LogRateLimited(
                    "gpu-lhm-worker",
                    $"gpu source=lhm error={exception.GetType().Name}",
                    TimeSpan.FromSeconds(30));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            int delay = RetrySeconds[retryIndex];
            retryIndex = Math.Min(retryIndex + 1, RetrySeconds.Length - 1);
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

    private async Task SampleUntilCancelledAsync(
        Computer computer,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            long cycleStart = GpuMonotonicClock.GetTimestamp();
            DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
            var samples = new List<GpuProviderSample>();
            int index = 0;

            foreach (IHardware hardware in computer.Hardware)
            {
                GpuVendor? vendor = ToVendor(hardware.HardwareType);
                if (vendor is null)
                {
                    continue;
                }

                try
                {
                    hardware.Update();
                    samples.Add(ReadHardware(hardware, vendor.Value, index, sampledAt, cycleStart));
                }
                catch (Exception exception)
                {
                    string id = SafeIdentifier(hardware);
                    BandDiagnostics.LogRateLimited(
                        $"gpu-lhm-device-{id}",
                        $"gpu source=lhm device=\"{id}\" error={exception.GetType().Name}",
                        TimeSpan.FromSeconds(30));
                }

                index++;
            }

            Volatile.Write(
                ref _latestCycle,
                new GpuProviderCycle(
                    GpuTelemetrySource.LibreHardwareMonitor,
                    sampledAt,
                    cycleStart,
                    samples));

            TimeSpan elapsed = GpuMonotonicClock.Elapsed(
                cycleStart,
                GpuMonotonicClock.GetTimestamp());
            TimeSpan remaining = TimeSpan.FromSeconds(1) - elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static GpuProviderSample ReadHardware(
        IHardware hardware,
        GpuVendor vendor,
        int index,
        DateTimeOffset sampledAt,
        long monotonicTimestamp)
    {
        string hardwareId = SafeIdentifier(hardware);
        string? pnpDeviceId = ReadStringProperty(hardware, "DeviceId");
        var readings = new List<GpuSensorReading>();

        foreach (ISensor sensor in hardware.Sensors)
        {
            try
            {
                GpuSensorKind? kind = sensor.SensorType switch
                {
                    SensorType.Load => GpuSensorKind.Load,
                    SensorType.Temperature => GpuSensorKind.Temperature,
                    SensorType.SmallData => GpuSensorKind.SmallData,
                    _ => null,
                };
                if (kind is not null)
                {
                    readings.Add(new GpuSensorReading(sensor.Name, kind.Value, sensor.Value));
                }
            }
            catch (Exception exception)
            {
                BandDiagnostics.LogRateLimited(
                    $"gpu-lhm-sensor-{hardwareId}",
                    $"gpu source=lhm device=\"{hardwareId}\" sensor-read error={exception.GetType().Name}",
                    TimeSpan.FromSeconds(30));
            }
        }

        GpuSensorSelection selected = GpuSensorSelector.Select(vendor, readings);
        string stableIdentity = !string.IsNullOrWhiteSpace(pnpDeviceId)
            ? pnpDeviceId
            : hardwareId;
        return new GpuProviderSample(
            $"lhm:{stableIdentity.Trim().ToUpperInvariant()}",
            index,
            string.IsNullOrWhiteSpace(hardware.Name) ? "Graphics adapter" : hardware.Name.Trim(),
            vendor,
            GpuTelemetrySource.LibreHardwareMonitor,
            hardwareId,
            pnpDeviceId,
            null,
            null,
            selected.UsagePercent,
            selected.TemperatureCelsius,
            selected.DedicatedMemoryUsedBytes,
            selected.DedicatedMemoryTotalBytes,
            sampledAt,
            monotonicTimestamp);
    }

    private static GpuVendor? ToVendor(HardwareType hardwareType) => hardwareType switch
    {
        HardwareType.GpuNvidia => GpuVendor.Nvidia,
        HardwareType.GpuAmd => GpuVendor.Amd,
        HardwareType.GpuIntel => GpuVendor.Intel,
        _ => null,
    };

    private static string SafeIdentifier(IHardware hardware)
    {
        try
        {
            return hardware.Identifier.ToString();
        }
        catch
        {
            return hardware.Name;
        }
    }

    private static string? ReadStringProperty(object instance, string propertyName)
    {
        try
        {
            return instance.GetType().GetProperty(propertyName)?.GetValue(instance) as string;
        }
        catch
        {
            return null;
        }
    }
}
