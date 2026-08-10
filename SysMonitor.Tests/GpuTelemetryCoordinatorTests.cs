using System.Diagnostics;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class GpuTelemetryCoordinatorTests
{
    [Fact]
    public void FreshNvidiaSmiCycleSuppressesAllLhmNvidiaAdapters()
    {
        long now = 100 * Stopwatch.Frequency;
        var smi = new FakeProvider(Cycle(GpuTelemetrySource.NvidiaSmi, now, Sample("smi", GpuVendor.Nvidia, GpuTelemetrySource.NvidiaSmi, 10)));
        var lhm = new FakeProvider(Cycle(
            GpuTelemetrySource.LibreHardwareMonitor,
            now,
            Sample("lhm-nv", GpuVendor.Nvidia, GpuTelemetrySource.LibreHardwareMonitor, 99),
            Sample("lhm-amd", GpuVendor.Amd, GpuTelemetrySource.LibreHardwareMonitor, 20)));
        var coordinator = new GpuTelemetryCoordinator(smi, lhm);

        Assert.Equal("lhm-amd", coordinator.Read(now)!.Name);
    }

    [Fact]
    public void SameModelNameWithoutComparableIdentityNeverMerges()
    {
        long now = 100 * Stopwatch.Frequency;
        GpuProviderSample smiSample = Sample("same", GpuVendor.Nvidia, GpuTelemetrySource.NvidiaSmi, 10) with
        {
            TemperatureCelsius = null,
        };
        GpuProviderSample lhmSample = Sample("same", GpuVendor.Nvidia, GpuTelemetrySource.LibreHardwareMonitor, 99) with
        {
            TemperatureCelsius = 77,
        };
        var coordinator = new GpuTelemetryCoordinator(
            new FakeProvider(Cycle(GpuTelemetrySource.NvidiaSmi, now, smiSample)),
            new FakeProvider(Cycle(GpuTelemetrySource.LibreHardwareMonitor, now, lhmSample)));

        var result = coordinator.Read(now);
        Assert.Equal(10, result!.UsagePercent);
        Assert.Null(result.TemperatureCelsius);
    }

    [Fact]
    public void ExactPciIdentityCanFillOnlyMissingMetrics()
    {
        long now = 100 * Stopwatch.Frequency;
        GpuProviderSample smiSample = Sample("smi", GpuVendor.Nvidia, GpuTelemetrySource.NvidiaSmi, 10) with
        {
            PciBusId = "00000000:01:00.0",
            TemperatureCelsius = null,
        };
        GpuProviderSample lhmSample = Sample("lhm", GpuVendor.Nvidia, GpuTelemetrySource.LibreHardwareMonitor, 99) with
        {
            PciBusId = "00000000:01:00.0",
            TemperatureCelsius = 72,
        };
        var coordinator = new GpuTelemetryCoordinator(
            new FakeProvider(Cycle(GpuTelemetrySource.NvidiaSmi, now, smiSample)),
            new FakeProvider(Cycle(GpuTelemetrySource.LibreHardwareMonitor, now, lhmSample)));

        var result = coordinator.Read(now);
        Assert.Equal(10, result!.UsagePercent);
        Assert.Equal(72, result.TemperatureCelsius);
    }

    [Fact]
    public void StaleSmiAllowsLhmNvidiaAndOutOfOrderCycleIsRejected()
    {
        long original = 100 * Stopwatch.Frequency;
        var smi = new FakeProvider(Cycle(GpuTelemetrySource.NvidiaSmi, original, Sample("smi", GpuVendor.Nvidia, GpuTelemetrySource.NvidiaSmi, 80)));
        var lhm = new FakeProvider(Cycle(GpuTelemetrySource.LibreHardwareMonitor, original + 3 * Stopwatch.Frequency, Sample("lhm", GpuVendor.Nvidia, GpuTelemetrySource.LibreHardwareMonitor, 25)));
        var coordinator = new GpuTelemetryCoordinator(smi, lhm);

        Assert.Equal("smi", coordinator.Read(original)!.Name);
        smi.Latest = Cycle(GpuTelemetrySource.NvidiaSmi, original - 1, Sample("older", GpuVendor.Nvidia, GpuTelemetrySource.NvidiaSmi, 99));
        long later = original + 3 * Stopwatch.Frequency;
        Assert.Equal("lhm", coordinator.Read(later)!.Name);
    }

    [Fact]
    public void ChallengerNeedsTwoConsecutiveDisplayTicks()
    {
        long now = 100 * Stopwatch.Frequency;
        var lhm = new FakeProvider(Cycle(GpuTelemetrySource.LibreHardwareMonitor, now, Sample("A", GpuVendor.Amd, GpuTelemetrySource.LibreHardwareMonitor, 60)));
        var coordinator = new GpuTelemetryCoordinator(new FakeProvider(null), lhm);
        Assert.Equal("A", coordinator.Read(now)!.Name);

        lhm.Latest = Cycle(
            GpuTelemetrySource.LibreHardwareMonitor,
            now + 1,
            Sample("A", GpuVendor.Amd, GpuTelemetrySource.LibreHardwareMonitor, 60),
            Sample("B", GpuVendor.Intel, GpuTelemetrySource.LibreHardwareMonitor, 70));
        Assert.Equal("A", coordinator.Read(now + 1)!.Name);
        Assert.Equal("B", coordinator.Read(now + 2)!.Name);
    }

    [Fact]
    public void NoUsageRetainsCurrentFreshAdapter()
    {
        long now = 100 * Stopwatch.Frequency;
        var lhm = new FakeProvider(Cycle(GpuTelemetrySource.LibreHardwareMonitor, now, Sample("B", GpuVendor.Amd, GpuTelemetrySource.LibreHardwareMonitor, 50)));
        var coordinator = new GpuTelemetryCoordinator(new FakeProvider(null), lhm);
        Assert.Equal("B", coordinator.Read(now)!.Name);

        lhm.Latest = Cycle(
            GpuTelemetrySource.LibreHardwareMonitor,
            now + 1,
            Sample("A", GpuVendor.Intel, GpuTelemetrySource.LibreHardwareMonitor, null),
            Sample("B", GpuVendor.Amd, GpuTelemetrySource.LibreHardwareMonitor, null));
        var retained = coordinator.Read(now + 1);
        Assert.Equal("B", retained!.Name);
        Assert.Null(retained.UsagePercent);
    }

    [Fact]
    public async Task LifecycleIsAwaitedAndIdempotent()
    {
        var smi = new FakeProvider(null);
        var lhm = new FakeProvider(null);
        var coordinator = new GpuTelemetryCoordinator(smi, lhm);

        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.StopAsync();
        await coordinator.StopAsync();
        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        Assert.Equal(1, smi.StartCount);
        Assert.Equal(1, lhm.StartCount);
        Assert.Equal(1, smi.StopCount);
        Assert.Equal(1, lhm.StopCount);
        Assert.Equal(1, smi.DisposeCount);
        Assert.Equal(1, lhm.DisposeCount);
    }

    private static GpuProviderCycle Cycle(
        GpuTelemetrySource source,
        long timestamp,
        params GpuProviderSample[] samples) =>
        new(source, DateTimeOffset.UtcNow, timestamp, samples);

    private static GpuProviderSample Sample(
        string id,
        GpuVendor vendor,
        GpuTelemetrySource source,
        double? usage) =>
        new(
            id,
            0,
            id,
            vendor,
            source,
            null,
            null,
            null,
            null,
            usage,
            60,
            100,
            1000,
            DateTimeOffset.UtcNow,
            0);

    private sealed class FakeProvider : IGpuTelemetryProvider
    {
        internal FakeProvider(GpuProviderCycle? latest) => Latest = latest;

        internal GpuProviderCycle? Latest { get; set; }
        internal int StartCount { get; private set; }
        internal int StopCount { get; private set; }
        internal int DisposeCount { get; private set; }
        public GpuProviderCycle? LatestCycle => Latest;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
