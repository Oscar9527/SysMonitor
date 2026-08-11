using System.Buffers.Binary;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class MahmSharedMemoryParserTests
{
    [Fact]
    public void SelectsOnlyAggregateCpuTemperatureAndIgnoresPerDeviceEntry()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        byte[] data = CreateFixture(now, 2);
        WriteEntry(data, 0, 91.5f, gpu: 0, sourceId: MahmSharedMemoryParser.CpuTemperatureSourceId);
        WriteEntry(data, 1, 67.25f, gpu: MahmSharedMemoryParser.GlobalGpu, sourceId: MahmSharedMemoryParser.CpuTemperatureSourceId);

        Assert.Equal(67.25, MahmSharedMemoryParser.Parse(data, now).Value);
    }

    [Fact]
    public void MultipleAggregateEntriesAreAmbiguous()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_100);
        byte[] data = CreateFixture(now, 2);
        WriteEntry(data, 0, 60, MahmSharedMemoryParser.GlobalGpu, MahmSharedMemoryParser.CpuTemperatureSourceId);
        WriteEntry(data, 1, 61, MahmSharedMemoryParser.GlobalGpu, MahmSharedMemoryParser.CpuTemperatureSourceId);

        SharedMemoryValue result = MahmSharedMemoryParser.Parse(data, now);
        Assert.Null(result.Value);
        Assert.Contains("multiple", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(float.MaxValue)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-51f)]
    [InlineData(151f)]
    public void InvalidSentinelAndImplausibleTemperaturesAreRejected(float temperature)
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_200);
        byte[] data = CreateFixture(now, 1);
        WriteEntry(data, 0, temperature, MahmSharedMemoryParser.GlobalGpu, MahmSharedMemoryParser.CpuTemperatureSourceId);

        Assert.Null(MahmSharedMemoryParser.Parse(data, now).Value);
    }

    [Fact]
    public void RejectsStaleVersionSignatureAndTruncatedGpuMetadata()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_300);
        byte[] stale = CreateFixture(now.AddSeconds(-6), 1);
        WriteEntry(stale, 0, 70, MahmSharedMemoryParser.GlobalGpu, MahmSharedMemoryParser.CpuTemperatureSourceId);
        Assert.Null(MahmSharedMemoryParser.Parse(stale, now).Value);

        byte[] version = (byte[])stale.Clone();
        WriteUInt32(version, 4, 0x0003_0000);
        Assert.Null(MahmSharedMemoryParser.Parse(version, now.AddSeconds(-6)).Value);

        byte[] signature = (byte[])stale.Clone();
        WriteUInt32(signature, 0, 0);
        Assert.Null(MahmSharedMemoryParser.Parse(signature, now.AddSeconds(-6)).Value);

        byte[] gpuMetadata = CreateFixture(now, 1, gpuCount: 1, gpuEntrySize: 16);
        Array.Resize(ref gpuMetadata, gpuMetadata.Length - 1);
        Assert.Null(MahmSharedMemoryParser.Parse(gpuMetadata, now).Value);
    }

    [Fact]
    public void RejectsOverflowingOrUndersizedLayouts()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_400);
        byte[] data = CreateFixture(now, 0);
        WriteUInt32(data, 12, uint.MaxValue);
        WriteUInt32(data, 16, uint.MaxValue);
        Assert.Null(MahmSharedMemoryParser.Parse(data, now).Value);

        WriteUInt32(data, 12, 0);
        WriteUInt32(data, 16, MahmSharedMemoryParser.MinimumEntrySize - 1);
        Assert.Null(MahmSharedMemoryParser.Parse(data, now).Value);
    }

    private static byte[] CreateFixture(
        DateTimeOffset time,
        uint count,
        uint gpuCount = 0,
        uint gpuEntrySize = 0)
    {
        const int headerSize = MahmSharedMemoryParser.MinimumHeaderSize;
        const int entrySize = MahmSharedMemoryParser.MinimumEntrySize;
        var data = new byte[checked(headerSize + (int)count * entrySize + (int)gpuCount * (int)gpuEntrySize)];
        WriteUInt32(data, 0, MahmSharedMemoryParser.Signature);
        WriteUInt32(data, 4, 0x0002_0001);
        WriteUInt32(data, 8, headerSize);
        WriteUInt32(data, 12, count);
        WriteUInt32(data, 16, entrySize);
        WriteUInt32(data, 20, checked((uint)time.ToUnixTimeSeconds()));
        WriteUInt32(data, 24, gpuCount);
        WriteUInt32(data, 28, gpuEntrySize);
        return data;
    }

    private static void WriteEntry(byte[] data, int index, float value, uint gpu, uint sourceId)
    {
        int offset = MahmSharedMemoryParser.MinimumHeaderSize + index * MahmSharedMemoryParser.MinimumEntrySize;
        WriteUInt32(data, offset + 1300, BitConverter.SingleToUInt32Bits(value));
        WriteUInt32(data, offset + 1316, gpu);
        WriteUInt32(data, offset + 1320, sourceId);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)), value);
}
