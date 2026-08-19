namespace SysMonitor.Services;

/// <summary>
/// A trailing, latest-wins UI refresh gate.  Producers only set a dirty bit;
/// at most one timer and one dispatcher callback are outstanding.  A refresh
/// arriving during the callback is delivered on the next 250 ms boundary.
/// </summary>
internal sealed class UiRefreshScheduler : IDisposable
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimeSpan _interval;
    private readonly Action<Action> _dispatch;
    private readonly Func<bool> _isActive;
    private readonly Action _callback;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private bool _pending;
    private bool _scheduled;
    private bool _disposed;
    private int _inFlight;
    private int _callbackThreadId;

    internal UiRefreshScheduler(
        Action<Action> dispatch,
        Func<bool> isActive,
        Action callback,
        TimeSpan? interval = null)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _interval = interval ?? DefaultInterval;
        if (_interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _timer = new System.Threading.Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal void Request()
    {
        bool start;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = true;
            start = !_scheduled;
            if (start)
            {
                _scheduled = true;
            }
        }

        if (start)
        {
            TryArm(TimeSpan.Zero);
        }
    }

    private void OnTimer(object? state)
    {
        try
        {
            _dispatch(RunOnUi);
        }
        catch
        {
            lock (_gate)
            {
                _scheduled = false;
                _pending = false;
            }
        }
    }

    private void RunOnUi()
    {
        lock (_gate)
        {
            if (_disposed || !_pending)
            {
                _scheduled = false;
                return;
            }

            _pending = false;
            _inFlight++;
            _callbackThreadId = Environment.CurrentManagedThreadId;
        }

        try
        {
            if (_isActive())
            {
                _callback();
            }
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown and a closing WPF window are benign races.
        }
        finally
        {
            bool arm;
            lock (_gate)
            {
                _inFlight--;
                _callbackThreadId = 0;
                Monitor.PulseAll(_gate);
                arm = !_disposed && _pending;
                if (!arm)
                {
                    _scheduled = false;
                }
            }

            if (arm)
            {
                TryArm(_interval);
            }
        }
    }

    private void TryArm(TimeSpan due)
    {
        try
        {
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            lock (_gate)
            {
                _scheduled = false;
                _pending = false;
            }
        }
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
            _pending = false;
            _scheduled = false;
            int currentThreadId = Environment.CurrentManagedThreadId;
            while (_inFlight > 0 && _callbackThreadId != currentThreadId)
            {
                Monitor.Wait(_gate);
            }
        }

        _timer.Dispose();
    }
}
