using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class MonitorServiceTests
{
    [Fact]
    public async Task StartAsync_WhenGpuStartupFails_CleansUpAndAllowsRetry()
    {
        var nvidia = new RecordingGpuProvider();
        var libreHardwareMonitor = new RecordingGpuProvider(
            new InvalidOperationException("GPU startup failed."));
        var coordinator = new GpuTelemetryCoordinator(nvidia, libreHardwareMonitor);
        await using var service = new MonitorService(
            new MonitorOptions(EnableLibreHardwareMonitor: false, EnableCpuTemperatureReader: false),
            coordinator,
            cpuTemperatureReader: null);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync());
        Assert.Equal("GPU startup failed.", exception.Message);
        Assert.Equal(1, nvidia.StartCount);
        Assert.Equal(1, nvidia.StopCount);

        await service.StartAsync();
        Assert.Equal(2, nvidia.StartCount);
        Assert.Equal(2, libreHardwareMonitor.StartCount);

        await service.StopAsync();
        Assert.Equal(2, nvidia.StopCount);
        Assert.Equal(1, libreHardwareMonitor.StopCount);
    }

    private sealed class RecordingGpuProvider : IGpuTelemetryProvider
    {
        private readonly Exception? _firstStartException;

        internal RecordingGpuProvider(Exception? firstStartException = null) =>
            _firstStartException = firstStartException;

        internal int StartCount { get; private set; }
        internal int StopCount { get; private set; }
        public GpuProviderCycle? LatestCycle => null;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            if (StartCount == 1 && _firstStartException is not null)
            {
                throw _firstStartException;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
