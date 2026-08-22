using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SysMonitor.Services;

public sealed class TaskbarMotionTracker : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectReorder = 0x8004;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;

    private readonly Dispatcher _dispatcher;
    private readonly Action<Action> _postDispatcherNotification;
    private readonly Action _reposition;
    private readonly Action _requestRegionProbe;
    private readonly Action _requestRecoveryProbe;
    private readonly Action _healthCheck;
    private readonly WinEventDelegate _winEventCallback;
    private readonly DispatcherTimer _healthTimer;
    private readonly DispatcherTimer _eventDebounceTimer;
    private readonly object _subtreeGate = new();
    private readonly HashSet<nint> _confirmedSubtreeHandles = new();
    private nint _hook;
    private nint _taskbarHandle;
    private int _generation;
    private int _layoutChangePending;
    private int _eventGeneration;
    private int _layoutNotificationVersion;
    private int _layoutNotificationPosted;
    private int _closing = 1;
    private bool _started;
    private bool _hasTaskbarSignature;
    private DateTime _nextRecoveryProbeUtc;
    private TaskbarSignature _taskbarSignature;

    public TaskbarMotionTracker(
        Dispatcher dispatcher,
        Action reposition,
        Action requestRegionProbe,
        Action requestRecoveryProbe,
        Action healthCheck)
        : this(
            dispatcher,
            reposition,
            requestRegionProbe,
            requestRecoveryProbe,
            healthCheck,
            postDispatcherNotification: null)
    {
    }

    internal TaskbarMotionTracker(
        Dispatcher dispatcher,
        Action reposition,
        Action requestRegionProbe,
        Action requestRecoveryProbe,
        Action healthCheck,
        Action<Action>? postDispatcherNotification)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _postDispatcherNotification = postDispatcherNotification ??
            (callback => _ = dispatcher.InvokeAsync(callback, DispatcherPriority.Render));
        _reposition = reposition ?? throw new ArgumentNullException(nameof(reposition));
        _requestRegionProbe = requestRegionProbe ??
            throw new ArgumentNullException(nameof(requestRegionProbe));
        _requestRecoveryProbe = requestRecoveryProbe ??
            throw new ArgumentNullException(nameof(requestRecoveryProbe));
        _healthCheck = healthCheck ?? throw new ArgumentNullException(nameof(healthCheck));
        _winEventCallback = OnWinEvent;

        _healthTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _healthTimer.Tick += OnHealthTimerTick;
        _eventDebounceTimer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _eventDebounceTimer.Tick += OnEventDebounceTimerTick;
    }

    public void Start()
    {
        VerifyDispatcherAccess();
        if (_started)
        {
            _reposition();
            return;
        }

        _started = true;
        Volatile.Write(ref _closing, 0);
        RebuildHook();
        UpdateTaskbarSignature();
        _nextRecoveryProbeUtc = DateTime.UtcNow.AddSeconds(2);
        _healthTimer.Start();
        _requestRegionProbe();
        _reposition();
    }

    public void Stop()
    {
        VerifyDispatcherAccess();
        if (!_started)
        {
            return;
        }

        _started = false;
        Volatile.Write(ref _closing, 1);
        Interlocked.Increment(ref _generation);
        _healthTimer.Stop();
        _eventDebounceTimer.Stop();
        Unhook();
        _taskbarHandle = nint.Zero;
        lock (_subtreeGate)
        {
            _confirmedSubtreeHandles.Clear();
        }
        _hasTaskbarSignature = false;
        _nextRecoveryProbeUtc = DateTime.MinValue;
        Interlocked.Exchange(ref _layoutChangePending, 0);
    }

    public void NotifyTaskbarStateChanged()
    {
        VerifyDispatcherAccess();
        if (!_started || Volatile.Read(ref _closing) != 0)
        {
            return;
        }

        nint currentTaskbar = FindWindow("Shell_TrayWnd", null);
        if (currentTaskbar != _taskbarHandle || _hook == nint.Zero)
        {
            RebuildHook();
        }

        UpdateTaskbarSignature();
        _requestRegionProbe();
        _reposition();
    }

    public void Dispose()
    {
        // Close the notification gate even when Dispatcher shutdown prevents us
        // from synchronously running the full UI-thread Stop path.
        Volatile.Write(ref _closing, 1);
        Interlocked.Increment(ref _generation);

        if (_dispatcher.CheckAccess())
        {
            Stop();
        }
        else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
        {
            _dispatcher.Invoke(Stop);
        }

        _healthTimer.Tick -= OnHealthTimerTick;
        _eventDebounceTimer.Tick -= OnEventDebounceTimerTick;
    }

    private void OnHealthTimerTick(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _closing) != 0)
        {
            return;
        }

        nint currentTaskbar = FindWindow("Shell_TrayWnd", null);
        bool rebuilt = currentTaskbar != _taskbarHandle || _hook == nint.Zero;
        if (rebuilt)
        {
            RebuildHook();
        }

        bool signatureChanged = UpdateTaskbarSignature();
        if (rebuilt || signatureChanged)
        {
            _nextRecoveryProbeUtc = DateTime.UtcNow.AddSeconds(2);
            Interlocked.Exchange(ref _layoutChangePending, 1);
            ScheduleQuietProbe(Volatile.Read(ref _generation));
        }
        else
        {
            if (!_eventDebounceTimer.IsEnabled &&
                DateTime.UtcNow >= _nextRecoveryProbeUtc)
            {
                _nextRecoveryProbeUtc = DateTime.UtcNow.AddSeconds(2);
                _requestRecoveryProbe();
            }

            _healthCheck();
        }
    }

    private void RebuildHook()
    {
        VerifyDispatcherAccess();
        _ = Interlocked.Increment(ref _generation);
        Unhook();

        lock (_subtreeGate)
        {
            _confirmedSubtreeHandles.Clear();
        }

        _taskbarHandle = FindWindow("Shell_TrayWnd", null);
        if (_taskbarHandle == nint.Zero || Volatile.Read(ref _closing) != 0)
        {
            return;
        }

        lock (_subtreeGate)
        {
            _confirmedSubtreeHandles.Add(_taskbarHandle);
        }
        _ = EnumChildWindows(
            _taskbarHandle,
            (handle, _) =>
            {
                lock (_subtreeGate)
                {
                    _confirmedSubtreeHandles.Add(handle);
                }
                return true;
            },
            nint.Zero);

        _ = GetWindowThreadProcessId(_taskbarHandle, out uint processId);
        if (processId == 0)
        {
            return;
        }

        _hook = SetWinEventHook(
            EventObjectCreate,
            EventObjectLocationChange,
            nint.Zero,
            _winEventCallback,
            processId,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        int generation = Volatile.Read(ref _generation);
        bool supportedEvent = eventType == EventObjectLocationChange || eventType is
            EventObjectCreate or EventObjectDestroy or EventObjectShow or
            EventObjectHide or EventObjectReorder;
        if (Volatile.Read(ref _closing) != 0 ||
            generation == 0 ||
            !supportedEvent ||
            windowHandle == nint.Zero ||
            !AcceptEventHandle(eventType, windowHandle))
        {
            return;
        }

        QueueLayoutChangeNotification(generation);
    }

    private void QueueLayoutChangeNotification(int generation)
    {
        Interlocked.Exchange(ref _layoutChangePending, 1);
        Volatile.Write(ref _eventGeneration, generation);
        _ = Interlocked.Increment(ref _layoutNotificationVersion);
        PostLayoutChangeNotification();
    }

    // These narrow hooks keep coalescing tests independent of the shell process and WinEvent hook.
    internal void ActivateForTesting(int generation)
    {
        VerifyDispatcherAccess();
        if (generation == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        _started = true;
        Volatile.Write(ref _closing, 0);
        Volatile.Write(ref _generation, generation);
    }

    internal void NotifyAcceptedLayoutChangeForTesting()
    {
        int generation = Volatile.Read(ref _generation);
        if (Volatile.Read(ref _closing) != 0 || generation == 0)
        {
            return;
        }

        QueueLayoutChangeNotification(generation);
    }

    internal void RunEventDebounceForTesting()
    {
        VerifyDispatcherAccess();
        OnEventDebounceTimerTick(null, EventArgs.Empty);
    }

    private void PostLayoutChangeNotification()
    {
        if (Interlocked.CompareExchange(ref _layoutNotificationPosted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _postDispatcherNotification(OnLayoutChangeNotification);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _layoutNotificationPosted, 0);
        }
        catch (TaskCanceledException)
        {
            Interlocked.Exchange(ref _layoutNotificationPosted, 0);
        }
    }

    private void OnLayoutChangeNotification()
    {
        int observedVersion = Volatile.Read(ref _layoutNotificationVersion);
        try
        {
            if (Volatile.Read(ref _closing) != 0)
            {
                return;
            }

            int generation = Volatile.Read(ref _eventGeneration);
            bool layoutDirty = Volatile.Read(ref _layoutChangePending) != 0;
            if (layoutDirty &&
                generation != 0 &&
                generation == Volatile.Read(ref _generation))
            {
                ScheduleQuietProbe(generation);
            }
        }
        finally
        {
            CompleteLayoutChangeNotification(observedVersion);
        }
    }

    private void CompleteLayoutChangeNotification(int observedVersion)
    {
        Interlocked.Exchange(ref _layoutNotificationPosted, 0);
        if (Volatile.Read(ref _closing) != 0 ||
            Volatile.Read(ref _layoutNotificationVersion) == observedVersion)
        {
            return;
        }

        PostLayoutChangeNotification();
    }

    private void OnEventDebounceTimerTick(object? sender, EventArgs e)
    {
        _eventDebounceTimer.Stop();
        bool regionDirty = Interlocked.Exchange(ref _layoutChangePending, 0) != 0;
        if (Volatile.Read(ref _closing) != 0 ||
            _eventGeneration != Volatile.Read(ref _generation))
        {
            return;
        }

        nint currentTaskbar = FindWindow("Shell_TrayWnd", null);
        if (currentTaskbar != _taskbarHandle || _hook == nint.Zero)
        {
            RebuildHook();
        }

        bool signatureChanged = UpdateTaskbarSignature();
        if (regionDirty || signatureChanged)
        {
            _requestRegionProbe();
        }
    }

    private bool AcceptEventHandle(uint eventType, nint windowHandle)
    {
        nint taskbar = _taskbarHandle;
        if (eventType == EventObjectDestroy)
        {
            lock (_subtreeGate)
            {
                return windowHandle == taskbar ||
                    _confirmedSubtreeHandles.Remove(windowHandle);
            }
        }

        if (windowHandle != taskbar && !IsChild(taskbar, windowHandle))
        {
            return false;
        }

        lock (_subtreeGate)
        {
            _confirmedSubtreeHandles.Add(windowHandle);
        }
        return true;
    }

    private void ScheduleQuietProbe(int generation)
    {
        if (Volatile.Read(ref _closing) != 0 ||
            generation != Volatile.Read(ref _generation))
        {
            return;
        }

        Volatile.Write(ref _eventGeneration, generation);
        _eventDebounceTimer.Stop();
        _eventDebounceTimer.Start();
    }

    private bool UpdateTaskbarSignature()
    {
        TaskbarSignature signature = CaptureTaskbarSignature(_taskbarHandle);
        bool changed = !_hasTaskbarSignature || signature != _taskbarSignature;
        _taskbarSignature = signature;
        _hasTaskbarSignature = true;
        return changed;
    }

    private static TaskbarSignature CaptureTaskbarSignature(nint taskbarHandle)
    {
        bool visible = taskbarHandle != nint.Zero && IsWindowVisible(taskbarHandle);
        NativeRect rect = default;
        _ = taskbarHandle != nint.Zero &&
            GetWindowRect(taskbarHandle, out rect);
        uint dpi = taskbarHandle == nint.Zero ? 0 : GetDpiForWindow(taskbarHandle);
        return new TaskbarSignature(
            taskbarHandle,
            visible,
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top,
            dpi);
    }

    private void Unhook()
    {
        nint hook = _hook;
        _hook = nint.Zero;
        if (hook != nint.Zero)
        {
            _ = UnhookWinEvent(hook);
        }
    }

    private void VerifyDispatcherAccess()
    {
        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "TaskbarMotionTracker lifecycle must run on its owning Dispatcher.");
        }
    }

    private delegate void WinEventDelegate(
        nint hook,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    private delegate bool EnumWindowsProc(nint windowHandle, nint parameter);

    private readonly record struct TaskbarSignature(
        nint Handle,
        bool Visible,
        int Left,
        int Top,
        int Width,
        int Height,
        uint Dpi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint windowHandle,
        out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parentWindow, nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        nint parentWindow,
        EnumWindowsProc callback,
        nint parameter);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookModule,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
}
