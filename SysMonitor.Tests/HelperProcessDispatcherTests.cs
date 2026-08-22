using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class HelperProcessDispatcherTests
{
    [Fact]
    public void Classify_AcceptsExactCpuTemperatureRequest()
    {
        string pipe = "SysMonitor.CpuTemperature." + Guid.NewGuid().ToString("N");

        Assert.Equal(
            HelperProcessKind.CpuTemperature,
            HelperProcessDispatcher.Classify(["--cpu-temperature-helper", pipe]));
    }

    [Theory]
    [InlineData()]
    [InlineData("--cpu-temperature-helper")]
    [InlineData("--cpu-temperature-helper", "bad-pipe")]
    [InlineData("--cpu-temperature-helper", "SysMonitor.CpuTemperature.00000000000000000000000000000000", "extra")]
    public void Classify_RejectsMalformedCpuTemperatureRequest(params string[] arguments)
    {
        Assert.Equal(HelperProcessKind.None, HelperProcessDispatcher.Classify(arguments));
    }

    [Fact]
    public void Classify_AcceptsExactPresentMonRequest()
    {
        string suffix = Guid.NewGuid().ToString("N");

        Assert.Equal(
            HelperProcessKind.PresentMon,
            HelperProcessDispatcher.Classify(
                ["--presentmon-helper", $"SysMonitor.PresentMon.{suffix}", "123", $"SysMonitor-123-{suffix}"]));
    }

    [Theory]
    [InlineData("--presentmon-helper")]
    [InlineData("--presentmon-helper", "bad-pipe", "123", "bad-session")]
    [InlineData("--presentmon-helper", "SysMonitor.PresentMon.00000000000000000000000000000000", "0", "SysMonitor-00000000000000000000000000000000")]
    public void Classify_RejectsMalformedPresentMonRequest(params string[] arguments)
    {
        Assert.Equal(HelperProcessKind.None, HelperProcessDispatcher.Classify(arguments));
    }
}
