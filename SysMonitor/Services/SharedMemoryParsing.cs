using System.Buffers.Binary;

namespace SysMonitor.Services;

internal readonly record struct SharedMemoryValue(double? Value, string Reason)
{
    internal bool HasValue => Value is not null;

    internal static SharedMemoryValue Missing(string reason) => new(null, reason);
    internal static SharedMemoryValue Present(double value, string reason) => new(value, reason);
}

internal static class SharedMemoryParsing
{
    internal static bool TryReadUInt32(ReadOnlySpan<byte> data, ulong offset, out uint value)
    {
        value = 0;
        if (offset > int.MaxValue || offset + sizeof(uint) > (ulong)data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)offset, sizeof(uint)));
        return true;
    }

    internal static bool TryReadSingle(ReadOnlySpan<byte> data, ulong offset, out float value)
    {
        value = default;
        if (!TryReadUInt32(data, offset, out uint bits))
        {
            return false;
        }

        value = BitConverter.UInt32BitsToSingle(bits);
        return true;
    }

    internal static bool TryRange(ulong offset, ulong count, ulong stride, ulong capacity, out ulong end)
    {
        end = 0;
        try
        {
            end = checked(offset + checked(count * stride));
            return end <= capacity;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
