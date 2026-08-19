using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class GlobalHotkeyServiceTests
{
    [Fact]
    public void Registration_UsesFixedChordAndNoRepeat()
    {
        var native = new FakeNative { RegisterResult = true };
        using var service = new GlobalHotkeyService(native, createWindow: false);

        Assert.Equal(GlobalHotkeyService.HotkeyId, native.Id);
        Assert.Equal(
            GlobalHotkeyService.ModifierControl |
            GlobalHotkeyService.ModifierShift |
            GlobalHotkeyService.ModifierNoRepeat,
            native.Modifiers);
        Assert.Equal(GlobalHotkeyService.VirtualKeyF10, native.Key);
        Assert.True(service.IsRegistered);
    }

    [Fact]
    public void RegistrationConflict_ProvidesDiagnosticWithoutClaimingRegistration()
    {
        var native = new FakeNative { RegisterResult = false, LastError = 1409 };
        using var service = new GlobalHotkeyService(native, createWindow: false);

        Assert.False(service.IsRegistered);
        Assert.Contains("Ctrl+Shift+F10", service.RegistrationDiagnostic);
    }

    [Fact]
    public void Dispose_UnregistersExactlyOnce()
    {
        var native = new FakeNative { RegisterResult = true };
        var service = new GlobalHotkeyService(native, createWindow: false);

        service.Dispose();
        service.Dispose();

        Assert.Equal(1, native.UnregisterCalls);
    }

    private sealed class FakeNative : IGlobalHotkeyNative
    {
        public bool RegisterResult { get; init; }
        public int LastError { get; init; }
        public int Id { get; private set; }
        public uint Modifiers { get; private set; }
        public uint Key { get; private set; }
        public int UnregisterCalls { get; private set; }

        public bool Register(nint windowHandle, int id, uint modifiers, uint virtualKey)
        {
            Id = id;
            Modifiers = modifiers;
            Key = virtualKey;
            return RegisterResult;
        }

        public bool Unregister(nint windowHandle, int id)
        {
            UnregisterCalls++;
            return true;
        }

        public int GetLastError() => LastError;
    }
}
