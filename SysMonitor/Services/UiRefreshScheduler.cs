using System.Diagnostics;

namespace SysMonitor.Services;

/// <summary>
/// A trailing, latest-wins UI refresh gate. Producers only replace the pending
/// callback; at most one scheduled dispatcher callback is current.
/// </summary>
internal sealed class UiRefreshScheduler : IDisposable
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    private TimeSpan _interval;
    private readonly bool _enforceMinimumInterval;
    private readonly Action<Action> _dispatch;
    private readonly Func<bool> _isActive;
    private readonly Action _callback;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private Action? _pendingCallback;
    private bool _scheduled;
    private bool _disposed;
    private int _inFlight;
    private int _callbackThreadId;
    private long _scheduleVersion;
    private long _lastCallbackStartedTimestamp = long.MinValue;

    internal UiRefreshScheduler(
        Action<Action> dispatch,
        Func<bool> isActive,
        Action callback,
        TimeSpan? interval = null,
        bool enforceMinimumInterval = false)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _interval = interval ?? DefaultInterval;
        ValidateInterval(_interval, nameof(interval));
        _enforceMinimumInterval = enforceMinimumInterval;
    }

    internal void Request() => Request(_callback);

    internal void Request(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingCallback = callback;
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
            ScheduleLocked(GetNextDueTimeLocked());
        }
    }

    internal void SetInterval(TimeSpan interval)
    {
        ValidateInterval(interval, nameof(interval));

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _interval = interval;
            if (_enforceMinimumInterval && _scheduled && _pendingCallback is not null && _inFlight == 0)
            {
                ScheduleLocked(GetNextDueTimeLocked());
            }
        }
    }

    internal void InvalidatePending()
    {
        lock (_gate)
        {
            InvalidatePendingLocked();
        }
    }

    internal void RestartInterval()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _lastCallbackStartedTimestamp = Stopwatch.GetTimestamp();
            if (_enforceMinimumInterval && _scheduled && _pendingCallback is not null && _inFlight == 0)
            {
                ScheduleLocked(GetNextDueTimeLocked());
            }
        }
    }

    private void InvalidatePendingLocked()
    {
        _pendingCallback = null;
        _scheduled = _inFlight > 0;
        _scheduleVersion++;
        DisposeTimerLocked();
    }

    private void ScheduleLocked(TimeSpan due)
    {
        DisposeTimerLocked();
        long version = ++_scheduleVersion;
        _timer = new System.Threading.Timer(
            OnTimer,
            version,
            due,
            Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(object? state)
    {
        long version = (long)state!;
        lock (_gate)
        {
            if (_disposed || version != _scheduleVersion || !_scheduled || _pendingCallback is null)
            {
                return;
            }

            TimeSpan remaining = GetNextDueTimeLocked();
            if (_enforceMinimumInterval && remaining > TimeSpan.Zero)
            {
                ScheduleLocked(remaining);
                return;
            }
        }

        try
        {
            _dispatch(() => RunOnUi(version));
        }
        catch
        {
            lock (_gate)
            {
                if (version == _scheduleVersion)
                {
                    _scheduled = false;
                    _pendingCallback = null;
                    DisposeTimerLocked();
                }
            }
        }
    }

    private void RunOnUi(long version)
    {
        Action? pending;
        lock (_gate)
        {
            if (_disposed || version != _scheduleVersion || !_scheduled || _pendingCallback is null)
            {
                return;
            }

            if (_inFlight > 0)
            {
                return;
            }

            TimeSpan remaining = GetNextDueTimeLocked();
            if (_enforceMinimumInterval && remaining > TimeSpan.Zero)
            {
                ScheduleLocked(remaining);
                return;
            }

            pending = _pendingCallback;
            _pendingCallback = null;
            _inFlight++;
            _callbackThreadId = Environment.CurrentManagedThreadId;
            _lastCallbackStartedTimestamp = Stopwatch.GetTimestamp();
        }

        try
        {
            if (_isActive())
            {
                pending();
            }
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown and a closing WPF window are benign races.
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
                _callbackThreadId = 0;
                Monitor.PulseAll(_gate);

                if (!_disposed && _pendingCallback is not null)
                {
                    _scheduled = true;
                    ScheduleLocked(_enforceMinimumInterval ? GetNextDueTimeLocked() : _interval);
                }
                else if (!_disposed)
                {
                    _scheduled = false;
                    DisposeTimerLocked();
                }
            }
        }
    }

    private TimeSpan GetNextDueTimeLocked()
    {
        if (!_enforceMinimumInterval || _lastCallbackStartedTimestamp == long.MinValue)
        {
            return TimeSpan.Zero;
        }

        TimeSpan remaining = _interval - Stopwatch.GetElapsedTime(_lastCallbackStartedTimestamp);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static void ValidateInterval(TimeSpan interval, string parameterName)
    {
        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private void DisposeTimerLocked()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pendingCallback = null;
            _scheduled = false;
            _scheduleVersion++;
            DisposeTimerLocked();
            int currentThreadId = Environment.CurrentManagedThreadId;
            while (_inFlight > 0 && _callbackThreadId != currentThreadId)
            {
                Monitor.Wait(_gate);
            }
        }
    }
}
