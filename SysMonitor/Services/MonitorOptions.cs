namespace SysMonitor.Services;

/// <summary>
/// Selects hardware providers before the monitoring service is constructed.
/// Game-safe sessions never construct compatibility providers that may load a
/// hardware-access driver or start an elevated helper.
/// </summary>
public sealed record MonitorOptions(
    bool EnableLibreHardwareMonitor,
    bool EnableCpuTemperatureReader,
    bool EnableSharedMemoryCpuTemperature)
{
    public static MonitorOptions GameSafe { get; } = new(false, false, true);

    public static MonitorOptions CompatibilitySensors { get; } = new(true, true, true);

    public static MonitorOptions FromGameSafeMode(bool gameSafeMode) =>
        gameSafeMode ? GameSafe : CompatibilitySensors;
}
