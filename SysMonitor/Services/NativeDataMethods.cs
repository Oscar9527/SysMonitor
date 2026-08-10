using System.Runtime.InteropServices;

namespace SysMonitor.Services;

internal static class NativeDataMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPerformanceInfo(
        ref PerformanceInformation performanceInformation,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;

        internal readonly ulong ToUInt64() =>
            ((ulong)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PerformanceInformation
    {
        internal uint Size;
        internal UIntPtr CommitTotal;
        internal UIntPtr CommitLimit;
        internal UIntPtr CommitPeak;
        internal UIntPtr PhysicalTotal;
        internal UIntPtr PhysicalAvailable;
        internal UIntPtr SystemCache;
        internal UIntPtr KernelTotal;
        internal UIntPtr KernelPaged;
        internal UIntPtr KernelNonpaged;
        internal UIntPtr PageSize;
        internal uint HandleCount;
        internal uint ProcessCount;
        internal uint ThreadCount;
    }
}
