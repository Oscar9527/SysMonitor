using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace SysMonitor.Services;

/// <summary>
/// Resolves the monitor containing a window into a stable overlay identity.
/// All native calls are read-only.  If DISPLAYCONFIG is unavailable or does
/// not expose a path for the monitor, a conservative GDI-plus-bounds fallback
/// identity is returned.
/// </summary>
internal sealed class OverlayMonitorIdentityResolver
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;

    public bool TryResolveForWindow(
        nint windowHandle,
        out OverlayMonitorIdentity identity,
        bool usePrimaryWhenNoWindow = true)
    {
        identity = default;
        nint monitor;
        try
        {
            monitor = MonitorFromWindow(
                windowHandle,
                windowHandle == nint.Zero && usePrimaryWhenNoWindow
                    ? MonitorDefaultToPrimary
                    : MonitorDefaultToNearest);
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            return false;
        }

        return monitor != nint.Zero && TryResolveForMonitor(monitor, out identity);
    }

    public bool TryResolveForMonitor(nint monitorHandle, out OverlayMonitorIdentity identity)
    {
        identity = default;
        if (monitorHandle == nint.Zero)
        {
            return false;
        }

        MonitorInfoEx monitorInfo = new()
        {
            CbSize = Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty,
        };
        bool monitorInfoRead;
        try
        {
            monitorInfoRead = GetMonitorInfo(monitorHandle, ref monitorInfo);
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }

        if (!monitorInfoRead)
        {
            return false;
        }

        ScreenPixelBounds bounds = new(
            monitorInfo.MonitorArea.Left,
            monitorInfo.MonitorArea.Top,
            monitorInfo.MonitorArea.Right,
            monitorInfo.MonitorArea.Bottom);
        string gdiName = OverlayMonitorIdentityText.NormalizeGdiName(monitorInfo.DeviceName);
        if (!bounds.IsValid || string.IsNullOrEmpty(gdiName))
        {
            // Keep a malformed native rectangle from being persisted or
            // matched.  The caller can safely ignore this result.
            return false;
        }

        try
        {
            if (DisplayConfigMap.TryRead(out DisplayConfigMap map) &&
                map.TryGet(gdiName, out DisplayConfigMonitor monitor))
            {
                if (!monitor.IsAmbiguous && !string.IsNullOrEmpty(monitor.MonitorDevicePath))
                {
                    identity = OverlayMonitorIdentity.CreateStable(
                        monitor.MonitorDevicePath,
                        gdiName,
                        monitor.FriendlyName,
                        bounds);
                    return true;
                }
            }
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            // Native display topology can disappear while a monitor is being
            // hot-plugged.  Fall through to the deterministic fallback.
        }
        catch (Exception)
        {
            // Malformed or transient native data must never escape into WPF
            // positioning.  The GDI-plus-bounds identity remains usable.
        }

        identity = OverlayMonitorIdentity.CreateFallback(gdiName, gdiName, bounds);
        return true;
    }

    private static bool IsNativeFailure(Exception exception) =>
        exception is DllNotFoundException or
        EntryPointNotFoundException or
        BadImageFormatException or
        MarshalDirectiveException or
        SEHException;

    private readonly record struct DisplayConfigMonitor(
        string MonitorDevicePath,
        string FriendlyName,
        bool IsAmbiguous);

    private sealed class DisplayConfigMap
    {
        private readonly Dictionary<string, DisplayConfigMonitor> _byGdiName;

        private DisplayConfigMap(Dictionary<string, DisplayConfigMonitor> byGdiName)
        {
            _byGdiName = byGdiName;
        }

        public bool TryGet(string gdiName, out DisplayConfigMonitor monitor) =>
            _byGdiName.TryGetValue(gdiName, out monitor);

        public static bool TryRead(out DisplayConfigMap map)
        {
            map = null!;
            if (!TryQueryActivePaths(out DISPLAYCONFIG_PATH_INFO[] paths))
            {
                return false;
            }

            var byGdi = new Dictionary<string, DisplayConfigMonitor>(StringComparer.Ordinal);
            var pathOwners = new Dictionary<string, string>(StringComparer.Ordinal);
            var ambiguousGdi = new HashSet<string>(StringComparer.Ordinal);
            var ambiguousPaths = new HashSet<string>(StringComparer.Ordinal);

            foreach (DISPLAYCONFIG_PATH_INFO path in paths)
            {
                if (!TryGetSourceName(path.SourceInfo, out string sourceName))
                {
                    continue;
                }

                sourceName = OverlayMonitorIdentityText.NormalizeGdiName(sourceName);
                if (string.IsNullOrEmpty(sourceName) ||
                    !TryGetTargetName(path.TargetInfo, out string friendlyName, out string devicePath))
                {
                    continue;
                }

                devicePath = OverlayMonitorIdentityText.NormalizeMonitorDevicePath(devicePath);
                if (string.IsNullOrEmpty(devicePath))
                {
                    continue;
                }

                if (pathOwners.TryGetValue(devicePath, out string? previousSource))
                {
                    ambiguousPaths.Add(devicePath);
                    ambiguousGdi.Add(previousSource);
                    ambiguousGdi.Add(sourceName);
                }
                else
                {
                    pathOwners[devicePath] = sourceName;
                }

                if (byGdi.TryGetValue(sourceName, out DisplayConfigMonitor previous) &&
                    !string.Equals(previous.MonitorDevicePath, devicePath, StringComparison.Ordinal))
                {
                    ambiguousGdi.Add(sourceName);
                }

                byGdi[sourceName] = new DisplayConfigMonitor(devicePath, friendlyName, false);
            }

            if (ambiguousGdi.Count > 0 || ambiguousPaths.Count > 0)
            {
                foreach (string gdi in ambiguousGdi)
                {
                    if (byGdi.TryGetValue(gdi, out DisplayConfigMonitor existing))
                    {
                        byGdi[gdi] = existing with { IsAmbiguous = true };
                    }
                }

                foreach ((string gdi, DisplayConfigMonitor existing) in byGdi.ToArray())
                {
                    if (ambiguousPaths.Contains(existing.MonitorDevicePath))
                    {
                        byGdi[gdi] = existing with { IsAmbiguous = true };
                    }
                }
            }

            map = new DisplayConfigMap(byGdi);
            return true;
        }
    }

    private static bool TryQueryActivePaths(out DISPLAYCONFIG_PATH_INFO[] paths)
    {
        paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
        try
        {
            uint pathCount = 0;
            uint modeCount = 0;
            int status = GetDisplayConfigBufferSizes(QueryDisplayConfigOnlyActivePaths, out pathCount, out modeCount);
            if (status != ErrorSuccess || pathCount == 0)
            {
                return false;
            }

            // Topology can change between the sizing and query calls.  Retry a
            // bounded number of times and never throw into overlay placement.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var candidatePaths = new DISPLAYCONFIG_PATH_INFO[checked((int)pathCount)];
                var candidateModes = new DISPLAYCONFIG_MODE_INFO[
                    checked((int)Math.Max(modeCount, 1u))];
                uint requestedPathCount = pathCount;
                uint requestedModeCount = modeCount;
                status = QueryDisplayConfig(
                    QueryDisplayConfigOnlyActivePaths,
                    ref requestedPathCount,
                    candidatePaths,
                    ref requestedModeCount,
                    candidateModes,
                    nint.Zero);
                if (status == ErrorSuccess)
                {
                    if (requestedPathCount > candidatePaths.Length)
                    {
                        return false;
                    }

                    paths = candidatePaths.Take((int)requestedPathCount).ToArray();
                    return true;
                }

                if (status != ErrorInsufficientBuffer)
                {
                    return false;
                }

                pathCount = requestedPathCount;
                modeCount = requestedModeCount;
                if (pathCount == 0)
                {
                    return false;
                }
            }
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    private static bool TryGetSourceName(
        DISPLAYCONFIG_PATH_SOURCE_INFO source,
        out string sourceName)
    {
        sourceName = string.Empty;
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                Type = DisplayConfigDeviceInfoGetSourceName,
                Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                AdapterId = source.AdapterId,
                Id = source.Id,
            },
            ViewGdiDeviceName = string.Empty,
        };
        if (DisplayConfigGetDeviceInfo(ref request) != ErrorSuccess)
        {
            return false;
        }

        sourceName = request.ViewGdiDeviceName ?? string.Empty;
        return !string.IsNullOrWhiteSpace(sourceName);
    }

    private static bool TryGetTargetName(
        DISPLAYCONFIG_PATH_TARGET_INFO target,
        out string friendlyName,
        out string devicePath)
    {
        friendlyName = string.Empty;
        devicePath = string.Empty;
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                Type = DisplayConfigDeviceInfoGetTargetName,
                Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                AdapterId = target.AdapterId,
                Id = target.Id,
            },
            MonitorFriendlyDeviceName = string.Empty,
            MonitorDevicePath = string.Empty,
        };
        if (DisplayConfigGetDeviceInfo(ref request) != ErrorSuccess)
        {
            return false;
        }

        friendlyName = request.MonitorFriendlyDeviceName?.Trim() ?? string.Empty;
        devicePath = request.MonitorDevicePath ?? string.Empty;
        return !string.IsNullOrWhiteSpace(devicePath);
    }

    private const uint QueryDisplayConfigOnlyActivePaths = 0x00000002;
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint DisplayConfigDeviceInfoGetSourceName = 1;
    private const uint DisplayConfigDeviceInfoGetTargetName = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int CbSize;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DISPLAYCONFIG_RATIONAL RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO SourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public DISPLAYCONFIG_MODE_INFO_UNION ModeInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_2DREGION
    {
        public uint Cx;
        public uint Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong PixelRate;
        public DISPLAYCONFIG_RATIONAL HSyncFreq;
        public DISPLAYCONFIG_RATIONAL VSyncFreq;
        public DISPLAYCONFIG_2DREGION ActiveSize;
        public DISPLAYCONFIG_2DREGION TotalSize;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_MODE
    {
        public DISPLAYCONFIG_VIDEO_SIGNAL_INFO TargetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_POSITION
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public DISPLAYCONFIG_POSITION Position;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct DISPLAYCONFIG_MODE_INFO_UNION
    {
        [FieldOffset(0)]
        public DISPLAYCONFIG_SOURCE_MODE SourceMode;

        [FieldOffset(0)]
        public DISPLAYCONFIG_TARGET_MODE TargetMode;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitorHandle, ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        nint currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", SetLastError = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", SetLastError = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
}
