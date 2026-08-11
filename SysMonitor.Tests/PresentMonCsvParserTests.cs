using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class PresentMonCsvParserTests
{
    [Fact]
    public void RequiresExactTenColumnHeader()
    {
        Assert.True(PresentMonCsvParser.IsExpectedHeader(
            "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped," +
            "TimeInSeconds,msInPresentAPI,msBetweenPresents"));
        Assert.False(PresentMonCsvParser.IsExpectedHeader(
            "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped," +
            "TimeInSeconds,msBetweenPresents,msInPresentAPI"));
    }

    [Fact]
    public void ApplicationMayContainCommaBecausePidAndAddressAnchorTheRow()
    {
        const string line = "Game, Deluxe.exe,4242,0x000000000000CAFE,DXGI,1,0,0,12.5,0.2,16.666";

        Assert.True(PresentMonCsvParser.TryParseFrame(line, 4242, out PresentMonFrame frame));
        Assert.Equal("Game, Deluxe.exe", frame.Application);
        Assert.Equal(0xCAFEul, frame.SwapChainAddress);
        Assert.Equal(12.5, frame.TimeInSeconds);
        Assert.Equal(16.666, frame.MillisecondsBetweenPresents);
    }

    [Theory]
    [InlineData("Game.exe,42,0x1,DXGI,1,0,0,NaN,0.1,16")]
    [InlineData("Game.exe,42,0x1,DXGI,1,0,0,1,0.1,Infinity")]
    [InlineData("Game.exe,42,0xNOPE,DXGI,1,0,0,1,0.1,16")]
    [InlineData("Game.exe,43,0x1,DXGI,1,0,0,1,0.1,16")]
    [InlineData("Game.exe,42,0x1,Vulkan,1,0,0,1,0.1,16")]
    [InlineData("Game.exe,42,0x1,DXGI,1,0,Maybe,1,0.1,16")]
    public void RejectsInvalidRuntimeValuesAndWrongTarget(string line)
    {
        Assert.False(PresentMonCsvParser.TryParseFrame(line, 42, out _));
    }

    [Fact]
    public async Task BoundedLineReaderRejectsOversizedRows()
    {
        var reader = new PresentMonBoundedLineReader(
            new StringReader(new string('x', PresentMonCsvParser.MaximumLineLength + 1) + "\n"));

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync(CancellationToken.None));
    }
}
