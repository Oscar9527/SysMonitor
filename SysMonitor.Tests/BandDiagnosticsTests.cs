using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class BandDiagnosticsTests
{
    [Fact]
    public void RateLimitedKeysUseTtlAndDeterministicBoundedEviction()
    {
        string prefix = $"retention-{Guid.NewGuid():N}-";
        DateTimeOffset insertedAt = DateTimeOffset.UtcNow;
        for (int index = 0; index < 1024; index++)
        {
            Assert.True(BandDiagnostics.TrackRateLimitedKeyForTests(
                $"{prefix}{index:0000}",
                TimeSpan.Zero,
                insertedAt.AddTicks(index)));
        }

        Assert.InRange(BandDiagnostics.RateLimitedKeyCount, 1, 512);
        Assert.True(BandDiagnostics.IsRateLimitedKeyTrackedForTests($"{prefix}1023"));

        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        var tiedEntries = new Dictionary<string, DateTimeOffset>
        {
            ["z"] = timestamp,
            ["a"] = timestamp,
            ["m"] = timestamp,
        };
        Assert.Equal("a", BandDiagnostics.SelectOldestRateLimitedKeyForTests(tiedEntries));
        Assert.True(BandDiagnostics.IsRateLimitedKeyExpiredForTests(
            timestamp.AddHours(24),
            timestamp));
        Assert.False(BandDiagnostics.IsRateLimitedKeyExpiredForTests(
            timestamp.AddHours(23),
            timestamp));
    }
}
