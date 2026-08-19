using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class CpuFrequencyReaderTests
{
    [Fact]
    public void AveragesOnlyValidCurrentMhzValues()
    {
        Assert.Equal(2500d, CpuFrequencyReader.AverageValidCurrentMhz(new uint[] { 2000, 0, 3000 }));
    }

    [Fact]
    public void MissingOrImplausibleValuesRemainUnknown()
    {
        Assert.Null(CpuFrequencyReader.AverageValidCurrentMhz(Array.Empty<uint>()));
        Assert.Null(CpuFrequencyReader.AverageValidCurrentMhz(new uint[] { 0, uint.MaxValue }));
    }

    [Fact]
    public void NativeReadNeverFabricatesNonpositiveFrequency()
    {
        double? frequency = new CpuFrequencyReader().ReadCurrentMhz();
        Assert.True(frequency is null or > 0d);
    }
}
