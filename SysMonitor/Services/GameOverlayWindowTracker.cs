using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SysMonitor.Services;

/// <summary>
/// Follows a selected window using WinEvent notifications.  The hook is
/// deliberately out-of-context: no code is injected into the target process
/// and the native callback only posts coalesced work back to the dispatcher.
/// </summary>
public sealed class GameOverlayWindowTracker : IDisposable
{
    internal const uint EventSystemForeground = 0x0003;
    internal const uint EventSystemMoveSizeStart = 0x000A;
    internal const uint EventSystemMoveSizeEnd = 0x000B;
    internal const uint EventSystemMinimizeStart = 0x0016;
    internal const uint EventSystemMinimizeEnd = 0x0017;
    internal const uint EventObjectDestroy = 0x8001;
    internal const uint EventObjectLocationChange = 0x800B;
    internal const int ObjIdWindow = 0;
    internal const int WinEventOutOfContext = 0x0000;
    internal const int WinEventSkipOwnProcess = 0x0002;

    private const int MoveTimerMilliseconds = 16;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _moveTimer;
    private readonly WinEventDelegate _winEventDelegate;
    private readonly object _queueGate = new();
    private TrackerWork _pendingWork;
    private long _pendingGeneration;
    private bool _dispatchPosted;
    private long _bindingGeneration;
    private nint _overlayWindow;
    private GameOverlayTargetIdentity? _targetIdentity;
    private nint _systemHook;
    private nint _objectHook;
    private bool _fastTrackingEnabled;
    private bool _moving;
    private bool _minimized;
    private bool _disposed;
    private GameOverlayTrackingState _state = GameOverlayTrackingState.Detached;

