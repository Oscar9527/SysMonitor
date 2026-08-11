using System.IO;
using System.IO.MemoryMappedFiles;
using SysMonitor.Models;

namespace SysMonitor.Services;

internal interface IRtssFrameSource
{
    SharedMemoryValue Read(int processId);
}

internal static class RtssSharedMemoryParser
{
    internal const uint Signature = 0x52545353;
    internal const int HeaderSize = 20;
    internal const int MinimumEntrySize = 284;
    internal const int RollingFpsOffset = 5024;
    internal const int RollingFpsEntrySize = 5028;
    internal const uint MaximumEntries = 4096;
    internal const uint MaximumEntrySize = 1024 * 1024;
    internal const double MaximumPlausibleFps = 2000d;

    internal static SharedMemoryValue Parse(ReadOnlySpan<byte> data, int processId, uint tickCount)
    {
        if (processId <= 0)
        {
            return SharedMemoryValue.Missing("invalid target pid");
        }

        if (data.Length < HeaderSize ||
            !SharedMemoryParsing.TryReadUInt32(data, 0, out uint signature) ||
            signature != Signature)
        {
            return SharedMemoryValue.Missing("RTSS signature unavailable");
        }

        SharedMemoryParsing.TryReadUInt32(data, 4, out uint version);
        if ((version >> 16) != 2)
        {
            return SharedMemoryValue.Missing("unsupported RTSS shared-memory version");
        }

        SharedMemoryParsing.TryReadUInt32(data, 8, out uint entrySize);
        SharedMemoryParsing.TryReadUInt32(data, 12, out uint arrayOffset);
        SharedMemoryParsing.TryReadUInt32(data, 16, out uint entryCount);
        if (entrySize < MinimumEntrySize || entrySize > MaximumEntrySize ||
            entryCount > MaximumEntries ||
            arrayOffset < HeaderSize ||
            !SharedMemoryParsing.TryRange(arrayOffset, entryCount, entrySize, (ulong)data.Length, out _))
        {
            return SharedMemoryValue.Missing("invalid RTSS shared-memory layout");
        }

        for (uint index = 0; index < entryCount; index++)
        {
            ulong offset = (ulong)arrayOffset + (ulong)index * entrySize;
            if (!SharedMemoryParsing.TryReadUInt32(data, offset, out uint pid) || pid != (uint)processId)
            {
                continue;
            }

            SharedMemoryParsing.TryReadUInt32(data, offset + 268, out uint time0);
            SharedMemoryParsing.TryReadUInt32(data, offset + 272, out uint time1);
            SharedMemoryParsing.TryReadUInt32(data, offset + 276, out uint frames);
            SharedMemoryParsing.TryReadUInt32(data, offset + 280, out uint frameTimeMicroseconds);
            if (unchecked(tickCount - time1) > 2000u)
            {
                return SharedMemoryValue.Missing("RTSS target sample is stale");
            }

            if (entrySize >= RollingFpsEntrySize &&
                SharedMemoryParsing.TryReadUInt32(data, offset + RollingFpsOffset, out uint rollingTenths) &&
                TryValidateFps(rollingTenths / 10d, out double rollingFps))
            {
                return SharedMemoryValue.Present(rollingFps, "RTSS rolling FPS");
            }

            if (frameTimeMicroseconds > 0 &&
                TryValidateFps(1_000_000d / frameTimeMicroseconds, out double frameTimeFps))
            {
                return SharedMemoryValue.Present(frameTimeFps, "RTSS frame-time FPS");
            }

            uint elapsed = unchecked(time1 - time0);
            if (elapsed > 0 && frames > 0 &&
                TryValidateFps(1000d * frames / elapsed, out double counterFps))
            {
                return SharedMemoryValue.Present(counterFps, "RTSS frame-counter FPS");
            }

            return SharedMemoryValue.Missing("RTSS target has no plausible FPS sample");
        }

        return SharedMemoryValue.Missing("RTSS target pid not found");
    }

    private static bool TryValidateFps(double value, out double fps)
    {
        fps = value;
        return double.IsFinite(value) && value > 0d && value <= MaximumPlausibleFps;
    }
}

internal sealed class RtssSharedMemoryReader : IRtssFrameSource
{
    internal const string MappingName = "RTSSSharedMemoryV2";
    private const long MaximumMappingBytes = 64L * 1024 * 1024;

