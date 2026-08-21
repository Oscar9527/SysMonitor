using SysMonitor.Models;

namespace SysMonitor.Tests;

public sealed class BandLayoutTests
{
    [Fact]
    public void GroupsRemainInCanonicalOrderAndSeparatorsAreAdjacentCount()
    {
        var visibility = new BandMetricVisibility(true, false, true, false, true, true);

        EffectiveBandLayout layout = EffectiveBandLayout.Create(
            visibility, compact: false, wide: false, gpuCapable: true, itemSpacingDip: 10);

        Assert.Equal(
            new[] { BandMetric.Cpu, BandMetric.Gpu, BandMetric.Upload, BandMetric.SystemDisk },
            layout.ActiveGroups);
        Assert.Equal(3, layout.SeparatorCount);
    }

    [Fact]
    public void CompactAlwaysOmitsDiskAndCanShowSingleMetric()
    {
        var diskOnly = new BandMetricVisibility(false, false, false, false, false, true);
        var cpuOnly = new BandMetricVisibility(true, false, false, false, false, false);

        EffectiveBandLayout hidden = EffectiveBandLayout.Create(
            diskOnly, compact: true, wide: false, gpuCapable: true, itemSpacingDip: 10);
        EffectiveBandLayout single = EffectiveBandLayout.Create(
            cpuOnly, compact: true, wide: false, gpuCapable: false, itemSpacingDip: 10);

        Assert.False(hidden.HasVisibleGroups);
        Assert.Equal(new[] { BandMetric.Cpu }, single.ActiveGroups);
        Assert.Equal(0, single.SeparatorCount);
        Assert.True(single.TargetWidthDip < 320);
    }

    [Fact]
    public void GpuRequiresBothUserVisibilityAndStableCapability()
    {
        BandMetricVisibility gpuOnly = new(false, false, true, false, false, false);

        EffectiveBandLayout userHiddenBefore = EffectiveBandLayout.Create(
            gpuOnly with { Gpu = false }, false, false, false, 10);
        EffectiveBandLayout userHiddenAfter = EffectiveBandLayout.Create(
            gpuOnly with { Gpu = false }, false, false, true, 10);

        Assert.False(EffectiveBandLayout.Create(gpuOnly, false, false, false, 10).HasVisibleGroups);
        Assert.True(EffectiveBandLayout.Create(gpuOnly, false, false, true, 10).HasVisibleGroups);
        Assert.False(userHiddenAfter.HasVisibleGroups);
        Assert.Equal(userHiddenBefore, userHiddenAfter);
    }

    [Fact]
    public void WidthGrowsMonotonicallyWithVisibleGroupsAndSpacing()
    {
        EffectiveBandLayout one = EffectiveBandLayout.Create(
            new BandMetricVisibility(true, false, false, false, false, false),
            false, false, false, 4);
        EffectiveBandLayout two = EffectiveBandLayout.Create(
            new BandMetricVisibility(true, true, false, false, false, false),
            false, false, false, 4);
        EffectiveBandLayout widerSpacing = EffectiveBandLayout.Create(
            new BandMetricVisibility(true, true, false, false, false, false),
            false, false, false, 12);

        Assert.True(two.TargetWidthDip > one.TargetWidthDip);
        Assert.True(widerSpacing.TargetWidthDip > two.TargetWidthDip);
    }

    [Fact]
    public void EquivalentInputsProduceEqualDescriptor()
    {
        BandMetricVisibility visibility = new(true, true, true, true, true, true);

        EffectiveBandLayout first = EffectiveBandLayout.Create(visibility, false, true, true, 10);
        EffectiveBandLayout second = EffectiveBandLayout.Create(visibility, false, true, true, 10);

        Assert.Equal(first, second);
    }

    [Fact]
    public void PowerMetricsIncreaseAllocatedWidth()
    {
        var standard = new BandMetricVisibility(true, false, true, false, false, false);
        var withCpuPower = standard with { CpuPower = true };
        var withBothPower = standard with { CpuPower = true, GpuPower = true };

        EffectiveBandLayout layoutStd = EffectiveBandLayout.Create(standard, false, false, true, 4);
        EffectiveBandLayout layoutCpuPwr = EffectiveBandLayout.Create(withCpuPower, false, false, true, 4);
        EffectiveBandLayout layoutBothPwr = EffectiveBandLayout.Create(withBothPower, false, false, true, 4);

        Assert.True(layoutCpuPwr.TargetWidthDip > layoutStd.TargetWidthDip);
        Assert.True(layoutBothPwr.TargetWidthDip > layoutCpuPwr.TargetWidthDip);
    }
}
