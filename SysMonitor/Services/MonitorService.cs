using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using SysMonitor.Models;

namespace SysMonitor.Services;

internal enum CpuTemperatureSource
{
    None,
    Unavailable,
    MsiAfterburnerSharedMemory,
    CompatibilitySensors,
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

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CpuUsageReader _cpuReader = new();
    private readonly CpuTemperatureReader? _cpuTemperatureReader;
    private readonly ICpuTemperatureSource? _sharedMemoryCpuTemperature;
    private readonly CpuFrequencyReader _cpuFrequencyReader = new();
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
    private bool _disposed;
    private CpuTemperatureSource _cpuTemperatureSource;

    public event EventHandler<MonitorSnapshot>? SnapshotUpdated;

    public MonitorSnapshot Latest => Volatile.Read(ref _latest);

    public MonitorService()
        : this(MonitorOptions.GameSafe)
    {
    }

    public MonitorService(MonitorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _cpuTemperatureReader = options.EnableCpuTemperatureReader
            ? new CpuTemperatureReader()
            : null;
        _sharedMemoryCpuTemperature = options.EnableSharedMemoryCpuTemperature
            ? new MahmSharedMemoryReader()
            : null;
        _gpuCoordinator = new GpuTelemetryCoordinator(options.EnableLibreHardwareMonitor);
        BandDiagnostics.Log(
            $"monitor options gameSafe={!options.EnableLibreHardwareMonitor && !options.EnableCpuTemperatureReader} " +
            $"sharedMemoryCpuTemperature={options.EnableSharedMemoryCpuTemperature} " +
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
            await _gpuCoordinator.StartAsync(_runCancellation.Token).ConfigureAwait(false);
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
        _cpuTemperatureReader?.Dispose();
        _cpuReader.Dispose();
        _lifecycle.Dispose();
    }

    private async Task SamplingLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            double cpuUsage = ReadCpuUsage();
            double? cpuTemperature = ReadCpuTemperature();
            double? cpuFrequency = _cpuFrequencyReader.ReadCurrentMhz();
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
                CpuFrequencyMhz = cpuFrequency
            };

            Volatile.Write(ref _latest, snapshot);
            RaiseSnapshotUpdated(snapshot);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private double? ReadCpuTemperature()
    {
        SharedMemoryValue shared = _sharedMemoryCpuTemperature?.Read(DateTimeOffset.UtcNow) ??
            SharedMemoryValue.Missing("MAHM shared-memory reader disabled");
        if (shared.Value is double sharedValue)
        {
            LogCpuTemperatureSource(CpuTemperatureSource.MsiAfterburnerSharedMemory, shared.Reason);
            return sharedValue;
        }

        double? compatibilityValue = _cpuTemperatureReader?.Read();
        if (compatibilityValue is double value)
        {
            LogCpuTemperatureSource(CpuTemperatureSource.CompatibilitySensors, "LibreHardwareMonitor");
            return value;
        }

        LogCpuTemperatureSource(CpuTemperatureSource.Unavailable, shared.Reason);
        return null;
    }

    private void LogCpuTemperatureSource(CpuTemperatureSource source, string detail)
    {
        if (_cpuTemperatureSource == source)
        {
            return;
        }

        _cpuTemperatureSource = source;
        BandDiagnostics.LogRateLimited(
            "cpu-temperature-source-change",
            $"CPU temperature source={source} detail={detail}",
            TimeSpan.FromSeconds(30));
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
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsPhysicalActiveAdapter(networkInterface))
                {
                    continue;
                }

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
        return !VirtualAdapterMarkers.Any(marker =>
            identity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

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
