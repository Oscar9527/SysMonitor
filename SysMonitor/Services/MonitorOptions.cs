namespace SysMonitor.Services;

/// <summary>
/// Selects hardware providers before the monitoring service is constructed.
/// Game-safe sessions keep GPU compatibility providers disabled while retaining
/// the independent CPU-only temperature reader.
/// </summary>
public sealed record MonitorOptions(
    bool EnableLibreHardwareMonitor,
    bool EnableCpuTemperatureReader)
{
    public TimeSpan SamplingInterval { get; init; } = TimeSpan.FromSeconds(1);
    public static MonitorOptions GameSafe { get; } = new(false, true);

    public static MonitorOptions CompatibilitySensors { get; } = new(true, true);

    public static MonitorOptions FromGameSafeMode(bool gameSafeMode) =>
        gameSafeMode ? GameSafe : CompatibilitySensors;
}
