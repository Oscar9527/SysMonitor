using System.Runtime.InteropServices;

namespace SysMonitor.Services;

internal sealed class CpuFrequencyReader
{
    internal double? ReadCurrentMhz()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        int processorCount = Math.Max(1, Environment.ProcessorCount);
        int structureSize = Marshal.SizeOf<ProcessorPowerInformation>();
        int bufferSize;
        try
        {
            bufferSize = checked(processorCount * structureSize);
        }
        catch (OverflowException)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            uint status = CallNtPowerInformation(
                PowerInformationLevel.ProcessorInformation,
                IntPtr.Zero,
                0,
                buffer,
                (uint)bufferSize);
            if (status != 0)
            {
                return null;
            }

            var currentMhz = new uint[processorCount];
            for (int index = 0; index < processorCount; index++)
            {
                IntPtr entry = IntPtr.Add(buffer, index * structureSize);
                currentMhz[index] = Marshal.PtrToStructure<ProcessorPowerInformation>(entry).CurrentMhz;
            }

            return AverageValidCurrentMhz(currentMhz);
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static double? AverageValidCurrentMhz(IEnumerable<uint> currentMhz)
    {
        ulong sum = 0;
        int count = 0;
        foreach (uint value in currentMhz)
        {
            if (value == 0 || value > 10_000_000)
            {
                continue;
            }

            sum += value;
            count++;
        }

        return count == 0 ? null : sum / (double)count;
    }

    private enum PowerInformationLevel
    {
        ProcessorInformation = 11,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        internal uint Number;
        internal uint MaxMhz;
        internal uint CurrentMhz;
        internal uint MhzLimit;
        internal uint MaxIdleState;
        internal uint CurrentIdleState;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        PowerInformationLevel informationLevel,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize);
}