    public SharedMemoryValue Read(int processId)
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
            if (capacity < RtssSharedMemoryParser.HeaderSize || capacity > MaximumMappingBytes)
            {
                return SharedMemoryValue.Missing("invalid RTSS mapping capacity");
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                SharedMemoryValue? result = TryReadStableEntry(view, capacity, processId);
                if (result is SharedMemoryValue value)
                {
                    return value;
                }
            }

            return SharedMemoryValue.Missing("RTSS sample changed while being read");
        }
        catch (Exception exception) when (exception is FileNotFoundException or
            UnauthorizedAccessException or IOException or ArgumentException or OverflowException)
        {
            return SharedMemoryValue.Missing($"RTSS mapping unavailable ({exception.GetType().Name})");
        }
    }

    private static SharedMemoryValue? TryReadStableEntry(
        MemoryMappedViewAccessor view,
        long capacity,
        int processId)
    {
        byte[] header = ReadBytes(view, 0, RtssSharedMemoryParser.HeaderSize);
        if (header.Length < RtssSharedMemoryParser.HeaderSize ||
            !SharedMemoryParsing.TryReadUInt32(header, 0, out uint signature) ||
            signature != RtssSharedMemoryParser.Signature ||
            !SharedMemoryParsing.TryReadUInt32(header, 4, out uint version) ||
            (version >> 16) != 2 ||
            !SharedMemoryParsing.TryReadUInt32(header, 8, out uint entrySize) ||
            !SharedMemoryParsing.TryReadUInt32(header, 12, out uint arrayOffset) ||
            !SharedMemoryParsing.TryReadUInt32(header, 16, out uint entryCount) ||
            entrySize < RtssSharedMemoryParser.MinimumEntrySize ||
            entrySize > RtssSharedMemoryParser.MaximumEntrySize ||
            entryCount > RtssSharedMemoryParser.MaximumEntries ||
            !SharedMemoryParsing.TryRange(arrayOffset, entryCount, entrySize, (ulong)capacity, out _))
        {
            var boundedHeader = new byte[RtssSharedMemoryParser.HeaderSize];
            header.CopyTo(boundedHeader, 0);
            return RtssSharedMemoryParser.Parse(boundedHeader, processId, CurrentTickCount());
        }

        for (uint index = 0; index < entryCount; index++)
        {
            long entryOffset = checked((long)((ulong)arrayOffset + (ulong)index * entrySize));
            if (view.ReadUInt32(entryOffset) != (uint)processId)
            {
                continue;
            }

            byte[] entry = ReadBytes(view, entryOffset, checked((int)entrySize));
            byte[] headerAfter = ReadBytes(view, 0, RtssSharedMemoryParser.HeaderSize);
            uint pidAfter = view.ReadUInt32(entryOffset);
            uint time1After = view.ReadUInt32(entryOffset + 272);
            uint rollingAfter = entrySize >= RtssSharedMemoryParser.RollingFpsEntrySize
                ? view.ReadUInt32(entryOffset + RtssSharedMemoryParser.RollingFpsOffset)
                : 0;
            SharedMemoryParsing.TryReadUInt32(entry, 272, out uint capturedTime1);
            uint capturedRolling = 0;
            if (entrySize >= RtssSharedMemoryParser.RollingFpsEntrySize)
            {
                SharedMemoryParsing.TryReadUInt32(
                    entry,
                    RtssSharedMemoryParser.RollingFpsOffset,
                    out capturedRolling);
            }

            if (!header.AsSpan().SequenceEqual(headerAfter) ||
                pidAfter != (uint)processId ||
                capturedTime1 != time1After ||
                capturedRolling != rollingAfter)
            {
                return null;
            }

            int compactOffset = RtssSharedMemoryParser.HeaderSize;
            byte[] compact = new byte[checked(compactOffset + (int)entrySize)];
            header.CopyTo(compact, 0);
            BitConverter.GetBytes((uint)compactOffset).CopyTo(compact, 12);
            BitConverter.GetBytes(1u).CopyTo(compact, 16);
            entry.CopyTo(compact, compactOffset);
            return RtssSharedMemoryParser.Parse(compact, processId, CurrentTickCount());
        }

        return SharedMemoryValue.Missing("RTSS target pid not found");
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

    private static uint CurrentTickCount() => unchecked((uint)Environment.TickCount64);
}
