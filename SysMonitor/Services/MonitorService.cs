using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net;
using System.Runtime.InteropServices;
using SysMonitor.Models;

namespace SysMonitor.Services;

internal enum CpuTemperatureSource
{
    None,
    Unavailable,
    LibreHardwareMonitor,
}

public sealed class MonitorService : IMonitorService
{
    private static long s_nextProducerId;
    private static readonly TimeSpan NetworkRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DriveRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly string[] VirtualAdapterMarkers =
    {
        "Hyper-V", "VMware", "Docker", "TAP", "TUN", "Virtual", "虚拟",
    };

    private static readonly string[] AdditionalVirtualAdapterMarkers =
    {
        "ZeroTier", "WireGuard", "OpenVPN", "Wintun", "Tailscale", "Hamachi",
    };

    private readonly SemaphoreSlim _samplingWakeup = new(0, 1);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CpuUsageReader _cpuReader = new();
    private readonly CpuTemperatureReader? _cpuTemperatureReader;
    private readonly CpuFrequencyReader _cpuFrequencyReader = new();
    private readonly MemoryFrequencyReader _memoryFrequencyReader = new();
    private TimeSpan _samplingInterval;
    private readonly GpuTelemetryCoordinator _gpuCoordinator;
    private readonly DriveTelemetryCache _driveTelemetry = new(GetSystemDriveRoot());
    private readonly List<NetworkCounter> _networkCounters = new();
    private readonly long _producerId = Interlocked.Increment(ref s_nextProducerId);
    private CancellationTokenSource? _runCancellation;
    private Task? _samplingTask;
    private MonitorSnapshot _latest = MonitorSnapshot.Empty;
    private long _sequence;
    private ulong _previousIdle;
    private ulong _previousTotal;
    private bool _cpuPrimed;
    private long _networkLastTimestamp;
    private long _networkRefreshTimestamp;
    private long _driveRefreshTimestamp;
    private int _detailedTelemetryEnabled;
    private bool _disposed;
    private CpuTemperatureSource _cpuTemperatureSource;

    public event EventHandler<MonitorSnapshot>? SnapshotUpdated;

    public MonitorSnapshot Latest => Volatile.Read(ref _latest);

    public void SetSamplingInterval(TimeSpan interval)
    {
        if (interval >= TimeSpan.FromMilliseconds(250) && interval <= TimeSpan.FromSeconds(10))
            _samplingInterval = interval;
    }

    /// <summary>Enables clock reads required only by the detailed game HUD.</summary>
    public void SetDetailedTelemetryEnabled(bool enabled) =>
        Volatile.Write(ref _detailedTelemetryEnabled, enabled ? 1 : 0);

    public MonitorService()
        : this(MonitorOptions.GameSafe)
    {
    }

    public MonitorService(MonitorOptions options)
        : this(options, gpuCoordinator: null, cpuTemperatureReader: null)
    {
    }

    internal MonitorService(
        MonitorOptions options,
        GpuTelemetryCoordinator? gpuCoordinator,
        CpuTemperatureReader? cpuTemperatureReader)
    {
        ArgumentNullException.ThrowIfNull(options);
        _cpuTemperatureReader = options.EnableCpuTemperatureReader
            ? cpuTemperatureReader ?? new CpuTemperatureReader()
            : null;
        if (_cpuTemperatureReader is not null)
        {
            _cpuTemperatureReader.ReaderReady += OnCpuTemperatureReaderReady;
        }
        _samplingInterval = options.SamplingInterval is { } interval && interval >= TimeSpan.FromMilliseconds(250)
            ? interval
            : TimeSpan.FromSeconds(1);
        _gpuCoordinator = gpuCoordinator ?? new GpuTelemetryCoordinator(options.EnableLibreHardwareMonitor);
        BandDiagnostics.Log(
            $"monitor options gpuCompatibility={options.EnableLibreHardwareMonitor} " +
            $"cpuTemperature={options.EnableCpuTemperatureReader} " +
            $"libreHardwareMonitor={options.EnableLibreHardwareMonitor}");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_samplingTask is { IsCompleted: false })
            {
                return;
            }

