using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SysMonitor.Services;

internal static class ProcessExecutablePathResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaximumPathCharacters = 32_768;

    internal static string? TryResolve(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using SafeProcessHandle process = OpenProcess(
                ProcessQueryLimitedInformation,
                inheritHandle: false,
                processId);
            if (process.IsInvalid)
            {
                return null;
            }

            var path = new StringBuilder(MaximumPathCharacters);
            uint length = (uint)path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref length) || length == 0)
            {
                return null;
            }

            string resolved = path.ToString(0, checked((int)length));
            return Path.IsPathFullyQualified(resolved) ? Path.GetFullPath(resolved) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or
            IOException or NotSupportedException or OverflowException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executablePath,
        ref uint size);
}
