using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class OverlayMonitorIdentityResolverTests
{
    [Fact]
    public void ResolverReturnsAValidPrimaryMonitorSnapshot()
    {
        var resolver = new OverlayMonitorIdentityResolver();

        Assert.True(resolver.TryResolveForWindow(nint.Zero, out OverlayMonitorIdentity identity));
        Assert.True(identity.IsValid);
        Assert.True(identity.Bounds.Width > 0);
        Assert.True(identity.Bounds.Height > 0);
        Assert.StartsWith(@"\\.\DISPLAY", identity.GdiDeviceName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StablePathMatchesRegardlessOfNativePathCaseAndWhitespace()
    {
        ScreenPixelBounds bounds = new(-1920, 0, 0, 1080);
        OverlayMonitorIdentity requested = OverlayMonitorIdentity.CreateStable(
            "  \\?\\display#abc#123  ",
            "\\\\.\\DISPLAY2",
            "Panel",
            bounds);
        OverlayMonitorIdentity candidate = OverlayMonitorIdentity.CreateStable(
            "\\?\\DISPLAY#ABC#123",
            "\\\\.\\DISPLAY2",
            "Panel (renamed)",
            bounds);

        Assert.True(OverlayMonitorIdentityMatcher.TryMatch(requested, [candidate], out OverlayMonitorIdentity match));
        Assert.Equal(candidate, match);
        Assert.False(match.IsFallback);
    }

    [Fact]
    public void FallbackRequiresExactGdiNameAndFullBounds()
    {
        OverlayMonitorIdentity requested = OverlayMonitorIdentity.CreateFallback(
            "\\\\.\\DISPLAY3",
            "Display 3",
            new ScreenPixelBounds(-2560, -100, 0, 1340));
        OverlayMonitorIdentity same = OverlayMonitorIdentity.CreateFallback(
            "  \\\\.\\display3 ",
            "Display 3 (renamed)",
            new ScreenPixelBounds(-2560, -100, 0, 1340));
        OverlayMonitorIdentity shifted = OverlayMonitorIdentity.CreateFallback(
            "\\\\.\\DISPLAY3",
            "Display 3",
            new ScreenPixelBounds(-2560, -99, 0, 1340));

        Assert.True(OverlayMonitorIdentityMatcher.TryMatch(requested, [same], out _));
        Assert.False(OverlayMonitorIdentityMatcher.TryMatch(requested, [shifted], out _));
    }

    [Fact]
    public void RenamedOrMissingStablePathDoesNotMatchFallbackOrAnotherPath()
    {
        OverlayMonitorIdentity requested = OverlayMonitorIdentity.CreateStable(
            "\\?\\DISPLAY#OLD#1",
            "\\\\.\\DISPLAY1",
            "Old panel",
            new ScreenPixelBounds(0, 0, 1920, 1080));
        OverlayMonitorIdentity renamed = OverlayMonitorIdentity.CreateStable(
            "\\?\\DISPLAY#NEW#2",
            "\\\\.\\DISPLAY1",
            "New panel",
            requested.Bounds);
        OverlayMonitorIdentity fallback = OverlayMonitorIdentity.CreateFallback(
            "\\\\.\\DISPLAY1",
            "New panel",
            requested.Bounds);

        Assert.False(OverlayMonitorIdentityMatcher.TryMatch(requested, [renamed], out _));
        Assert.False(OverlayMonitorIdentityMatcher.TryMatch(requested, [fallback], out _));
        Assert.False(OverlayMonitorIdentityMatcher.TryMatch(requested, Array.Empty<OverlayMonitorIdentity>(), out _));
    }

    [Fact]
    public void DuplicateStableIdsFailClosed()
    {
        OverlayMonitorIdentity requested = OverlayMonitorIdentity.CreateStable(
            "\\?\\DISPLAY#DUPLICATE",
            "\\\\.\\DISPLAY1",
            "Panel",
            new ScreenPixelBounds(0, 0, 1920, 1080));
        OverlayMonitorIdentity duplicateOne = OverlayMonitorIdentity.CreateStable(
            "\\?\\DISPLAY#DUPLICATE",
            "\\\\.\\DISPLAY1",
            "Panel",
            requested.Bounds);
        OverlayMonitorIdentity duplicateTwo = OverlayMonitorIdentity.CreateStable(
            "\\?\\display#duplicate",
            "\\\\.\\DISPLAY2",
            "Panel",
            new ScreenPixelBounds(1920, 0, 3840, 1080));

        Assert.False(OverlayMonitorIdentityMatcher.TryMatch(requested, [duplicateOne, duplicateTwo], out _));
    }

    [Fact]
    public void NegativePhysicalBoundsArePreservedInFallbackIdentity()
    {
        ScreenPixelBounds bounds = new(-3840, -1440, -1920, 0);
        OverlayMonitorIdentity identity = OverlayMonitorIdentity.CreateFallback(
            "\\\\.\\DISPLAY4",
            null,
            bounds);

        Assert.True(identity.IsFallback);
        Assert.Equal(bounds, identity.Bounds);
        Assert.Contains("-3840,-1440,-1920,0", identity.StableMonitorId, StringComparison.Ordinal);
        Assert.Contains("DISPLAY4", identity.StableMonitorId, StringComparison.Ordinal);
    }
}
