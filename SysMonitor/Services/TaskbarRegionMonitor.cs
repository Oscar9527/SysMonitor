using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;

namespace SysMonitor.Services;

public sealed class TaskbarRegionMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<TaskbarRegionSnapshot> _snapshotAvailable;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private long _requestedGeneration;
    private long _publishedGeneration;
    private int _stopping;
    private int _active;
    private bool _started;

    public TaskbarRegionMonitor(
        Dispatcher dispatcher,
        Action<TaskbarRegionSnapshot> snapshotAvailable)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _snapshotAvailable = snapshotAvailable ??
            throw new ArgumentNullException(nameof(snapshotAvailable));
        _thread = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "SysMonitor taskbar region probe"
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public void Start()
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        Volatile.Write(ref _active, 1);
        if (!_started)
        {
            _started = true;
            _thread.Start();
        }

        RequestProbe();
    }

    public void Stop()
    {
        Volatile.Write(ref _active, 0);
        Interlocked.Increment(ref _requestedGeneration);
    }

    public void RequestProbe()
    {
        if (!_started || Volatile.Read(ref _active) == 0 ||
            Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _requestedGeneration);
        _wake.Set();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        _wake.Set();
        if (_started && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        if (!_started || !_thread.IsAlive)
        {
            _wake.Dispose();
        }
    }

    private void WorkerMain()
    {
        while (Volatile.Read(ref _stopping) == 0)
        {
            _wake.WaitOne();
            if (Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            long generation = Volatile.Read(ref _requestedGeneration);
            TaskbarRegionSnapshot snapshot;
            try
            {
                snapshot = Probe(generation);
            }
            catch
            {
                snapshot = TaskbarRegionSnapshot.Invalid(generation, nint.Zero);
            }
            if (Volatile.Read(ref _stopping) != 0)
            {
                return;
            }
            if (generation != Volatile.Read(ref _requestedGeneration))
            {
                continue;
            }

            try
            {
                _ = _dispatcher.InvokeAsync(
                    () => Publish(snapshot),
                    DispatcherPriority.Render);
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private void Publish(TaskbarRegionSnapshot snapshot)
    {
        if (Volatile.Read(ref _stopping) != 0 ||
            Volatile.Read(ref _active) == 0 ||
            snapshot.Generation < Volatile.Read(ref _requestedGeneration) ||
            snapshot.Generation <= _publishedGeneration)
        {
            return;
        }

        _publishedGeneration = snapshot.Generation;
        _snapshotAvailable(snapshot);
    }

    private static TaskbarRegionSnapshot Probe(long generation)
    {
        nint taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == nint.Zero ||
            !IsWindow(taskbar) ||
            !GetWindowRect(taskbar, out NativeRect taskbarRect))
        {
            return TaskbarRegionSnapshot.Invalid(generation, taskbar);
        }

        uint taskbarDpi = GetDpiForWindow(taskbar);
        if (taskbarDpi == 0)
        {
            taskbarDpi = 96;
        }

        if (taskbarRect.Width <= 0 || taskbarRect.Height <= 4)
        {
            return TaskbarRegionSnapshot.Invalid(
                generation,
                taskbar,
                taskbarRect.Left,
                taskbarRect.Top,
                taskbarRect.Right,
                taskbarRect.Bottom,
                taskbarDpi);
        }

        if (TryProbeAutomation(taskbar, taskbarRect, out int occupiedRight, out int notificationLeft) ||
            TryProbeWin32(taskbar, taskbarRect, out occupiedRight, out notificationLeft))
        {
            int padding = Math.Max(2, (int)Math.Round(2 * taskbarDpi / 96d));
            int safeLeft = occupiedRight + padding;
            int safeRight = notificationLeft - padding;
            if (safeLeft < safeRight &&
                safeLeft >= taskbarRect.Left &&
                safeRight <= taskbarRect.Right)
            {
                return new TaskbarRegionSnapshot(
                    generation,
                    taskbar,
                    taskbarRect.Left,
                    taskbarRect.Top,
                    taskbarRect.Right,
                    taskbarRect.Bottom,
                    safeLeft,
                    safeRight,
                    taskbarDpi,
                    true,
                    true);
            }

            return TaskbarRegionSnapshot.Unsafe(
                generation,
                taskbar,
                taskbarRect.Left,
                taskbarRect.Top,
                taskbarRect.Right,
                taskbarRect.Bottom,
                safeLeft,
                safeRight,
                taskbarDpi);
        }

        return TaskbarRegionSnapshot.Invalid(
            generation,
            taskbar,
            taskbarRect.Left,
            taskbarRect.Top,
            taskbarRect.Right,
            taskbarRect.Bottom,
            taskbarDpi);
    }

    private static bool TryProbeAutomation(
        nint taskbar,
        NativeRect taskbarRect,
        out int occupiedRight,
        out int notificationLeft)
    {
        occupiedRight = 0;
        notificationLeft = 0;
        try
        {
            AutomationElement root = AutomationElement.FromHandle(taskbar);
            AutomationElementCollection descendants = root.FindAll(
                TreeScope.Descendants,
                System.Windows.Automation.Condition.TrueCondition);
            var elements = new List<AutomationInfo>(descendants.Count);
            int verticalMidpoint = taskbarRect.Top + taskbarRect.Height / 2;
            for (int index = 0; index < descendants.Count; index++)
            {
                try
                {
                    AutomationElement element = descendants[index];
                    // The Band is a native child of the taskbar. Depending on the
                    // Windows/UIA provider it can be exposed as Window, Pane, or
                    // Group. Never let our own previous rectangle become an
                    // "occupied taskbar icon" boundary and feed back into the
                    // next position calculation.
                    if (element.Current.ProcessId == Environment.ProcessId)
                    {
                        continue;
                    }

                    System.Windows.Rect bounds = element.Current.BoundingRectangle;
                    if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                    {
                        continue;
                    }

                    var rect = new NativeRect(
                        (int)Math.Floor(bounds.Left),
                        (int)Math.Floor(bounds.Top),
                        (int)Math.Ceiling(bounds.Right),
                        (int)Math.Ceiling(bounds.Bottom));
                    if (!Contains(taskbarRect, rect) ||
                        rect.Top > verticalMidpoint ||
                        rect.Bottom < verticalMidpoint)
                    {
                        continue;
                    }

                    elements.Add(new AutomationInfo(
                        rect,
                        element.Current.ControlType,
                        element.Current.AutomationId ?? string.Empty,
                        element.Current.ClassName ?? string.Empty,
                        element.Current.Name ?? string.Empty));
                }
                catch (ElementNotAvailableException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            int rightTolerance = Math.Max(4, taskbarRect.Height / 4);
            AutomationInfo? notification = elements
                .Where(item =>
                    IsContainer(item.ControlType) &&
                    item.Rect.Left >= taskbarRect.Left + taskbarRect.Width / 2 &&
                    item.Rect.Right >= taskbarRect.Right - rightTolerance &&
                    item.Rect.Width < taskbarRect.Width * 0.6)
                .OrderBy(item => item.Rect.Left)
                .FirstOrDefault();
            if (notification is null)
            {
                return false;
            }

            notificationLeft = notification.Rect.Left;
            int notificationBoundary = notificationLeft;
            int minimumWidth = Math.Max(8, taskbarRect.Height / 5);
            int[] occupiedEdges = elements
                .Where(item =>
                    IsTaskbarItem(item.ControlType) &&
                    item.Rect.Right <= notificationBoundary &&
                    item.Rect.Width >= minimumWidth &&
                    item.Rect.Width < taskbarRect.Width * 0.45)
                .Select(item => item.Rect.Right)
                .ToArray();
            if (occupiedEdges.Length == 0)
            {
                // Older taskbar UIA providers can expose an app button as a
                // compact Group/Pane instead of Button. Accept only icon-sized
                // containers; never use the broad task-list/ReBar container,
                // because its empty tail would unnecessarily collapse the
                // user's horizontal movement range.
                int maximumContainerWidth = Math.Max(96, taskbarRect.Height * 3);
                occupiedEdges = elements
                    .Where(item =>
                        IsContainer(item.ControlType) &&
                        item.Rect.Right <= notificationBoundary &&
                        item.Rect.Width >= minimumWidth &&
                        item.Rect.Width <= maximumContainerWidth)
                    .Select(item => item.Rect.Right)
                    .ToArray();
            }

            if (occupiedEdges.Length == 0)
            {
                return false;
            }

            occupiedRight = occupiedEdges.Max();
            return true;
        }
        catch (Exception exception) when (
            exception is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryProbeWin32(
        nint taskbar,
        NativeRect taskbarRect,
        out int occupiedRight,
        out int notificationLeft)
    {
        occupiedRight = 0;
        notificationLeft = 0;
        var children = new List<WindowInfo>();
        EnumChildWindows(taskbar, (handle, _) =>
        {
            if (!IsWindowVisible(handle) ||
                !GetWindowRect(handle, out NativeRect rect) ||
                !Intersects(taskbarRect, rect))
            {
                return true;
            }

            var name = new StringBuilder(256);
            _ = GetClassName(handle, name, name.Capacity);
            children.Add(new WindowInfo(rect, name.ToString()));
            return true;
        }, nint.Zero);

        NativeRect? notification = children
            .Where(item => item.ClassName.Contains("TrayNotify", StringComparison.OrdinalIgnoreCase))
            .Select(item => (NativeRect?)item.Rect)
            .OrderBy(item => item!.Value.Left)
            .FirstOrDefault();
        if (notification is null)
        {
            return false;
        }

        NativeRect? taskList = children
            .Where(item =>
                item.ClassName.Contains("TaskList", StringComparison.OrdinalIgnoreCase) ||
                item.ClassName.Contains("MSTask", StringComparison.OrdinalIgnoreCase))
            .Select(item => (NativeRect?)item.Rect)
            .OrderByDescending(item => item!.Value.Right)
            .FirstOrDefault();
        if (taskList is null)
        {
            return false;
        }

        occupiedRight = taskList.Value.Right;
        notificationLeft = notification.Value.Left;
        // On classic Windows 10 taskbars MSTaskListWClass often spans the
        // entire free strip even when only a few icons are present. Such an
        // overlap is not proof that the safe gap disappeared, so treat it as
        // unavailable and retain the last validated UIA boundaries.
        return occupiedRight < notificationLeft;
    }

    private static bool IsContainer(ControlType type) =>
        type == ControlType.Pane || type == ControlType.ToolBar || type == ControlType.Group;

    private static bool IsTaskbarItem(ControlType type) =>
        type == ControlType.Button ||
        type == ControlType.ListItem || type == ControlType.TabItem;

    private static bool Contains(NativeRect outer, NativeRect inner)
    {
        const int tolerance = 3;
        return inner.Left >= outer.Left - tolerance &&
            inner.Top >= outer.Top - tolerance &&
            inner.Right <= outer.Right + tolerance &&
            inner.Bottom <= outer.Bottom + tolerance;
    }

    private static bool Intersects(NativeRect first, NativeRect second) =>
        first.Left < second.Right && first.Right > second.Left &&
        first.Top < second.Bottom && first.Bottom > second.Top;

    private sealed record AutomationInfo(
        NativeRect Rect,
        ControlType ControlType,
        string AutomationId,
        string ClassName,
        string Name);

    private sealed record WindowInfo(NativeRect Rect, string ClassName);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private delegate bool EnumWindowsProc(nint windowHandle, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint windowHandle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        nint parentWindow,
        EnumWindowsProc callback,
        nint parameter);
}

public sealed record TaskbarRegionSnapshot(
    long Generation,
    nint TaskbarHandle,
    int TaskbarLeft,
    int TaskbarTop,
    int TaskbarRight,
    int TaskbarBottom,
    int SafeLeft,
    int SafeRight,
    uint TaskbarDpi,
    bool IsValid,
    bool HasTrustedBounds)
{
    public static TaskbarRegionSnapshot Invalid(
        long generation,
        nint taskbarHandle,
        int taskbarLeft = 0,
        int taskbarTop = 0,
        int taskbarRight = 0,
        int taskbarBottom = 0,
        uint taskbarDpi = 96) =>
        new(
            generation,
            taskbarHandle,
            taskbarLeft,
            taskbarTop,
            taskbarRight,
            taskbarBottom,
            0,
            0,
            taskbarDpi,
            false,
            false);

    public static TaskbarRegionSnapshot Unsafe(
        long generation,
        nint taskbarHandle,
        int taskbarLeft,
        int taskbarTop,
        int taskbarRight,
        int taskbarBottom,
        int safeLeft,
        int safeRight,
        uint taskbarDpi) =>
        new(
            generation,
            taskbarHandle,
            taskbarLeft,
            taskbarTop,
            taskbarRight,
            taskbarBottom,
            safeLeft,
            safeRight,
            taskbarDpi,
            false,
            true);
}
