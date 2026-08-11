using SysMonitor.Models;

namespace SysMonitor.Tests;

public sealed class GpuCapabilityStabilizerTests
{
    [Fact]
    public void RequiresTwoConsecutivePresentSamplesAndTransitionsOnce()
    {
        var stabilizer = new GpuCapabilityStabilizer();

        Assert.False(stabilizer.Observe(true));
        Assert.False(stabilizer.IsCapable);
        Assert.True(stabilizer.Observe(true));
        Assert.True(stabilizer.IsCapable);
        Assert.False(stabilizer.Observe(true));
    }

    [Fact]
    public void RequiresFiveConsecutiveMissingSamplesAndTransitionsOnce()
    {
        var stabilizer = new GpuCapabilityStabilizer();
        _ = stabilizer.Observe(true);
        _ = stabilizer.Observe(true);

        for (int index = 0; index < 4; index++)
        {
            Assert.False(stabilizer.Observe(false));
            Assert.True(stabilizer.IsCapable);
        }

        Assert.True(stabilizer.Observe(false));
        Assert.False(stabilizer.IsCapable);
        Assert.False(stabilizer.Observe(false));
    }

    [Fact]
    public void OppositeSampleBreaksConsecutiveRun()
    {
        var stabilizer = new GpuCapabilityStabilizer();

        Assert.False(stabilizer.Observe(true));
        Assert.False(stabilizer.Observe(false));
        Assert.False(stabilizer.Observe(true));
        Assert.True(stabilizer.Observe(true));
    }
}
