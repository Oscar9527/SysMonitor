using System.IO;
using System.IO.MemoryMappedFiles;

namespace SysMonitor.Services;

internal interface ICpuTemperatureSource
{
    SharedMemoryValue Read(DateTimeOffset now);
}

internal static class MahmSharedMemoryParser
{
    internal const uint Signature = 0x4D41484D;
    internal const int MinimumHeaderSize = 32;
    internal const int MinimumEntrySize = 1324;
    internal const uint CpuTemperatureSourceId = 0x80;
    internal const uint GlobalGpu = 0xFFFFFFFF;
    internal const uint MaximumEntries = 4096;
    internal const uint MaximumGpuEntries = 256;

    internal static SharedMemoryValue Parse(ReadOnlySpan<byte> data, DateTimeOffset now)
    {
        if (data.Length < MinimumHeaderSize ||
            !SharedMemoryParsing.TryReadUInt32(data, 0, out uint signature) ||
            signature != Signature)
        {
            return SharedMemoryValue.Missing("MAHM signature unavailable");
        }

        SharedMemoryParsing.TryReadUInt32(data, 4, out uint version);
        if ((version >> 16) != 2)
        {
            return SharedMemoryValue.Missing("unsupported MAHM shared-memory version");
        }

        SharedMemoryParsing.TryReadUInt32(data, 8, out uint headerSize);
        SharedMemoryParsing.TryReadUInt32(data, 12, out uint entryCount);
        SharedMemoryParsing.TryReadUInt32(data, 16, out uint entrySize);
        SharedMemoryParsing.TryReadUInt32(data, 20, out uint unixTime);
        SharedMemoryParsing.TryReadUInt32(data, 24, out uint gpuCount);
        SharedMemoryParsing.TryReadUInt32(data, 28, out uint gpuEntrySize);
        if (headerSize < MinimumHeaderSize || entrySize < MinimumEntrySize ||
            entryCount > MaximumEntries || gpuCount > MaximumGpuEntries ||
            (gpuCount > 0 && gpuEntrySize == 0))
        {
            return SharedMemoryValue.Missing("invalid MAHM shared-memory layout");
        }

        ulong entriesOffset = headerSize;
        if (!SharedMemoryParsing.TryRange(
                entriesOffset,
                entryCount,
                entrySize,
                (ulong)data.Length,
                out ulong gpuEntriesOffset) ||
            !SharedMemoryParsing.TryRange(
                gpuEntriesOffset,
                gpuCount,
                gpuEntrySize,
                (ulong)data.Length,
                out _))
        {
            return SharedMemoryValue.Missing("invalid MAHM shared-memory capacity");
        }

        long ageSeconds = now.ToUnixTimeSeconds() - unixTime;
        if (ageSeconds < -1 || ageSeconds > 5)
        {
            return SharedMemoryValue.Missing("MAHM sample is stale");
        }

        double? temperature = null;
        for (uint index = 0; index < entryCount; index++)
        {
            ulong offset = entriesOffset + (ulong)index * entrySize;
            if (!SharedMemoryParsing.TryReadUInt32(data, offset + 1316, out uint gpu) ||
                !SharedMemoryParsing.TryReadUInt32(data, offset + 1320, out uint sourceId) ||
                gpu != GlobalGpu || sourceId != CpuTemperatureSourceId)
            {
                continue;
            }

            if (!SharedMemoryParsing.TryReadSingle(data, offset + 1300, out float value) ||
                value == float.MaxValue || !float.IsFinite(value) || value < -50f || value > 150f)
            {
                continue;
            }

            if (temperature is not null)
            {
                return SharedMemoryValue.Missing("multiple aggregate MAHM CPU temperature entries");
            }

            temperature = value;
        }

        return temperature is double result
            ? SharedMemoryValue.Present(result, "MSI Afterburner shared memory")
            : SharedMemoryValue.Missing("MAHM aggregate CPU temperature unavailable");
    }
}

internal sealed class MahmSharedMemoryReader : ICpuTemperatureSource
{
    internal const string MappingName = "MAHMSharedMemory";
    private const long MaximumMappingBytes = 64L * 1024 * 1024;

    public SharedMemoryValue Read(DateTimeOffset now)
    {
        try
        {
            using MemoryMappedFile mapping = MemoryMappedFile.OpenExisting(
                MappingName,
                MemoryMappedFileRights.Read);
            using MemoryMappedViewAccessor view = mapping.CreateViewAccessor(
                0,
                0,
                MemoryMappedFileAccess.Read);
            long capacity = view.Capacity;
            if (capacity < MahmSharedMemoryParser.MinimumHeaderSize || capacity > MaximumMappingBytes)
            {
                return SharedMemoryValue.Missing("invalid MAHM mapping capacity");
            }

            byte[] headerBefore = ReadBytes(view, 0, MahmSharedMemoryParser.MinimumHeaderSize);
            byte[] snapshot = ReadBytes(view, 0, checked((int)capacity));
            byte[] headerAfter = ReadBytes(view, 0, MahmSharedMemoryParser.MinimumHeaderSize);
            if (!headerBefore.AsSpan().SequenceEqual(headerAfter) ||
                !headerBefore.AsSpan().SequenceEqual(snapshot.AsSpan(0, headerBefore.Length)))
            {
                return SharedMemoryValue.Missing("MAHM header changed while being read");
            }

            return MahmSharedMemoryParser.Parse(snapshot, now);
        }
        catch (Exception exception) when (exception is FileNotFoundException or
            UnauthorizedAccessException or IOException or ArgumentException or OverflowException)
        {
            return SharedMemoryValue.Missing($"MAHM mapping unavailable ({exception.GetType().Name})");
        }
    }

    private static byte[] ReadBytes(MemoryMappedViewAccessor view, long offset, int count)
    {
        var bytes = new byte[count];
        int read = view.ReadArray(offset, bytes, 0, count);
        if (read != count)
        {
            throw new IOException("Shared-memory snapshot was incomplete.");
        }

        return bytes;
    }
}
