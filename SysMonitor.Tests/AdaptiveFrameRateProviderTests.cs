using SysMonitor.Models;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class AdaptiveFrameRateProviderTests
{
    [Fact]
    public async Task ExistingRtssSampleDoesNotStartPresentMonFallback()
    {
        var rtss = new FakeRtssSource
        {
            Result = SharedMemoryValue.Present(59.9, "RTSS rolling FPS")
        };
        var fallback = new FakeFrameRateProvider();
        await using var provider = new AdaptiveFrameRateProvider(rtss, fallback);

        await provider.StartAsync(456);
        await WaitUntilAsync(
            () => provider.Latest.Source == FrameRateSource.RtssSharedMemory,
            TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(1250));

        Assert.Equal(59.9, provider.Latest.PresentFps);
        Assert.Equal(0, fallback.StartCount);
    }

    [Fact]
    public async Task FallsBackRecoversAndStartIsIdempotent()
    {
        var rtss = new FakeRtssSource();
        var fallback = new FakeFrameRateProvider();
        await using var provider = new AdaptiveFrameRateProvider(rtss, fallback);

        await provider.StartAsync(123);
        await provider.StartAsync(123);
        await WaitUntilAsync(() => fallback.StartCount == 1, TimeSpan.FromSeconds(3));
        Assert.Equal(FrameRateSource.PresentMon, provider.Latest.Source);
        Assert.Equal(77, provider.Latest.PresentFps);

        rtss.Result = SharedMemoryValue.Present(144.4, "RTSS rolling FPS");
        await WaitUntilAsync(
            () => provider.Latest.Source == FrameRateSource.RtssSharedMemory,
            TimeSpan.FromSeconds(2));
        Assert.Equal(144.4, provider.Latest.PresentFps);
        Assert.True(fallback.StopCount >= 1);

        await provider.StopAsync();
        await provider.StopAsync();
        Assert.Equal(FrameRateStatus.Disabled, provider.Latest.Status);
        Assert.Equal(1, fallback.StartCount);
    }

    [Fact]
    public void GameSafeOptionsKeepGpuCompatibilityOffButEnableIndependentCpuTemperature()
    {
        MonitorOptions options = MonitorOptions.GameSafe;

        Assert.True(options.EnableCpuTemperatureReader);
        Assert.False(options.EnableLibreHardwareMonitor);
    }

    [Fact]
    public void CompatibilityOptionsEnableBothHardwareSensorReaders()
    {
        MonitorOptions options = MonitorOptions.FromGameSafeMode(gameSafeMode: false);

        Assert.True(options.EnableCpuTemperatureReader);
        Assert.True(options.EnableLibreHardwareMonitor);
    }

    [Fact]
    public async Task FactoryCreatesAdaptiveProvider()
    {
        await using IFrameRateProvider provider = GameOverlayFrameRateProviderFactory.Create();

        Assert.IsType<AdaptiveFrameRateProvider>(provider);
    }

    [Fact]
    public async Task StopBeforeFallbackDelayPreventsLatePresentMonStart()
    {
        var fallback = new FakeFrameRateProvider();
        await using var provider = new AdaptiveFrameRateProvider(new FakeRtssSource(), fallback);

        await provider.StartAsync(789);
        await provider.StopAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(1250));

        Assert.Equal(0, fallback.StartCount);
        Assert.Equal(FrameRateStatus.Disabled, provider.Latest.Status);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!predicate())
        {
            await Task.Delay(25, cancellation.Token);
        }
    }

    private sealed class FakeRtssSource : IRtssFrameSource
    {
        internal SharedMemoryValue Result { get; set; } = SharedMemoryValue.Missing("not found");
        public SharedMemoryValue Read(int processId) => Result;
    }

    private sealed class FakeFrameRateProvider : IFrameRateProvider
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public FrameRateSnapshot Latest { get; private set; } = FrameRateSnapshot.Disabled;
        public event EventHandler<FrameRateSnapshot>? SnapshotUpdated;

        public Task StartAsync(int processId, CancellationToken cancellationToken = default)
        {
            StartCount++;
            Latest = new FrameRateSnapshot(
                77,
                FrameRateStatus.Active,
                processId,
                DateTimeOffset.UtcNow,
                null,
                FrameRateSource.PresentMon);
            SnapshotUpdated?.Invoke(this, Latest);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCount++;
            Latest = FrameRateSnapshot.Disabled;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
