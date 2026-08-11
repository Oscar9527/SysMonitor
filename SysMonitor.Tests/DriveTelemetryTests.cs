using System.Collections.Immutable;
using SysMonitor.Models;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class DriveTelemetryTests
{
    [Fact]
    public void SuccessfulEnumeration_FiltersAndSortsSystemDriveFirst()
    {
        var cache = new DriveTelemetryCache("d:\\Windows");

        cache.ApplySuccessfulEnumeration(
        [
            Success("E:\\", total: 500, free: 200),
            Success("D:\\", total: 100, free: 25),
            Success("C:\\", total: 200, free: 100),
            Success("F:\\", total: 0, free: 0),
            new DriveTelemetryObservation("G:\\", false, true, true, "USB", 100, 50),
            new DriveTelemetryObservation("H:\\", true, false, true, string.Empty, 100, 50),
        ]);

        Assert.Equal(["D:", "C:", "E:"], cache.Current.Select(item => item.Name));
        Assert.True(cache.Current[0].IsSystemDrive);
        Assert.All(cache.Current.Skip(1), item => Assert.False(item.IsSystemDrive));
    }

    [Theory]
    [InlineData(-10, 100, 100, 100)]
    [InlineData(200, 100, 0, 0)]
    [InlineData(25, 100, 75, 75)]
    public void SuccessfulEnumeration_ClampsFreeUsedAndPercent(
        long free,
        long total,
        long expectedUsed,
        double expectedPercent)
    {
        var cache = new DriveTelemetryCache("C:");

        cache.ApplySuccessfulEnumeration([Success("C:", total, free)]);

        DriveSnapshot drive = Assert.Single(cache.Current);
        Assert.Equal(expectedUsed, drive.UsedBytes);
        Assert.Equal(expectedPercent, drive.UsagePercent);
        Assert.InRange(drive.UsagePercent, 0d, 100d);
    }

    [Fact]
    public void GlobalFailure_KeepsLastSuccessfulSnapshot()
    {
        var cache = new DriveTelemetryCache("C:");
        cache.ApplySuccessfulEnumeration([Success("C:", 100, 40)]);
        ImmutableArray<DriveSnapshot> before = cache.Current;

        cache.ApplyGlobalFailure();

        Assert.Equal(before, cache.Current);
    }

    [Fact]
    public void PropertyFailure_AllowsTwoCyclesThenRemovesDrive()
    {
        var cache = new DriveTelemetryCache("C:");
        cache.ApplySuccessfulEnumeration([Success("C:", 100, 40)]);

        cache.ApplySuccessfulEnumeration([Failed("C:")]);
        Assert.Single(cache.Current);
        cache.ApplySuccessfulEnumeration([Failed("C:")]);
        Assert.Single(cache.Current);
        cache.ApplySuccessfulEnumeration([Failed("C:")]);
        Assert.Empty(cache.Current);
    }

    [Fact]
    public void PropertyFailure_RecoveryResetsGraceCounter()
    {
        var cache = new DriveTelemetryCache("C:");
        cache.ApplySuccessfulEnumeration([Success("C:", 100, 40)]);
        cache.ApplySuccessfulEnumeration([Failed("C:")]);
        cache.ApplySuccessfulEnumeration([Failed("C:")]);

        cache.ApplySuccessfulEnumeration([Success("C:", 200, 50)]);
        cache.ApplySuccessfulEnumeration([Failed("C:")]);
        cache.ApplySuccessfulEnumeration([Failed("C:")]);

        DriveSnapshot drive = Assert.Single(cache.Current);
        Assert.Equal(150, drive.UsedBytes);
    }

    [Fact]
    public void MissingOrNotReadyDrive_IsRemovedImmediately()
    {
        var cache = new DriveTelemetryCache("C:");
        cache.ApplySuccessfulEnumeration(
        [
            Success("C:", 100, 40),
            Success("D:", 100, 40),
        ]);

        cache.ApplySuccessfulEnumeration(
        [
            new DriveTelemetryObservation("C:", true, false, true, string.Empty, 0, 0),
        ]);

        Assert.Empty(cache.Current);
    }

    [Fact]
    public void MonitorSnapshot_DefaultFixedDrives_IsAlwaysSafeAndImmutable()
    {
        MonitorSnapshot snapshot = MonitorSnapshot.Empty with { FixedDrives = default };

        Assert.False(snapshot.FixedDrives.IsDefault);
        Assert.Empty(snapshot.FixedDrives);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<DriveSnapshot>)snapshot.FixedDrives).Add(
                new DriveSnapshot("C:", string.Empty, 0, 1, 0, true)));
    }

    private static DriveTelemetryObservation Success(
        string name,
        long total,
        long free) =>
        new(name, true, true, true, name + " label", total, free);

    private static DriveTelemetryObservation Failed(string name) =>
        new(name, true, true, false, string.Empty, 0, 0);
}
