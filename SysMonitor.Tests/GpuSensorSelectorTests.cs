using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class GpuSensorSelectorTests
{
    [Fact]
    public void NvidiaUsesOnlyExactGpuCoreLoadAndTemperature()
    {
        GpuSensorSelection selected = GpuSensorSelector.Select(
            GpuVendor.Nvidia,
            new[]
            {
                new GpuSensorReading("GPU Controller", GpuSensorKind.Load, 99),
                new GpuSensorReading("GPU Core", GpuSensorKind.Load, 42),
                new GpuSensorReading("GPU Hot Spot", GpuSensorKind.Temperature, 105),
                new GpuSensorReading("GPU Core", GpuSensorKind.Temperature, 64),
            });

        Assert.Equal(42, selected.UsagePercent);
        Assert.Equal(64, selected.TemperatureCelsius);
    }

    [Fact]
    public void AmdDoesNotFallBackToControllerOrVideoLoads()
    {
        GpuSensorSelection selected = GpuSensorSelector.Select(
            GpuVendor.Amd,
            new[]
            {
                new GpuSensorReading("GPU Controller", GpuSensorKind.Load, 80),
                new GpuSensorReading("GPU Video Engine", GpuSensorKind.Load, 70),
            });

        Assert.Null(selected.UsagePercent);
    }

    [Fact]
    public void IntelUsesMaximumD3dEngineAndExcludesOtherLoads()
    {
        GpuSensorSelection selected = GpuSensorSelector.Select(
            GpuVendor.Intel,
            new[]
            {
                new GpuSensorReading("GPU Core", GpuSensorKind.Load, 95),
                new GpuSensorReading("D3D 3D", GpuSensorKind.Load, 30),
                new GpuSensorReading("GPU D3D Copy", GpuSensorKind.Load, 55),
            });

        Assert.Equal(55, selected.UsagePercent);
    }

    [Fact]
    public void MemoryUsesMiBAndCanDeriveUsedFromTotalMinusFree()
    {
        GpuSensorSelection selected = GpuSensorSelector.Select(
            GpuVendor.Amd,
            new[]
            {
                new GpuSensorReading("GPU Memory Total", GpuSensorKind.SmallData, 8192),
                new GpuSensorReading("GPU Memory Free", GpuSensorKind.SmallData, 6144),
                new GpuSensorReading("D3D Shared Memory Used", GpuSensorKind.SmallData, 999),
            });

        Assert.Equal(2048L * 1024 * 1024, selected.DedicatedMemoryUsedBytes);
        Assert.Equal(8192L * 1024 * 1024, selected.DedicatedMemoryTotalBytes);
    }

    [Fact]
    public void DedicatedD3dIsUsedOnlyFallbackAndSharedIsExcluded()
    {
        GpuSensorSelection selected = GpuSensorSelector.Select(
            GpuVendor.Intel,
            new[]
            {
                new GpuSensorReading("D3D Shared Memory Used", GpuSensorKind.SmallData, 900),
                new GpuSensorReading("D3D Dedicated Memory Used", GpuSensorKind.SmallData, 128.5),
            });

        Assert.Equal(134742016, selected.DedicatedMemoryUsedBytes);
        Assert.Null(selected.DedicatedMemoryTotalBytes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidMiBValuesRemainUnknown(double value)
    {
        Assert.Null(GpuSensorSelector.MiBToBytes(value));
    }

    [Fact]
    public void VeryLargeFiniteMiBValueSaturatesSafely()
    {
        Assert.Equal(long.MaxValue, GpuSensorSelector.MiBToBytes(1e13));
    }

    [Fact]
    public void ZeroCoreTemperatureAndZeroMemoryTotalRemainUnknown()
    {
        GpuSensorSelection selected = GpuSensorSelector.Select(
            GpuVendor.Amd,
            new[]
            {
                new GpuSensorReading("GPU Core", GpuSensorKind.Temperature, 0),
                new GpuSensorReading("GPU Memory Total", GpuSensorKind.SmallData, 0),
            });

        Assert.Null(selected.TemperatureCelsius);
        Assert.Null(selected.DedicatedMemoryTotalBytes);
    }

    [Fact]
    public void CompatibilityClocksUseOnlyExactCoreAndMemoryNames()
    {
        GpuSensorSelection selected = GpuSensorSelector.Select(
            GpuVendor.Amd,
            new[]
            {
                new GpuSensorReading("GPU Core", GpuSensorKind.Clock, 2100),
                new GpuSensorReading("GPU Memory", GpuSensorKind.Clock, 1750),
                new GpuSensorReading("GPU Shader", GpuSensorKind.Clock, 9999),
                new GpuSensorReading("GPU Memory Effective", GpuSensorKind.Clock, 14000),
            });

        Assert.Equal(2100, selected.CoreClockMhz);
        Assert.Equal(1750, selected.MemoryClockMhz);
    }
}
