using System.Buffers.Binary;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class RtssSharedMemoryParserTests
{
    [Fact]
    public void Version221FixtureUsesRollingFpsForExactPid()
    {
        uint tick = 50_000;
        byte[] data = CreateFixture(12_416, 2);
        WriteEntry(data, 0, 111, tick - 1_000, tick - 10, frames: 100, frameTime: 20_000, rollingTenths: 599);
        WriteEntry(data, 1, 222, tick - 1_000, tick - 5, frames: 200, frameTime: 10_000, rollingTenths: 1444);

        SharedMemoryValue result = RtssSharedMemoryParser.Parse(data, 222, tick);

        Assert.Equal(144.4, result.Value);
        Assert.Contains("rolling", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyEntryUsesFrameTimeThenFrameCounterFormula()
    {
        uint tick = 10_000;
        byte[] frameTime = CreateFixture(284, 1);
        WriteEntry(frameTime, 0, 7, tick - 1_000, tick, 60, 16_000);
        Assert.Equal(62.5, RtssSharedMemoryParser.Parse(frameTime, 7, tick).Value);

        byte[] counter = CreateFixture(284, 1);
        WriteEntry(counter, 0, 7, tick - 1_000, tick, 75, 0);
        Assert.Equal(75, RtssSharedMemoryParser.Parse(counter, 7, tick).Value);
    }

    [Fact]
    public void FreshnessUsesWrappingLow32TickArithmetic()
    {
        byte[] data = CreateFixture(284, 1);
        WriteEntry(data, 0, 99, uint.MaxValue - 100, uint.MaxValue - 5, 10, 20_000);

        Assert.Equal(50, RtssSharedMemoryParser.Parse(data, 99, 25).Value);
    }

    [Fact]
    public void RejectsStaleWrongSignatureWrongMajorAndInvalidBounds()
    {
        byte[] stale = CreateFixture(284, 1);
        WriteEntry(stale, 0, 3, 1_000, 2_000, 60, 16_667);
        Assert.Null(RtssSharedMemoryParser.Parse(stale, 3, 4_001).Value);

        byte[] signature = (byte[])stale.Clone();
        WriteUInt32(signature, 0, 0);
        Assert.Null(RtssSharedMemoryParser.Parse(signature, 3, 2_001).Value);

        byte[] version = (byte[])stale.Clone();
        WriteUInt32(version, 4, 0x0003_0000);
        Assert.Null(RtssSharedMemoryParser.Parse(version, 3, 2_001).Value);

        byte[] bounds = (byte[])stale.Clone();
        WriteUInt32(bounds, 16, uint.MaxValue);
        Assert.Null(RtssSharedMemoryParser.Parse(bounds, 3, 2_001).Value);
    }

    [Fact]
    public void ImplausibleRollingValueFallsBackButAllImplausibleValuesAreRejected()
    {
        byte[] data = CreateFixture(5_028, 1);
        WriteEntry(data, 0, 8, 1_000, 2_000, 60, 20_000, 50_000);
        Assert.Equal(50, RtssSharedMemoryParser.Parse(data, 8, 2_001).Value);

        WriteEntry(data, 0, 8, 1_000, 2_000, uint.MaxValue, 1, 50_000);
        Assert.Null(RtssSharedMemoryParser.Parse(data, 8, 2_001).Value);
    }

    private static byte[] CreateFixture(int entrySize, uint count)
    {
        const int offset = RtssSharedMemoryParser.HeaderSize;
        var data = new byte[checked(offset + entrySize * (int)count)];
        WriteUInt32(data, 0, RtssSharedMemoryParser.Signature);
        WriteUInt32(data, 4, 0x0002_0015);
        WriteUInt32(data, 8, (uint)entrySize);
        WriteUInt32(data, 12, offset);
        WriteUInt32(data, 16, count);
        return data;
    }

    private static void WriteEntry(
        byte[] data,
        int index,
        uint pid,
        uint time0,
        uint time1,
        uint frames,
        uint frameTime,
        uint? rollingTenths = null)
    {
        int entrySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        int offset = RtssSharedMemoryParser.HeaderSize + index * entrySize;
        WriteUInt32(data, offset, pid);
        WriteUInt32(data, offset + 268, time0);
        WriteUInt32(data, offset + 272, time1);
        WriteUInt32(data, offset + 276, frames);
        WriteUInt32(data, offset + 280, frameTime);
        if (rollingTenths is uint rolling)
        {
            WriteUInt32(data, offset + RtssSharedMemoryParser.RollingFpsOffset, rolling);
        }
    }

    private static void WriteUInt32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)), value);
}
