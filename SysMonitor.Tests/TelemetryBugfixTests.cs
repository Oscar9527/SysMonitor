using System.Diagnostics;
using System.Globalization;
using LibreHardwareMonitor.Hardware;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class TelemetryBugfixTests
{
    [Fact]
    public void CpuTemperatureReader_ReadWhenStopped_ReturnsNullWithoutSchedulingFallback()
    {
        using var reader = new CpuTemperatureReader(
            _ => new Computer(),
            _ => { },
            allowElevatedHelper: false);

        Assert.Null(reader.Read());
        Assert.False(reader.OpenInProgress);
        Assert.False(reader.MotherboardFallbackAttempted);
        Assert.Equal(0, reader.NextRetryTimestampForTests);

        reader.Start();
        Assert.True(SpinWait.SpinUntil(() => reader.HasOpenComputer, TimeSpan.FromSeconds(2)));

        reader.Stop();
        Assert.False(reader.HasOpenComputer);
        Assert.Null(reader.Read());
        Assert.Null(reader.ReadPower());
        Assert.False(reader.MotherboardFallbackAttempted);
        Assert.Equal(0, reader.NextRetryTimestampForTests);
    }

    [Fact]
    public void CpuTemperatureReader_OpenFailure_EscalatesOnceAndReadsDoNotPushRetryTimestamp()
    {
        int openCount = 0;
        using var reader = new CpuTemperatureReader(
            _ => new Computer(),
            _ =>
            {
                Interlocked.Increment(ref openCount);
                throw new UnauthorizedAccessException("Driver blocked");
            },
            allowElevatedHelper: false);

        reader.Start();
        Assert.True(SpinWait.SpinUntil(
            () => Volatile.Read(ref openCount) == 2 && !reader.OpenInProgress,
            TimeSpan.FromSeconds(2)));
        Assert.True(reader.MotherboardFallbackAttempted);

        long retryTimestamp = reader.NextRetryTimestampForTests;
        Assert.True(retryTimestamp > Stopwatch.GetTimestamp());

        for (int index = 0; index < 10; index++)
        {
            Assert.Null(reader.Read());
            Assert.Null(reader.ReadPower());
        }

        Assert.Equal(2, Volatile.Read(ref openCount));
        Assert.Equal(retryTimestamp, reader.NextRetryTimestampForTests);
    }

    [Theory]
    [InlineData("48.5,22.25", 48.5d, 22.25d)]
    [InlineData("NA,18.5", null, 18.5d)]
    [InlineData("51.2,NA", 51.2d, null)]
    [InlineData("NA,NA", null, null)]
    [InlineData("63.5", 63.5d, null)]
    [InlineData("NaN,Infinity", null, null)]
    [InlineData("0.5,0.5", null, null)]
    [InlineData("126,2001", null, null)]
    public void CpuTemperatureReader_ParseHelperTelemetryLine_IsCultureInvariantAndValidated(
        string line,
        double? expectedTemperature,
        double? expectedPower)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CpuTemperatureReader.ParseHelperTelemetryLine(
                line,
                out double? temperature,
                out double? powerWatts);

            Assert.Equal(expectedTemperature, temperature);
            Assert.Equal(expectedPower, powerWatts);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void NvidiaSmiCsv_TreatsBracketedNotSupportedAsUnavailable()
    {
        const string line =
            "2026/08/10 12:34:56.000, 0, [Not Supported], [Not Supported], [Not Supported], [Not Supported], [Not Supported], [Not Supported], [Not Supported], [Not Supported], [Not Supported], [Not Supported]";

        Assert.True(NvidiaSmiCsv.TryParseRow(line, out NvidiaSmiRow row));
        Assert.Null(row.Uuid);
        Assert.Null(row.PciBusId);
        Assert.Equal("NVIDIA graphics adapter", row.Name);
        Assert.Null(row.UsagePercent);
        Assert.Null(row.TemperatureCelsius);
        Assert.Null(row.MemoryUsedBytes);
        Assert.Null(row.MemoryTotalBytes);
        Assert.Null(row.CoreClockMhz);
        Assert.Null(row.MemoryClockMhz);
        Assert.Null(row.PowerWatts);
    }

    [Fact]
    public void NvidiaSmiCsv_ZeroMemoryTotalRemainsUnknown()
    {
        const string line =
            "2026/08/10 12:34:56.000, 0, GPU-0, 00000000:01:00.0, GPU, 10, 50, 0, 0, 1500, 5000, 45.5";

        Assert.True(NvidiaSmiCsv.TryParseRow(line, out NvidiaSmiRow row));
        Assert.Equal(0L, row.MemoryUsedBytes);
        Assert.Null(row.MemoryTotalBytes);
        Assert.Equal(45.5d, row.PowerWatts);
    }

    [Fact]
    public async Task MonitorService_DisposeThenStopIsIdempotentAndStartThrowsObjectDisposed()
    {
        var coordinator = new GpuTelemetryCoordinator(new InertGpuProvider(), new InertGpuProvider());
        var service = new MonitorService(
            new MonitorOptions(EnableLibreHardwareMonitor: false, EnableCpuTemperatureReader: false),
            coordinator,
            cpuTemperatureReader: null);

        await service.StartAsync();
        service.SetSamplingInterval(TimeSpan.FromMilliseconds(500));
        await Task.WhenAll(service.DisposeAsync().AsTask(), service.DisposeAsync().AsTask());
        await service.StopAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartAsync());
    }

    [Fact]
    public async Task GpuTelemetryCoordinator_ConcurrentReadAndStop_DoesNotThrow()
    {
        long now = 100 * Stopwatch.Frequency;
        var sample = new GpuProviderSample(
            "gpu-0",
            0,
            "Test GPU",
            GpuVendor.Amd,
            GpuTelemetrySource.LibreHardwareMonitor,
            null,
            null,
            null,
            null,
            50,
            60,
            100,
            1000,
            DateTimeOffset.UtcNow,
            now);
        var provider = new StaticGpuProvider(
            new GpuProviderCycle(GpuTelemetrySource.LibreHardwareMonitor, DateTimeOffset.UtcNow, now, [sample]));
        await using var coordinator = new GpuTelemetryCoordinator(new InertGpuProvider(), provider);

        using var cts = new CancellationTokenSource();
        Task readerTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                _ = coordinator.Read(now);
            }
        });

        for (int index = 0; index < 25; index++)
        {
            await coordinator.StartAsync(CancellationToken.None);
            await coordinator.StopAsync();
        }

        cts.Cancel();
        await readerTask;
        await coordinator.DisposeAsync();
    }

    [Fact]
    public void CpuUsageReader_RepeatedStartReadStopDispose_DoesNotThrow()
    {
        using var reader = new CpuUsageReader();
        reader.Start();
        _ = reader.Read();
        reader.Stop();
        Assert.Null(reader.Read());
        reader.Dispose();
        reader.Dispose();
        reader.Start();
        Assert.Null(reader.Read());
    }

    private sealed class InertGpuProvider : IGpuTelemetryProvider
    {
        public GpuProviderCycle? LatestCycle => null;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticGpuProvider : IGpuTelemetryProvider
    {
        private readonly GpuProviderCycle _cycle;

        internal StaticGpuProvider(GpuProviderCycle cycle) => _cycle = cycle;

        public GpuProviderCycle? LatestCycle => _cycle;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