            _runCancellation?.Dispose();
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            PrimeCpu();
            _cpuReader.Start();
            _cpuTemperatureReader?.Start();
            InitializeNetworkCounters();
            try
            {
                await _gpuCoordinator.StartAsync(_runCancellation.Token).ConfigureAwait(false);
            }
            catch
            {
                // The lifecycle gate is already held here. Clean up inline
                // instead of calling StopAsync, which would re-enter the gate
                // and deadlock while a provider start is failing/canceling.
                await CleanupFailedStartAsync().ConfigureAwait(false);
                throw;
            }

            _samplingTask = Task.Run(
                () => SamplingLoopAsync(_runCancellation.Token),
                CancellationToken.None);
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
            _runCancellation?.Cancel();
            Task? samplingTask = _samplingTask;
            if (samplingTask is not null)
            {
                try
                {
                    await samplingTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await _gpuCoordinator.StopAsync().ConfigureAwait(false);
            _cpuTemperatureReader?.Stop();
            _cpuReader.Stop();
            _samplingTask = null;
            _runCancellation?.Dispose();
            _runCancellation = null;
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
        await _gpuCoordinator.DisposeAsync().ConfigureAwait(false);
        if (_cpuTemperatureReader is not null)
        {
            _cpuTemperatureReader.ReaderReady -= OnCpuTemperatureReaderReady;
        }
        _cpuTemperatureReader?.Dispose();
        _cpuReader.Dispose();
        _samplingWakeup.Dispose();
        _lifecycle.Dispose();
    }

    private async Task SamplingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                double cpuUsage = ReadCpuUsage();
                (double? cpuTemperature, double? cpuPower) = ReadCpuTelemetry();
                // Current CPU clock and the DIMM configuration query are used only
                // in the detailed HUD. Keeping them off for the normal tray/band
                // session avoids allocating a WMI provider and native buffers.
                bool detailedTelemetry = Volatile.Read(ref _detailedTelemetryEnabled) != 0;
                double? cpuFrequency = detailedTelemetry ? _cpuFrequencyReader.ReadCurrentMhz() : null;
                double? memoryFrequency = detailedTelemetry ? _memoryFrequencyReader.ReadConfiguredMhz() : null;
                (double usage, long used, long total) memory = ReadMemory();
                (double download, double upload) network = ReadNetwork();
                RefreshDrivesIfDue();
                var fixedDrives = _driveTelemetry.Current;
                DriveSnapshot? systemDrive = fixedDrives.FirstOrDefault(item => item.IsSystemDrive);

                var snapshot = new MonitorSnapshot(
                    Interlocked.Increment(ref _sequence),
                    DateTimeOffset.Now,
                    cpuUsage,
                    cpuTemperature,
                    Environment.ProcessorCount,
                    memory.usage,
                    memory.used,
                    memory.total,
                    _gpuCoordinator.Read(),
                    network.download,
                    network.upload,
                    systemDrive?.Name ?? _driveTelemetry.SystemDriveName,
                    systemDrive?.UsagePercent ?? 0d,
                    fixedDrives)
                {
                    ProducerId = _producerId,
                    MonotonicTimestamp = Stopwatch.GetTimestamp(),
                    CpuFrequencyMhz = cpuFrequency,
                    CpuPowerWatts = cpuPower,
                    MemoryFrequencyMhz = memoryFrequency
                };

                Volatile.Write(ref _latest, snapshot);
                RaiseSnapshotUpdated(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                BandDiagnostics.LogRateLimited(
                    "sampling-loop-error",
                    $"Sampling loop recovered from unexpected error: {exception.Message}",
                    TimeSpan.FromSeconds(5));
            }

            TimeSpan delay = _samplingInterval;
            if (_cpuTemperatureReader is not null &&
                (_cpuTemperatureReader.OpenInProgress || _cpuTemperatureReader.HelperLaunchInProgress) &&
                _latest.CpuTemperatureCelsius is null)
            {
                delay = TimeSpan.FromMilliseconds(150);
            }

            try
            {
                await _samplingWakeup.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void OnCpuTemperatureReaderReady(object? sender, EventArgs e)
    {
        try
        {
            if (_samplingWakeup.CurrentCount == 0)
            {
                _samplingWakeup.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // A reader open can complete concurrently with service disposal.
        }
        catch (SemaphoreFullException)
        {
            // A sampling loop wakeup was already queued.
        }
    }

    private async Task CleanupFailedStartAsync()
    {
        try
        {
            _runCancellation?.Cancel();
        }
        catch (Exception exception)
        {
            BandDiagnostics.LogRateLimited(
                "monitor-start-cleanup-cancel",
                $"Cancellation during failed monitor start cleanup failed: {exception.GetType().Name}",
                TimeSpan.FromSeconds(30));
        }

        try
        {
            await _gpuCoordinator.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            BandDiagnostics.LogRateLimited(
                "monitor-start-cleanup-gpu",
                $"GPU cleanup after failed monitor start failed: {exception.GetType().Name}",
                TimeSpan.FromSeconds(30));
        }

        try
        {
            _cpuTemperatureReader?.Stop();
        }
        catch (Exception exception)
        {
            BandDiagnostics.LogRateLimited(
                "monitor-start-cleanup-cpu-temperature",
                $"CPU temperature cleanup after failed monitor start failed: {exception.GetType().Name}",
                TimeSpan.FromSeconds(30));
        }

        try
        {
            _cpuReader.Stop();
        }
        catch (Exception exception)
        {
            BandDiagnostics.LogRateLimited(
                "monitor-start-cleanup-cpu",
                $"CPU cleanup after failed monitor start failed: {exception.GetType().Name}",
                TimeSpan.FromSeconds(30));
        }

        _samplingTask = null;
        _networkCounters.Clear();
        CancellationTokenSource? runCancellation = _runCancellation;
        _runCancellation = null;
        runCancellation?.Dispose();
    }

    private (double? Temperature, double? PowerWatts) ReadCpuTelemetry()
    {
        (double? temperature, double? power) = _cpuTemperatureReader?.ReadTelemetry() ?? (null, null);
        if (temperature is double value)
        {
            LogCpuTemperatureSource(CpuTemperatureSource.LibreHardwareMonitor, "independent CPU sensor reader");
            return (value, power);
        }

        LogCpuTemperatureSource(CpuTemperatureSource.Unavailable, "no readable CPU temperature sensor");
        return (null, power);
    }

    private void LogCpuTemperatureSource(CpuTemperatureSource source, string detail)
    {
        if (_cpuTemperatureSource == source)
        {
            return;
        }

        _cpuTemperatureSource = source;
        BandDiagnostics.Log($"CPU temperature source={source} detail={detail}");
    }

    private void PrimeCpu()
    {
        _cpuPrimed = false;
        _ = ReadCpuUsage();
    }

    private double ReadCpuUsage()
    {
        return _cpuReader.Read() ?? ReadCpuBusyTimeUsage();
    }

    private double ReadCpuBusyTimeUsage()
    {
        try
        {
            if (!NativeDataMethods.GetSystemTimes(out NativeDataMethods.FileTime idleTime,
                    out NativeDataMethods.FileTime kernelTime,
                    out NativeDataMethods.FileTime userTime))
            {
                return 0;
            }

            ulong idle = idleTime.ToUInt64();
            ulong total = kernelTime.ToUInt64() + userTime.ToUInt64();
            if (!_cpuPrimed)
            {
                _previousIdle = idle;
                _previousTotal = total;
                _cpuPrimed = true;
                return 0;
            }

            ulong idleDelta = idle >= _previousIdle ? idle - _previousIdle : 0;
            ulong totalDelta = total >= _previousTotal ? total - _previousTotal : 0;
            _previousIdle = idle;
            _previousTotal = total;
            if (totalDelta == 0 || idleDelta > totalDelta)
            {
                return 0;
            }

            return Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    private static (double usage, long used, long total) ReadMemory()
    {
        try
        {
            var info = new NativeDataMethods.PerformanceInformation
            {
                Size = (uint)Marshal.SizeOf<NativeDataMethods.PerformanceInformation>(),
            };
            if (!NativeDataMethods.GetPerformanceInfo(ref info, info.Size))
            {
                return default;
            }

            ulong totalPages = info.PhysicalTotal.ToUInt64();
            ulong availablePages = info.PhysicalAvailable.ToUInt64();
            ulong pageSize = info.PageSize.ToUInt64();
            if (totalPages == 0 || pageSize == 0)
            {
                return default;
            }

            availablePages = Math.Min(availablePages, totalPages);
            ulong usedPages = totalPages - availablePages;
            long totalBytes = SaturatingMultiply(totalPages, pageSize);
            long usedBytes = SaturatingMultiply(usedPages, pageSize);
            return (usedPages * 100d / totalPages, usedBytes, totalBytes);
        }
        catch
        {
            return default;
        }
    }

    private void RefreshDrivesIfDue()
    {
        long now = Stopwatch.GetTimestamp();
        if (_driveRefreshTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_driveRefreshTimestamp, now) < DriveRefreshInterval)
        {
            return;
        }

        _driveRefreshTimestamp = now;
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            _driveTelemetry.ApplyGlobalFailure();
            return;
        }

        var observations = new List<DriveTelemetryObservation>(drives.Length);
        foreach (DriveInfo drive in drives)
        {
            string name;
            try
            {
                name = drive.Name;
            }
            catch
            {
                continue;
            }

            bool isFixed;
            bool isReady;
            try
            {
                isFixed = drive.DriveType == DriveType.Fixed;
                isReady = drive.IsReady;
            }
            catch
            {
                observations.Add(new DriveTelemetryObservation(
                    name, true, true, false, string.Empty, 0, 0));
                continue;
            }

            if (!isFixed || !isReady)
            {
                observations.Add(new DriveTelemetryObservation(
                    name, isFixed, isReady, true, string.Empty, 0, 0));
                continue;
            }

            try
            {
                observations.Add(new DriveTelemetryObservation(
                    name,
                    true,
                    true,
                    true,
                    drive.VolumeLabel,
                    drive.TotalSize,
                    drive.TotalFreeSpace));
            }
            catch
            {
                observations.Add(new DriveTelemetryObservation(
                    name, true, true, false, string.Empty, 0, 0));
            }
        }

        _driveTelemetry.ApplySuccessfulEnumeration(observations);
    }

    private static string GetSystemDriveRoot()
    {
        try
        {
            return Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        }
        catch
        {
            return "C:\\";
        }
    }

    private void InitializeNetworkCounters()
    {
        _networkCounters.Clear();
        RebuildNetworkCounters(preserveExisting: false);
        long now = Stopwatch.GetTimestamp();
        _networkLastTimestamp = now;
        _networkRefreshTimestamp = now;
    }

    private (double download, double upload) ReadNetwork()
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_networkRefreshTimestamp, now) >= NetworkRefreshInterval)
            {
                RebuildNetworkCounters(preserveExisting: true);
                _networkRefreshTimestamp = now;
            }

            double elapsedSeconds = Stopwatch.GetElapsedTime(_networkLastTimestamp, now).TotalSeconds;
            _networkLastTimestamp = now;
            if (elapsedSeconds <= 0)
            {
                return default;
            }

            ulong receivedDelta = 0;
            ulong sentDelta = 0;
            foreach (NetworkCounter counter in _networkCounters)
            {
                try
                {
                    IPv4InterfaceStatistics stats = counter.Interface.GetIPv4Statistics();
                    ulong received = (ulong)Math.Max(0, stats.BytesReceived);
                    ulong sent = (ulong)Math.Max(0, stats.BytesSent);
                    if (received >= counter.BytesReceived)
                    {
                        receivedDelta = SaturatingAdd(receivedDelta, received - counter.BytesReceived);
                    }

                    if (sent >= counter.BytesSent)
                    {
                        sentDelta = SaturatingAdd(sentDelta, sent - counter.BytesSent);
                    }

                    counter.BytesReceived = received;
                    counter.BytesSent = sent;
                }
                catch
                {
                    // One disappearing interface must not invalidate the other counters.
                }
            }

            return (receivedDelta / elapsedSeconds, sentDelta / elapsedSeconds);
        }
        catch
        {
            return default;
        }
    }

    private void RebuildNetworkCounters(bool preserveExisting)
    {
        var existing = preserveExisting
            ? _networkCounters.ToDictionary(counter => counter.Interface.Id, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, NetworkCounter>(StringComparer.OrdinalIgnoreCase);
        var rebuilt = new List<NetworkCounter>();

        try
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            List<NetworkInterface> candidates = interfaces.Where(IsPhysicalActiveAdapter).ToList();
            uint? defaultRouteIndex = TryGetDefaultRouteInterfaceIndex();
            List<NetworkInterface> selected = defaultRouteIndex is uint index
                ? candidates.Where(networkInterface => TryGetInterfaceIndex(networkInterface) == index).ToList()
                : [];
            if (selected.Count == 0)
            {
                selected = candidates.Where(HasDefaultGateway).ToList();
            }

            // Report the active internet uplink rather than summing every
            // adapter. Overlay/VPN traffic may otherwise be counted twice.
            foreach (NetworkInterface networkInterface in selected.Count > 0 ? selected : candidates)
            {
                if (existing.TryGetValue(networkInterface.Id, out NetworkCounter? oldCounter))
                {
                    oldCounter.Interface = networkInterface;
                    rebuilt.Add(oldCounter);
                    continue;
                }

                try
                {
                    IPv4InterfaceStatistics stats = networkInterface.GetIPv4Statistics();
                    rebuilt.Add(new NetworkCounter(
                        networkInterface,
                        (ulong)Math.Max(0, stats.BytesReceived),
                        (ulong)Math.Max(0, stats.BytesSent)));
                }
                catch
                {
                }
            }
        }
        catch
        {
            return;
        }

        _networkCounters.Clear();
        _networkCounters.AddRange(rebuilt);
        BandDiagnostics.LogRateLimited(
            "network-uplink",
            $"network uplink adapters={string.Join(", ", rebuilt.Select(counter => counter.Interface.Name))}",
            TimeSpan.FromMinutes(5));
    }

    private static bool HasDefaultGateway(NetworkInterface networkInterface)
    {
        try
        {
            return networkInterface.GetIPProperties().GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !IPAddress.Any.Equals(gateway.Address));
        }
        catch { return false; }
    }

    private static uint? TryGetDefaultRouteInterfaceIndex()
    {
        try
        {
            return GetBestInterface(0x01010101, out uint index) == 0 ? index : null;
        }
        catch { return null; }
    }

    private static uint? TryGetInterfaceIndex(NetworkInterface networkInterface)
    {
        try { return (uint)networkInterface.GetIPProperties().GetIPv4Properties().Index; }
        catch { return null; }
    }

    private static bool IsPhysicalActiveAdapter(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up)
        {
            return false;
        }

        NetworkInterfaceType type = networkInterface.NetworkInterfaceType;
        if (type is NetworkInterfaceType.Loopback or
            NetworkInterfaceType.Tunnel or
            NetworkInterfaceType.Ppp)
        {
            return false;
        }

        bool supportedType = type is NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.Ethernet3Megabit or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.Wireless80211;
        if (!supportedType)
        {
            return false;
        }

        string identity = networkInterface.Name + " " + networkInterface.Description;
        return !VirtualAdapterMarkers.Concat(AdditionalVirtualAdapterMarkers).Any(marker =>
            identity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);

    private void RaiseSnapshotUpdated(MonitorSnapshot snapshot)
    {
        EventHandler<MonitorSnapshot>? handlers = SnapshotUpdated;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<MonitorSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch
            {
                // A subscriber cannot stop the monitoring loop.
            }
        }
    }

    private static long SaturatingMultiply(ulong left, ulong right)
    {
        if (left == 0 || right == 0)
        {
            return 0;
        }

        ulong max = (ulong)long.MaxValue;
        return left > max / right ? long.MaxValue : (long)(left * right);
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private sealed class NetworkCounter
    {
        internal NetworkCounter(NetworkInterface networkInterface, ulong bytesReceived, ulong bytesSent)
        {
            Interface = networkInterface;
            BytesReceived = bytesReceived;
            BytesSent = bytesSent;
        }

        internal NetworkInterface Interface { get; set; }
        internal ulong BytesReceived { get; set; }
        internal ulong BytesSent { get; set; }
    }
}
