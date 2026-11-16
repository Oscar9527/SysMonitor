using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SysMonitor.Services;

namespace SysMonitor.Tests;

[CollectionDefinition("Taskbar native recovery", DisableParallelization = true)]
public sealed class TaskbarNativeRecoveryCollection;

[Collection("Taskbar native recovery")]
public sealed class TaskbarPositionerNativeTests
{
    private static long s_generation;

    [Fact]
    public void NativeHiddenWindowIsShownByPositioningWithoutActivationOrHandleReplacement()
    {
        RunSta(() =>
        {
            using var fixture = new NativeFixture();
            nint handle = fixture.Handle;
            nint foreground = GetForegroundWindow();
            _ = ShowWindow(handle, 0);
            Assert.False(IsWindowVisible(handle));

            TaskbarPositionResult result = fixture.Position();

            Assert.True(result.LayoutValid);
            Assert.True(IsWindowVisible(handle));
            Assert.Equal(handle, fixture.Handle);
            Assert.Equal(foreground, GetForegroundWindow());
        });
    }

    [Fact]
    public void FullyClippedChildIsDetectedAndCanReturnToConfiguredPosition()
    {
        RunSta(() =>
        {
            using var fixture = new NativeFixture();
            Assert.True(GetWindowRect(fixture.Handle, out NativeRect before));
            Assert.True(SetWindowPos(fixture.Handle, 0, -32768, 0, 400, 34, 0x0014));

            Assert.True(TaskbarPositioner.NeedsVisibilityRecovery(fixture.Window));
            Assert.True(fixture.Position(force: true).LayoutValid);
            Assert.True(GetWindowRect(fixture.Handle, out NativeRect after));
            Assert.Equal(before, after);
            Assert.False(TaskbarPositioner.NeedsVisibilityRecovery(fixture.Window));
        });
    }

    [Fact]
    public void VisibleChildAndTemporarilyHiddenParentDoNotRequestRecovery()
    {
        RunSta(() =>
        {
            using var fixture = new NativeFixture();
            Assert.False(TaskbarPositioner.NeedsVisibilityRecovery(fixture.Window));
            _ = ShowWindow(fixture.ParentHandle, 0);
            Assert.False(IsWindowVisible(fixture.Handle));
            Assert.False(TaskbarPositioner.NeedsVisibilityRecovery(fixture.Window));
        });
    }

    private sealed class NativeFixture : IDisposable
    {
        private readonly HwndSource _parent;
        private readonly TaskbarRegionSnapshot _snapshot;

        internal NativeFixture()
        {
            // A private, off-screen parent exercises real HWND clipping and
            // visibility without touching Explorer or moving the user's windows.
            _parent = new HwndSource(new HwndSourceParameters("SysMonitor recovery test")
            {
                WindowStyle = unchecked((int)0x90000000),
                PositionX = -20000,
                PositionY = -20000,
                Width = 1000,
                Height = 40
            });
            Window = new Window
            {
                Width = 400, Height = 34, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                ShowActivated = false
            };
            Handle = new WindowInteropHelper(Window).EnsureHandle();
            _ = SetWindowLongPtr(Handle, -16, new nint(0x40000000));
            _ = SetParent(Handle, _parent.Handle);
            Assert.True(GetWindowRect(_parent.Handle, out NativeRect parentRect));
            _snapshot = new TaskbarRegionSnapshot(
                Interlocked.Increment(ref s_generation), _parent.Handle,
                parentRect.Left, parentRect.Top, parentRect.Right, parentRect.Bottom,
                parentRect.Left + 100, parentRect.Right - 100,
                GetDpiForWindow(_parent.Handle), true, true);
            _ = ShowWindow(Handle, 4);
            Assert.True(Position(force: true).LayoutValid);
        }

        internal Window Window { get; }
        internal nint Handle { get; }
        internal nint ParentHandle => _parent.Handle;
        internal TaskbarPositionResult Position(bool force = false) =>
            TaskbarPositioner.Position(Window, _snapshot, 98, 0, 400, force);

        public void Dispose()
        {
            Window.Close();
            _parent.Dispose();
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Native recovery test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct NativeRect(int Left, int Top, int Right, int Bottom);

    [DllImport("user32.dll")] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint handle, int index, nint value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
}
