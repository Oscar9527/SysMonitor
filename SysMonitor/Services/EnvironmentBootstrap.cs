using System.IO;
using System.Runtime.InteropServices;

namespace SysMonitor.Services;

internal static class EnvironmentBootstrap
{
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevelClass = 25;
    private const uint MediumMandatoryRid = 0x2000;

    internal static bool IsLowIntegrityRid(uint rid) => rid < MediumMandatoryRid;

    internal static bool IsRunningAtLowIntegrity() =>
        TryGetCurrentProcessIntegrityRid(out uint rid) && IsLowIntegrityRid(rid);

    internal static bool TryGetCurrentProcessIntegrityRid(out uint rid)
    {
        rid = 0;
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out nint token) || token == nint.Zero)
        {
            return false;
        }

        try
        {
            _ = GetTokenInformation(token, TokenIntegrityLevelClass, nint.Zero, 0, out int requiredLength);
            if (requiredLength <= 0)
            {
                return false;
            }

            nint buffer = Marshal.AllocHGlobal(requiredLength);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, requiredLength, out _))
                {
                    return false;
                }

                nint sid = Marshal.ReadIntPtr(buffer);
                if (sid == nint.Zero)
                {
                    return false;
                }

                nint countPointer = GetSidSubAuthorityCount(sid);
                if (countPointer == nint.Zero)
                {
                    return false;
                }

                byte count = Marshal.ReadByte(countPointer);
                if (count == 0)
                {
                    return false;
                }

                nint subAuthorityPointer = GetSidSubAuthority(sid, (uint)(count - 1));
                if (subAuthorityPointer == nint.Zero)
                {
                    return false;
                }

                rid = unchecked((uint)Marshal.ReadInt32(subAuthorityPointer));
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    internal static void EnsureWindowsDirectoryEnvironment()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINDIR")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
        {
            return;
        }

        string windowsDirectory = ResolveWindowsDirectory(
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Environment.SetEnvironmentVariable("WINDIR", windowsDirectory);
        Environment.SetEnvironmentVariable("windir", windowsDirectory);
    }

    internal static string ResolveWindowsDirectory(
        string? systemDirectory,
        string? specialFolderWindows = null)
    {
        string? candidate = null;
        if (!string.IsNullOrWhiteSpace(systemDirectory))
        {
            string trimmedSystem = systemDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            candidate = Path.GetDirectoryName(trimmedSystem);
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = specialFolderWindows;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = @"C:\Windows";
        }

        string normalized = candidate.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return string.IsNullOrWhiteSpace(normalized) ? @"C:\Windows" : normalized;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern nint GetSidSubAuthority(nint sid, uint subAuthorityIndex);
}
