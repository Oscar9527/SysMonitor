using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace SysMonitor.Services;

/// <summary>
/// Keeps a CPU-only LibreHardwareMonitor session open and reads the physical
/// package/die temperature. Missing sensors remain unknown instead of being
/// replaced with an estimated value.
/// </summary>
internal sealed class CpuTemperatureReader : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HelperValueMaxAge = TimeSpan.FromSeconds(5);
    private const int NoSensorRetryThreshold = 1;
    private const string HelperArgument = "--cpu-temperature-helper";
    private const string HelperPipePrefix = "SysMonitor.CpuTemperature.";
    private readonly object _gate = new();
    private readonly Func<bool, Computer> _computerFactory;
    private readonly Action<Computer> _computerOpen;
    private Computer? _computer;
    private CancellationTokenSource? _helperCancellation;
    private NamedPipeServerStream? _helperPipe;
    private Process? _helperProcess;
    private Task? _helperTask;
    private long _nextRetryTimestamp;
    private long _helperTemperatureTimestamp;
    private double _helperTemperature;
    private long _helperPowerTimestamp;
    private double _helperPower;
    private string? _loggedSensor;
    private string? _loggedPowerSensor;
    private int _consecutiveNoSensorReads;
    // The motherboard/Super-I/O tree can retain significantly more native
    // driver state. Open it only after a CPU-only scan has actually failed.
    private bool _motherboardFallbackAttempted;
    private bool _helperLaunchAttempted;
    private bool _helperLaunchInProgress;
    private bool _openInProgress;
    private bool _running;
    private bool _disposed;
    private int _lifecycleGeneration;

    internal CpuTemperatureReader()
        : this(
            includeMotherboard => new Computer
            {
                IsCpuEnabled = true,
                IsMotherboardEnabled = includeMotherboard,
            },
            computer => computer.Open())
    {
    }

    internal CpuTemperatureReader(
        Func<bool, Computer> computerFactory,
        Action<Computer> computerOpen)
    {
        _computerFactory = computerFactory ?? throw new ArgumentNullException(nameof(computerFactory));
        _computerOpen = computerOpen ?? throw new ArgumentNullException(nameof(computerOpen));
    }

    public event EventHandler? ReaderReady;

    internal bool OpenInProgress
    {
        get
        {
            lock (_gate)
            {
                return _openInProgress;
            }
        }
    }

    internal bool HasOpenComputer
    {
        get
        {
            lock (_gate)
            {
                return _computer is not null;
            }
        }
    }

    internal bool HelperLaunchInProgress
    {
        get
        {
            lock (_gate)
            {
                return _helperLaunchInProgress || (_helperProcess is not null && _helperTemperatureTimestamp == 0);
            }
        }
    }

    internal bool MotherboardFallbackAttempted
    {
        get
        {
            lock (_gate)
            {
                return _motherboardFallbackAttempted;
            }
        }
    }

    internal void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_running)
            {
                _running = true;
                _lifecycleGeneration++;
            }

            TryScheduleOpenLocked();
        }
    }

    internal double? Read() => ReadTelemetry().Temperature;

    internal double? ReadPower() => ReadTelemetry().PowerWatts;

    internal (double? Temperature, double? PowerWatts) ReadTelemetry()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (null, null);
            }

            if (_computer is null &&
                !_openInProgress &&
                !_helperLaunchInProgress &&
                _helperProcess is null &&
                Stopwatch.GetTimestamp() >= _nextRetryTimestamp)
            {
                TryScheduleOpenLocked();
            }

            if (_computer is null)
            {
                if (_openInProgress)
                {
                    return (ReadHelperTemperatureLocked(), ReadHelperPowerLocked());
                }

                RecordUnavailableReadLocked();
                return (ReadHelperTemperatureLocked(), ReadHelperPowerLocked());
            }

            try
            {
                (TemperatureCandidate? selectedTemp, PowerCandidate? selectedPower) = ReadBestTelemetry(_computer);
                if (selectedTemp is null)
                {
                    BandDiagnostics.LogRateLimited(
                        "cpu-temperature-no-sensor",
                        "CPU temperature unavailable in primary reader, checking elevated helper",
                        TimeSpan.FromMinutes(5));
                    RecordUnavailableReadLocked();
                    double? helperTemp = ReadHelperTemperatureLocked();
                    double? helperPower = ReadHelperPowerLocked();
                    return (helperTemp, selectedPower?.Value ?? helperPower);
                }

                _consecutiveNoSensorReads = 0;
                StopHelperLocked();

                if (!string.Equals(_loggedSensor, selectedTemp.Name, StringComparison.Ordinal))
                {
                    _loggedSensor = selectedTemp.Name;
                    BandDiagnostics.Log(
                        $"CPU temperature source=LibreHardwareMonitor sensor=\"{selectedTemp.Name}\" value={selectedTemp.Value:0.0}C");
                }

                if (selectedPower is not null && !string.Equals(_loggedPowerSensor, selectedPower.Name, StringComparison.Ordinal))
                {
                    _loggedPowerSensor = selectedPower.Name;
                    BandDiagnostics.Log(
                        $"CPU power source=LibreHardwareMonitor sensor=\"{selectedPower.Name}\" value={selectedPower.Value:0.0}W");
                }

                return (selectedTemp.Value, selectedPower?.Value ?? ReadHelperPowerLocked());
            }
            catch (Exception ex)
            {
                BandDiagnostics.LogRateLimited(
                    "cpu-temperature-read-failed",
                    $"CPU temperature read failed type={ex.GetType().Name}",
                    TimeSpan.FromMinutes(1));
                CloseLocked();
                ScheduleRetryLocked();
                RecordUnavailableReadLocked();
                return (ReadHelperTemperatureLocked(), ReadHelperPowerLocked());
            }
        }
    }

    internal void Stop()
    {
        Task? helperTask;
        lock (_gate)
        {
            if (_running)
            {
                _running = false;
                _lifecycleGeneration++;
            }

            CloseLocked();
            helperTask = StopHelperLocked();
        }

        WaitBrieflyForHelper(helperTask);
    }

    public void Dispose()
    {
        Task? helperTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
            _lifecycleGeneration++;
            CloseLocked();
            helperTask = StopHelperLocked();
        }

        WaitBrieflyForHelper(helperTask);
    }

    private void TryScheduleOpenLocked()
    {
        if (_computer is not null || _openInProgress || _disposed || !_running)
        {
            return;
        }

        bool includeMotherboard = _motherboardFallbackAttempted;
        int generation = _lifecycleGeneration;
        _openInProgress = true;
        BandDiagnostics.Log(
            $"CPU temperature reader open scheduled motherboard={includeMotherboard}");
        _ = Task.Run(() => OpenComputer(includeMotherboard, generation));
    }

    private void OpenComputer(bool includeMotherboard, int generation)
    {
        Computer? openingComputer = null;
        Exception? openError = null;
        try
        {
            openingComputer = _computerFactory(includeMotherboard);
            _computerOpen(openingComputer);
        }
        catch (Exception ex)
        {
            openError = ex;
        }

        Computer? computerToClose;
        lock (_gate)
        {
            if (openError is null &&
                openingComputer is not null &&
                _running &&
                !_disposed &&
                generation == _lifecycleGeneration)
            {
                _computer = openingComputer;
                openingComputer = null;
                _openInProgress = false;
                _nextRetryTimestamp = 0;
                BandDiagnostics.Log("CPU temperature reader opened source=LibreHardwareMonitor");
                ReaderReady?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Keep the open marked in progress until a rejected Computer has
            // been closed. This serializes LHM sessions across stop/restart.
            computerToClose = openingComputer;
        }

        if (computerToClose is not null)
        {
            try
            {
                computerToClose.Close();
            }
            catch
            {
            }
        }

        if (openError is not null)
        {
            BandDiagnostics.LogRateLimited(
                "cpu-temperature-open-failed",
                $"CPU temperature reader open failed type={openError.GetType().Name}",
                TimeSpan.FromMinutes(1));
        }

        lock (_gate)
        {
            _openInProgress = false;
            if (!_running || _disposed)
            {
                return;
            }

            if (generation != _lifecycleGeneration)
            {
                _nextRetryTimestamp = 0;
                TryScheduleOpenLocked();
            }
            else
            {
                ScheduleRetryLocked();
            }
        }
    }

    internal static double? ReadTemperature(Computer computer) =>
        ReadCpuTelemetry(computer).Temperature;

    internal static (double? Temperature, double? PowerWatts) ReadCpuTelemetry(Computer computer)
    {
        (TemperatureCandidate? temp, PowerCandidate? power) = ReadBestTelemetry(computer);
        return (temp?.Value, power?.Value);
    }

    private static (TemperatureCandidate? Temperature, PowerCandidate? Power) ReadBestTelemetry(Computer computer)
    {
        var tempCandidates = new List<TemperatureCandidate>();
        var powerCandidates = new List<PowerCandidate>();
        foreach (IHardware hardware in computer.Hardware)
        {
            CollectCpuTelemetry(hardware, tempCandidates, powerCandidates);
        }

        TemperatureCandidate? bestTemp = tempCandidates
            .OrderBy(candidate => candidate.Priority)
            .ThenByDescending(candidate => candidate.Value)
            .FirstOrDefault();

        PowerCandidate? bestPower = powerCandidates
            .OrderBy(candidate => candidate.Priority)
            .ThenByDescending(candidate => candidate.Value)
            .FirstOrDefault();

        return (bestTemp, bestPower);
    }

    private static void CollectCpuTelemetry(
        IHardware hardware,
        ICollection<TemperatureCandidate> tempCandidates,
        ICollection<PowerCandidate> powerCandidates)
    {
        hardware.Update();
        bool isCpuHardware = hardware.HardwareType == HardwareType.Cpu;
        if (isCpuHardware ||
            hardware.HardwareType is HardwareType.Motherboard or HardwareType.SuperIO)
        {
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.Value is not float value)
                {
                    continue;
                }

                if (sensor.SensorType == SensorType.Temperature)
                {
                    double temperature = value;
                    if (!double.IsFinite(temperature) || temperature is < 1 or > 125)
                    {
                        continue;
                    }

                    int priority = GetSensorPriority(sensor.Name);
                    if (!isCpuHardware)
                    {
                        // Motherboard sensors are a fallback. Only accept names that
                        // explicitly identify a CPU/package/core sensor so chipset and
                        // ambient temperatures are never mislabeled as CPU temperature.
                        if (priority > 3 && !IsCpuTemperatureName(sensor.Name))
                        {
                            continue;
                        }

                        priority += 4;
                    }

                    tempCandidates.Add(new TemperatureCandidate(
                        $"{hardware.Name} / {sensor.Name}",
                        temperature,
                        priority));
                }
                else if (isCpuHardware && sensor.SensorType == SensorType.Power)
                {
                    double power = value;
                    if (!double.IsFinite(power) || power is <= 0.5 or > 2000)
                    {
                        continue;
                    }

                    int priority = GetCpuPowerPriority(sensor.Name);
                    powerCandidates.Add(new PowerCandidate(
                        $"{hardware.Name} / {sensor.Name}",
                        power,
                        priority));
                }
            }
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            CollectCpuTelemetry(subHardware, tempCandidates, powerCandidates);
        }
    }

    private static int GetSensorPriority(string sensorName)
    {
        if (sensorName.Contains("Package", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (sensorName.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Die", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (sensorName.Contains("Max", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    private static int GetCpuPowerPriority(string sensorName)
    {
        if (sensorName.Contains("Package", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (sensorName.Contains("Total", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (sensorName.Contains("Core", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (sensorName.Contains("CPU", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return 4;
    }

    private static bool IsCpuTemperatureName(string name) =>
        name.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Processor", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Die", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Core", StringComparison.OrdinalIgnoreCase);

    private void ScheduleRetryLocked()
    {
        _nextRetryTimestamp = Stopwatch.GetTimestamp() +
            (long)(RetryInterval.TotalSeconds * Stopwatch.Frequency);
    }

    private void RecordUnavailableReadLocked()
    {
        _consecutiveNoSensorReads++;
        if (_consecutiveNoSensorReads < NoSensorRetryThreshold)
        {
            return;
        }

        // Some firmware exposes the CPU package through a motherboard sensor.
        // Try that heavier tree only after the lean CPU-only reader proves empty.
        CloseLocked();
        if (!_motherboardFallbackAttempted)
        {
            _motherboardFallbackAttempted = true;
            _nextRetryTimestamp = 0;
            _consecutiveNoSensorReads = 0;
            TryScheduleOpenLocked();
            return;
        }

        // If both regular readers are empty, retain the elevated fallback for
        // machines whose sensor driver requires it.
        ScheduleRetryLocked();
        TryStartHelperLocked();
        _consecutiveNoSensorReads = 0;
    }

    private double? ReadHelperTemperatureLocked()
    {
        long timestamp = Volatile.Read(ref _helperTemperatureTimestamp);
        if (timestamp == 0 ||
            Stopwatch.GetElapsedTime(timestamp, Stopwatch.GetTimestamp()) > HelperValueMaxAge)
        {
            return null;
        }

        return Volatile.Read(ref _helperTemperature);
    }

    private double? ReadHelperPowerLocked()
    {
        long timestamp = Volatile.Read(ref _helperPowerTimestamp);
        if (timestamp == 0 ||
            Stopwatch.GetElapsedTime(timestamp, Stopwatch.GetTimestamp()) > HelperValueMaxAge)
        {
            return null;
        }

        return Volatile.Read(ref _helperPower);
    }

    private void TryStartHelperLocked()
    {
        if (_disposed || _helperLaunchAttempted)
        {
            return;
        }

        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            _helperLaunchAttempted = true;
            return;
        }

        _helperLaunchAttempted = true;
        _helperLaunchInProgress = true;
        var cancellation = new CancellationTokenSource();
        _helperCancellation = cancellation;
        string pipeName = HelperPipePrefix + Guid.NewGuid().ToString("N");
        _helperTask = Task.Run(() => RunHelperClientAsync(
            executablePath,
            pipeName,
            cancellation));
    }

    private async Task RunHelperClientAsync(
        string executablePath,
        string pipeName,
        CancellationTokenSource ownerCancellation)
    {
        NamedPipeServerStream? pipe = null;
        Process? process = null;
        try
        {
            pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            lock (_gate)
            {
                if (_disposed ||
                    ownerCancellation.IsCancellationRequested ||
                    !ReferenceEquals(_helperCancellation, ownerCancellation))
                {
                    return;
                }

                _helperPipe = pipe;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add(HelperArgument);
            startInfo.ArgumentList.Add(pipeName);
            process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            BandDiagnostics.Log($"CPU temperature elevated helper started pid={process.Id}");

            lock (_gate)
            {
                if (_disposed ||
                    ownerCancellation.IsCancellationRequested ||
                    !ReferenceEquals(_helperCancellation, ownerCancellation))
                {
                    return;
                }

                _helperProcess = process;
                _helperLaunchInProgress = false;
            }

            using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                ownerCancellation.Token);
            connectionTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(connectionTimeout.Token).ConfigureAwait(false);
            BandDiagnostics.Log("CPU temperature elevated helper connected");

            using var reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 128,
                leaveOpen: true);
            while (!ownerCancellation.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ownerCancellation.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Contains(','))
                {
                    string[] parts = line.Split(',');
                    if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double tempVal) &&
                        double.IsFinite(tempVal) && tempVal is >= 1 and <= 125)
                    {
                        Volatile.Write(ref _helperTemperature, tempVal);
                        Volatile.Write(ref _helperTemperatureTimestamp, Stopwatch.GetTimestamp());
                    }
                    if (parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double powerVal) &&
                        double.IsFinite(powerVal) && powerVal is > 0.5 and <= 2000)
                    {
                        Volatile.Write(ref _helperPower, powerVal);
                        Volatile.Write(ref _helperPowerTimestamp, Stopwatch.GetTimestamp());
                    }
                    ReaderReady?.Invoke(this, EventArgs.Empty);
                    BandDiagnostics.LogRateLimited(
                        "cpu-temperature-helper-value",
                        $"CPU telemetry source=ElevatedHelper temp={Volatile.Read(ref _helperTemperature):0.0}C power={Volatile.Read(ref _helperPower):0.0}W",
                        TimeSpan.FromMinutes(5));
                }
                else if (double.TryParse(
                        line,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double value) &&
                    double.IsFinite(value) &&
                    value is >= 1 and <= 125)
                {
                    Volatile.Write(ref _helperTemperature, value);
                    Volatile.Write(ref _helperTemperatureTimestamp, Stopwatch.GetTimestamp());
                    ReaderReady?.Invoke(this, EventArgs.Empty);
                    BandDiagnostics.LogRateLimited(
                        "cpu-temperature-helper-value",
                        $"CPU temperature source=ElevatedHelper value={value:0.0}C",
                        TimeSpan.FromMinutes(5));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // UAC cancellation (Win32 error 1223), policy denial, driver errors,
            // and pipe failures are deliberately non-fatal to the normal monitor.
            BandDiagnostics.LogRateLimited(
                "cpu-temperature-helper-failed",
                $"Elevated CPU temperature helper unavailable type={ex.GetType().Name}",
                TimeSpan.FromMinutes(5));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_helperCancellation, ownerCancellation))
                {
                    _helperCancellation = null;
                    _helperPipe = null;
                    _helperProcess = null;
                    _helperTask = null;
                    _helperLaunchInProgress = false;
                }
            }

            TryTerminate(process);
            process?.Dispose();
            pipe?.Dispose();
            ownerCancellation.Dispose();
        }
    }

    private Task? StopHelperLocked()
    {
        Task? task = _helperTask;
        _helperTask = null;
        _helperLaunchInProgress = false;
        Volatile.Write(ref _helperTemperatureTimestamp, 0);

        CancellationTokenSource? cancellation = _helperCancellation;
        _helperCancellation = null;
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _helperPipe?.Dispose();
        _helperPipe = null;
        TryTerminate(_helperProcess);
        _helperProcess = null;
        return task;
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
            // Closing the pipe is the reliable cross-integrity shutdown signal.
            // The elevated helper exits as soon as that pipe is broken.
        }
    }

    private static void WaitBrieflyForHelper(Task? helperTask)
    {
        if (helperTask is null)
        {
            return;
        }

        try
        {
            _ = helperTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    private void CloseLocked()
    {
        Computer? computer = _computer;
        _computer = null;
        if (computer is null)
        {
            return;
        }

        try
        {
            computer.Close();
        }
        catch
        {
        }
    }

    private sealed record TemperatureCandidate(string Name, double Value, int Priority);
    private sealed record PowerCandidate(string Name, double Value, int Priority);
}
