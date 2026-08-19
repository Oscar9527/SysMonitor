using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SysMonitor.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    public const int HotkeyId = 0x534D;
    public const uint ModifierControl = 0x0002;
    public const uint ModifierShift = 0x0004;
    public const uint ModifierNoRepeat = 0x4000;
    public const uint Modifiers = ModifierControl | ModifierShift | ModifierNoRepeat;
    public const uint VirtualKeyF10 = 0x79;

    private const int WmHotkey = 0x0312;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly IGlobalHotkeyNative _native;
    private readonly HwndSource? _source;
    private bool _disposed;

    public GlobalHotkeyService()
        : this(new Win32GlobalHotkeyNative(), createWindow: true)
    {
    }

    internal GlobalHotkeyService(IGlobalHotkeyNative native, bool createWindow)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        if (!createWindow)
        {
            WindowHandle = new nint(1);
        }
        else
        {
            var parameters = new HwndSourceParameters("SysMonitor.GameOverlayHotkey")
            {
                Width = 0,
                Height = 0,
                PositionX = -32000,
                PositionY = -32000,
                WindowStyle = 0,
                ExtendedWindowStyle = WsExToolWindow | WsExNoActivate
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WindowProc);
            WindowHandle = _source.Handle;
        }

        IsRegistered = _native.Register(WindowHandle, HotkeyId, Modifiers, VirtualKeyF10);
        if (!IsRegistered)
        {
            int error = _native.GetLastError();
            RegistrationDiagnostic = error == 1409
                ? LocalizationService.Current.GetString("GameOverlayHotkeyConflict")
                : LocalizationService.Current.Format("GameOverlayHotkeyFailure", error);
        }
    }

    public event EventHandler? Pressed;

    public nint WindowHandle { get; }

    public bool IsRegistered { get; }

    public string? RegistrationDiagnostic { get; }

    internal bool ProcessMessage(int message, nint wParam)
    {
        if (!_disposed && message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (IsRegistered)
        {
            _native.Unregister(WindowHandle, HotkeyId);
        }

        if (_source is not null)
        {
            _source.RemoveHook(WindowProc);
            _source.Dispose();
        }
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        handled = ProcessMessage(message, wParam);
        return nint.Zero;
    }
}

internal interface IGlobalHotkeyNative
{
    bool Register(nint windowHandle, int id, uint modifiers, uint virtualKey);
    bool Unregister(nint windowHandle, int id);
    int GetLastError();
}

internal sealed class Win32GlobalHotkeyNative : IGlobalHotkeyNative
{
    public bool Register(nint windowHandle, int id, uint modifiers, uint virtualKey) =>
        RegisterHotKey(windowHandle, id, modifiers, virtualKey);

    public bool Unregister(nint windowHandle, int id) => UnregisterHotKey(windowHandle, id);

    public int GetLastError() => Marshal.GetLastWin32Error();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);
}
