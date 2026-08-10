using SysMonitor.Models;

namespace SysMonitor.Services;

public interface IMonitorService : IAsyncDisposable
{
    event EventHandler<MonitorSnapshot>? SnapshotUpdated;
    MonitorSnapshot Latest { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}
