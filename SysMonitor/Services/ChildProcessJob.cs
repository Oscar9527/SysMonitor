using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysMonitor.Services;

internal sealed class ChildProcessJob : IDisposable
{
    private readonly SafeJobHandle _handle;
    private int _disposed;

    internal ChildProcessJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error);
        }

        IntPtr pointer = IntPtr.Zero;
        try
        {
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            pointer = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(
                    _handle,
                    JobObjectInfoType.ExtendedLimitInformation,
                    pointer,
                    (uint)size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    internal void Assign(Process process)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _handle.Dispose();
    }

    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        JobObjectInfoType informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