    public GameOverlayWindowTracker(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _winEventDelegate = WinEventCallback;
        _moveTimer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(MoveTimerMilliseconds)
        };
        _moveTimer.Tick += OnMoveTimerTick;
    }

    /// <summary>Raised on the dispatcher when a fresh target geometry query is needed.</summary>
    public event EventHandler? PositionRefreshRequested;

    /// <summary>Raised on the dispatcher when the target is minimized or destroyed.</summary>
    public event EventHandler? TargetInvalidated;

    /// <summary>Raised on the dispatcher when the target enters its minimized state.</summary>
    public event EventHandler? TargetMinimized;

    /// <summary>Raised on the dispatcher after a minimized target is validated as restored.</summary>
    public event EventHandler? TargetRestored;

    internal GameOverlayTrackingState State => _state;
    internal bool FastTrackingEnabled => _fastTrackingEnabled;
    internal bool IsMoving => _moving;
    internal bool IsTargetMinimized => _minimized;

    /// <summary>Associates the overlay HWND used to filter self-generated events.</summary>
    public void SetOverlayWindow(nint overlayWindow)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => SetOverlayWindow(overlayWindow));
            return;
        }

        if (_disposed || _overlayWindow == overlayWindow)
        {
            return;
        }

        _overlayWindow = overlayWindow;
        if (_targetIdentity is not null)
        {
            TryStartFastTracking();
        }
    }

    /// <summary>
    /// Changes the target binding.  The process start time is the generation
    /// component that prevents HWND reuse from being mistaken for the target.
    /// </summary>
    public void SetTarget(ForegroundTarget? target)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => SetTarget(target));
            return;
        }

        if (_disposed)
        {
            return;
        }

        StopFastTracking();
        lock (_queueGate)
        {
            _bindingGeneration++;
            _pendingWork = TrackerWork.None;
            _pendingGeneration = _bindingGeneration;
        }

        _targetIdentity = null;
        _minimized = false;
        _state = GameOverlayTrackingState.Detached;
        if (target is null ||
            target.WindowHandle == nint.Zero ||
            target.ProcessId <= 0 ||
            target.ProcessStartedAt == default)
        {
            return;
        }

        var identity = new GameOverlayTargetIdentity(
            target.WindowHandle,
            target.ProcessId,
            target.ProcessStartedAt);
        _targetIdentity = identity;

        // A process identity lookup can fail transiently (for example, due to
        // access restrictions).  In that case retain the regular 750 ms
        // recovery poll, but do not install a fast hook with an unverified
        // identity.
        IdentityValidationResult validation = ValidateTargetIdentity(identity);
        if (validation != IdentityValidationResult.Valid)
        {
            _state = GameOverlayTrackingState.FastTrackingUnavailable;
            if (validation == IdentityValidationResult.Mismatch)
            {
                QueueWork(TrackerWork.Invalidate);
            }

            return;
        }

        TryStartFastTracking();
    }

    public void Dispose()
    {
        if (!_dispatcher.CheckAccess())
        {
            try
            {
                _dispatcher.Invoke(Dispose);
            }
            catch (InvalidOperationException)
            {
                // Dispatcher shutdown is equivalent to disposal.
                DisposeCore();
            }

            return;
        }

        DisposeCore();
        GC.SuppressFinalize(this);
    }

    internal static bool IsRelevantWinEvent(
        uint eventType,
        nint eventWindow,
        int objectId,
        int childId,
        nint targetWindow,
        nint overlayWindow)
    {
        if (targetWindow == nint.Zero || eventWindow == overlayWindow)
        {
            return false;
        }

        return eventType switch
        {
            EventSystemForeground => true,
            EventSystemMoveSizeStart or EventSystemMoveSizeEnd or
                EventSystemMinimizeStart or EventSystemMinimizeEnd => eventWindow == targetWindow,
            EventObjectLocationChange or EventObjectDestroy =>
                eventWindow == targetWindow && objectId == ObjIdWindow && childId == 0,
            _ => false
        };
    }

    internal static TrackerWork CoalesceWork(TrackerWork current, TrackerWork incoming) => current | incoming;

    internal static TrackerWork ClassifyWinEvent(uint eventType) => eventType switch
    {
        EventSystemMoveSizeStart => TrackerWork.MoveStart,
        EventSystemMoveSizeEnd => TrackerWork.MoveEnd,
        EventSystemMinimizeStart => TrackerWork.MinimizeStart,
        EventSystemMinimizeEnd => TrackerWork.MinimizeEnd,
        EventSystemForeground => TrackerWork.Revalidate,
        EventObjectDestroy => TrackerWork.Invalidate,
        EventObjectLocationChange => TrackerWork.Refresh,
        _ => TrackerWork.None
    };

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _moveTimer.Stop();
        _moveTimer.Tick -= OnMoveTimerTick;
        StopHooks();
        lock (_queueGate)
        {
            _bindingGeneration++;
            _pendingWork = TrackerWork.None;
            _dispatchPosted = false;
        }

        _targetIdentity = null;
        _fastTrackingEnabled = false;
        _moving = false;
        _minimized = false;
        _state = GameOverlayTrackingState.Disposed;
    }

    private void OnMoveTimerTick(object? sender, EventArgs e)
    {
        // Even though this is a dispatcher callback, route through the same
        // coalescing path as native events so a busy render queue cannot grow.
        if (_moving && !_disposed)
        {
            QueueWork(TrackerWork.MoveTick);
        }
    }

    private void WinEventCallback(
        nint hook,
        uint eventType,
        nint eventWindow,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        try
        {
            GameOverlayTargetIdentity? identity = _targetIdentity;
            if (_disposed || identity is null ||
                !IsRelevantWinEvent(
                    eventType,
                    eventWindow,
                    objectId,
                    childId,
                    identity.WindowHandle,
                    _overlayWindow))
            {
                return;
            }

            TrackerWork work = ClassifyWinEvent(eventType);
            if (work != TrackerWork.None)
            {
                QueueWork(work);
            }
        }
        catch
        {
            // Never let an unmanaged callback escape into user32.  The 750 ms
            // recovery poll remains the safe fallback if a queue operation is
            // unavailable during dispatcher shutdown.
        }
    }

    private void QueueWork(TrackerWork work)
    {
        if (work == TrackerWork.None)
        {
            return;
        }

        bool post;
        long generation;
        lock (_queueGate)
        {
            if (_disposed || _targetIdentity is null)
            {
                return;
            }

            generation = _bindingGeneration;
            if (_pendingGeneration != generation)
            {
                _pendingWork = TrackerWork.None;
                _pendingGeneration = generation;
            }

            _pendingWork = CoalesceWork(_pendingWork, work);
            post = !_dispatchPosted;
            _dispatchPosted |= post;
        }

        if (!post)
        {
            return;
        }

        try
        {
            _ = _dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(() => ProcessPending(generation)));
        }
        catch (InvalidOperationException)
        {
            lock (_queueGate)
            {
                _dispatchPosted = false;
            }
        }
    }

    private void ProcessPending(long generation)
    {
        TrackerWork work;
        lock (_queueGate)
        {
            work = generation == _bindingGeneration ? _pendingWork : TrackerWork.None;
            _pendingWork = TrackerWork.None;
            _dispatchPosted = false;
        }

        if (work == TrackerWork.None || _disposed || generation != _bindingGeneration || _targetIdentity is null)
        {
            return;
        }

        if ((work & TrackerWork.Invalidate) != 0)
        {
            HandleInvalidation();
            return;
        }

        if ((work & TrackerWork.MinimizeStart) != 0)
        {
            HandleMinimizeStart();
        }

        if ((work & TrackerWork.MinimizeEnd) != 0)
        {
            HandleMinimizeEnd();
        }

        if ((work & TrackerWork.MoveStart) != 0)
        {
            HandleMoveStart();
        }

        if ((work & TrackerWork.MoveEnd) != 0)
        {
            HandleMoveEnd();
        }

        if ((work & TrackerWork.MoveTick) != 0)
        {
            HandleMoveTick();
        }

        if ((work & TrackerWork.Revalidate) != 0)
        {
            HandleRevalidate();
        }

        if ((work & TrackerWork.Refresh) != 0)
        {
            HandleRefresh();
        }
    }

    private void HandleMoveStart()
    {
        if (_minimized || !_fastTrackingEnabled || _targetIdentity is not GameOverlayTargetIdentity identity)
        {
            return;
        }

        switch (ValidateTargetIdentity(identity))
        {
            case IdentityValidationResult.Valid:
                _moving = true;
                _state = GameOverlayTrackingState.Moving;
                _moveTimer.Start();
                RequestPositionRefresh();
                break;
            default:
                DisableFastTracking();
                break;
        }
    }

    private void HandleMinimizeStart()
    {
        if (_targetIdentity is not GameOverlayTargetIdentity identity)
        {
            return;
        }

        // Always treat minimize as minimize, never as invalidation.
        // Even if identity validation fails transiently, the window
        // event was for our bound HWND so we know it was minimized.
        _moving = false;
        _moveTimer.Stop();
        _minimized = true;
        _state = GameOverlayTrackingState.Minimized;
        TargetMinimized?.Invoke(this, EventArgs.Empty);
    }

    private void HandleMinimizeEnd()
    {
        if (!_minimized || _targetIdentity is not GameOverlayTargetIdentity identity)
        {
            return;
        }

        switch (ValidateTargetIdentity(identity))
        {
            case IdentityValidationResult.Valid:
                _minimized = false;
                _state = _fastTrackingEnabled
                    ? GameOverlayTrackingState.Tracking
                    : GameOverlayTrackingState.FastTrackingUnavailable;
                if (!_fastTrackingEnabled)
                {
                    TryStartFastTracking();
                }

                TargetRestored?.Invoke(this, EventArgs.Empty);
                RequestPositionRefresh();
                break;
            default:
                // Restore even on transient failure — the window event was
                // for our bound HWND. The controller poll will clean up if
                // the process truly exited.
                _minimized = false;
                _state = GameOverlayTrackingState.FastTrackingUnavailable;
                DisableFastTracking();
                TargetRestored?.Invoke(this, EventArgs.Empty);
                RequestPositionRefresh();
                break;
        }
    }

    private void HandleMoveEnd()
    {
        _moving = false;
        _moveTimer.Stop();
        if (_minimized)
        {
            return;
        }

        if (_targetIdentity is not GameOverlayTargetIdentity identity)
        {
            return;
        }

        switch (ValidateTargetIdentity(identity))
        {
            case IdentityValidationResult.Valid:
                _state = _fastTrackingEnabled
                    ? GameOverlayTrackingState.Tracking
                    : GameOverlayTrackingState.FastTrackingUnavailable;
                RequestPositionRefresh();
                break;
            default:
                DisableFastTracking();
                break;
        }
    }

    private void HandleMoveTick()
    {
        if (!_moving || !_fastTrackingEnabled || _targetIdentity is not GameOverlayTargetIdentity identity)
        {
            return;
        }

        switch (ValidateTargetIdentity(identity))
        {
            case IdentityValidationResult.Valid:
                RequestPositionRefresh();
                break;
            default:
                DisableFastTracking();
                break;
        }
    }

    private void HandleRevalidate()
    {
        if (_minimized)
        {
            if (_targetIdentity is GameOverlayTargetIdentity minIdentity &&
                IsWindow(minIdentity.WindowHandle) &&
                !IsIconic(minIdentity.WindowHandle))
            {
                HandleMinimizeEnd();
            }
            return;
        }

        if (_targetIdentity is not GameOverlayTargetIdentity identity)
        {
            return;
        }

        // Revalidate is triggered by EventSystemForeground which fires for
        // ANY window gaining focus (Win key, Alt+Tab, clicking desktop, etc.).
        // The game process is still alive — never invalidate the target here.
        // Only refresh position or downgrade to slow-poll on transient failure.
        switch (ValidateTargetIdentity(identity))
        {
            case IdentityValidationResult.Valid:
                if (!_fastTrackingEnabled)
                {
                    TryStartFastTracking();
                }

                RequestPositionRefresh();
                break;
            default:
                // Mismatch or Unavailable: the target HWND might be transiently
                // inaccessible (anti-cheat, fullscreen transition, UAC prompt).
                // Fall back to slow polling; the 250ms controller loop will
                // re-acquire the target if the process truly exited.
                DisableFastTracking();
                RequestPositionRefresh();
                break;
        }
    }

    private void HandleRefresh()
    {
        if (_minimized || !_fastTrackingEnabled || _targetIdentity is not GameOverlayTargetIdentity identity)
        {
            return;
        }

        switch (ValidateTargetIdentity(identity))
        {
            case IdentityValidationResult.Valid:
                RequestPositionRefresh();
                break;
            default:
                DisableFastTracking();
                break;
        }
    }

    private void HandleInvalidation()
    {
        if (_disposed || _targetIdentity is null)
        {
            return;
        }

        _moving = false;
        _moveTimer.Stop();
        StopFastTracking();
        _state = GameOverlayTrackingState.Invalidated;
        TargetInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void RequestPositionRefresh() =>
        PositionRefreshRequested?.Invoke(this, EventArgs.Empty);

    private void TryStartFastTracking()
    {
        if (_disposed || _fastTrackingEnabled || _targetIdentity is not GameOverlayTargetIdentity identity ||
            _overlayWindow == nint.Zero)
        {
            return;
        }

        if (ValidateTargetIdentity(identity) != IdentityValidationResult.Valid)
        {
            _state = GameOverlayTrackingState.FastTrackingUnavailable;
            return;
        }

        try
        {
            _systemHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemMinimizeEnd,
                nint.Zero,
                _winEventDelegate,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
            _objectHook = SetWinEventHook(
                EventObjectDestroy,
                EventObjectLocationChange,
                nint.Zero,
                _winEventDelegate,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
            if (_systemHook == nint.Zero || _objectHook == nint.Zero)
            {
                StopHooks();
                _state = GameOverlayTrackingState.FastTrackingUnavailable;
                return;
            }

            _fastTrackingEnabled = true;
            _state = GameOverlayTrackingState.Tracking;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or
            SEHException or Win32Exception)
        {
            StopHooks();
            _state = GameOverlayTrackingState.FastTrackingUnavailable;
        }
    }

    private void DisableFastTracking()
    {
        _fastTrackingEnabled = false;
        _moving = false;
        _moveTimer.Stop();
        StopHooks();
        if (!_disposed && _state != GameOverlayTrackingState.Invalidated)
        {
            _state = GameOverlayTrackingState.FastTrackingUnavailable;
        }
    }

    private void StopFastTracking()
    {
        _fastTrackingEnabled = false;
        _moving = false;
        _moveTimer.Stop();
        StopHooks();
    }

    private void StopHooks()
    {
        nint systemHook = _systemHook;
        nint objectHook = _objectHook;
        _systemHook = nint.Zero;
        _objectHook = nint.Zero;
        foreach (nint hook in new[] { systemHook, objectHook })
        {
            if (hook == nint.Zero)
            {
                continue;
            }

            try { _ = UnhookWinEvent(hook); }
            catch (Exception exception) when (
                exception is DllNotFoundException or EntryPointNotFoundException or SEHException)
            {
            }
        }

    }

    private static IdentityValidationResult ValidateTargetIdentity(GameOverlayTargetIdentity identity)
    {
        try
        {
            if (!IsWindow(identity.WindowHandle))
            {
                return IdentityValidationResult.Mismatch;
            }

            _ = GetWindowThreadProcessId(identity.WindowHandle, out uint processId);
            if (processId == 0 || processId != (uint)identity.ProcessId)
            {
                return IdentityValidationResult.Mismatch;
            }

            if (!TryReadProcessStartedAt(identity.ProcessId, out DateTimeOffset startedAt))
            {
                return IdentityValidationResult.Unavailable;
            }

            return startedAt == identity.ProcessStartedAt
                ? IdentityValidationResult.Valid
                : IdentityValidationResult.Mismatch;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or
            SEHException or Win32Exception)
        {
            return IdentityValidationResult.Unavailable;
        }
    }

    private static bool TryReadProcessStartedAt(int processId, out DateTimeOffset startedAt)
    {
        startedAt = default;
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
            return startedAt != default;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            UnauthorizedAccessException or NotSupportedException or Win32Exception)
        {
            return false;
        }
    }

    [Flags]
    internal enum TrackerWork
    {
        None = 0,
        Refresh = 1,
        MoveStart = 2,
        MoveEnd = 4,
        MoveTick = 8,
        Revalidate = 16,
        Invalidate = 32,
        MinimizeStart = 64,
        MinimizeEnd = 128
    }

    internal enum GameOverlayTrackingState
    {
        Detached,
        Tracking,
        Moving,
        FastTrackingUnavailable,
        Minimized,
        Invalidated,
        Disposed
    }

    internal sealed record GameOverlayTargetIdentity(
        nint WindowHandle,
        int ProcessId,
        DateTimeOffset ProcessStartedAt)
    {
        public bool Matches(nint windowHandle, int processId, DateTimeOffset processStartedAt) =>
            WindowHandle == windowHandle &&
            ProcessId == processId &&
            ProcessStartedAt == processStartedAt;
    }

    private enum IdentityValidationResult
    {
        Valid,
        Mismatch,
        Unavailable
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventDelegate(
        nint hook,
        uint eventType,
        nint eventWindow,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint moduleHandle,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}
