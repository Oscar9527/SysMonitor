using SysMonitor.UI;

namespace SysMonitor.Tests;

public sealed class DetailWindowShowPolicyTests
{
    [Fact]
    public void BandShowRaisesWithoutActivation()
    {
        DetailWindowShowPolicy policy = DetailWindow.SelectShowPolicy(fromBand: true);
        var requests = DetailWindow.SelectBandRaiseRequests(isTopmost: false);

        Assert.False(policy.Activate);
        Assert.True(policy.RaiseWithoutActivation);
        Assert.True(policy.RevealAfterLayout);
        Assert.Equal(2, requests.Length);
        Assert.Equal(new nint(-1), requests[0].InsertAfter);
        Assert.Equal(new nint(-2), requests[1].InsertAfter);
        Assert.All(requests, request => Assert.Equal(0x0013u, request.Flags));
        Assert.All(requests, request => Assert.Equal(0u, request.Flags & 0x0004u));
    }

    [Fact]
    public void PinnedBandShowRetainsTopmostZOrderWithoutActivation()
    {
        var requests = DetailWindow.SelectBandRaiseRequests(isTopmost: true);

        DetailWindowZOrderRequest request = Assert.Single(requests);
        Assert.Equal(new nint(-1), request.InsertAfter);
        Assert.Equal(0x0013u, request.Flags);
    }

    [Fact]
    public void TrayShowKeepsActivationAndSkipsNativeRaise()
    {
        DetailWindowShowPolicy policy = DetailWindow.SelectShowPolicy(fromBand: false);

        Assert.True(policy.Activate);
        Assert.False(policy.RaiseWithoutActivation);
        Assert.False(policy.RevealAfterLayout);
    }

    [Theory]
    [InlineData(0u, 1d)]
    [InlineData(96u, 1d)]
    [InlineData(144u, 1.5d)]
    [InlineData(192u, 2d)]
    public void PlacementDpiUsesBandWindowDpi(uint dpi, double expectedScale)
    {
        Assert.Equal(expectedScale, DetailWindow.ResolvePlacementDpiScale(dpi));
    }
}
