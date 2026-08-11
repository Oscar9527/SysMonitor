using SysMonitor.UI;

namespace SysMonitor.Tests;

public sealed class DetailWindowShowPolicyTests
{
    [Fact]
    public void BandShowRaisesWithoutActivation()
    {
        DetailWindowShowPolicy policy = DetailWindow.SelectShowPolicy(fromBand: true);
        DetailWindowZOrderRequest request = DetailWindow.SelectBandRaiseRequest(isTopmost: false);

        Assert.False(policy.Activate);
        Assert.True(policy.RaiseWithoutActivation);
        Assert.Equal(nint.Zero, request.InsertAfter);
        Assert.Equal(0x0013u, request.Flags);
        Assert.Equal(0u, request.Flags & 0x0004u);
    }

    [Fact]
    public void PinnedBandShowRetainsTopmostZOrderWithoutActivation()
    {
        DetailWindowZOrderRequest request =
            DetailWindow.SelectBandRaiseRequest(isTopmost: true);

        Assert.Equal(new nint(-1), request.InsertAfter);
        Assert.Equal(0x0013u, request.Flags);
    }

    [Fact]
    public void TrayShowKeepsActivationAndSkipsNativeRaise()
    {
        DetailWindowShowPolicy policy = DetailWindow.SelectShowPolicy(fromBand: false);

        Assert.True(policy.Activate);
        Assert.False(policy.RaiseWithoutActivation);
    }
}
