using System.Windows.Threading;

namespace SysMonitor.UI;

/// <summary>
/// Coalesces slider edits into one render-priority callback.  WPF's input
/// events can arrive much faster than a window can be repositioned; retaining
/// only a dirty bit keeps the queue bounded while the callback reads the
/// latest textbox/slider values.
/// </summary>
internal sealed class HudPreviewScheduler : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _callback;
    private readonly DispatcherPriority _priority;
    private readonly object _gate = new();
    private bool _requested;
    private bool _scheduled;
    private bool _disposed;
    private int _inFlight;
    private int _callbackThreadId;

    internal HudPreviewScheduler(
        Dispatcher dispatcher,
        Action callback,
        DispatcherPriority priority = DispatcherPriority.Render)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _priority = priority;
    }

    internal void Request()
    {
        lock (_gate)
        {
            if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                return;
            }

            _requested = true;
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
        }

        try
        {
            _dispatcher.BeginInvoke(_priority, new Action(Run));
        }
        catch (InvalidOperationException)
        {
            lock (_gate)
            {
                _scheduled = false;
                _requested = false;
            }
        }
    }

    internal void Cancel()
    {
        lock (_gate)
        {
            _requested = false;
        }
    }

    private void Run()
    {
        lock (_gate)
        {
            if (_disposed || !_requested)
            {
                _scheduled = false;
                return;
            }

            _requested = false;
            _inFlight++;
            _callbackThreadId = Environment.CurrentManagedThreadId;
        }

        try
        {
            if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                _callback();
            }
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown and a concurrently closed settings window
            // are normal lifecycle races; a stale preview is intentionally
            // discarded.
        }
        finally
        {
            bool scheduleAgain;
            lock (_gate)
            {
                _inFlight--;
                _callbackThreadId = 0;
                Monitor.PulseAll(_gate);
                scheduleAgain = !_disposed && _requested &&
                    !_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished;
                if (!scheduleAgain)
                {
                    _scheduled = false;
                }
            }

            if (scheduleAgain)
            {
                try
                {
                    _dispatcher.BeginInvoke(_priority, new Action(Run));
                }
                catch (InvalidOperationException)
                {
                    lock (_gate)
                    {
                        _scheduled = false;
                        _requested = false;
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _requested = false;
            int currentThreadId = Environment.CurrentManagedThreadId;
            while (_inFlight > 0 && _callbackThreadId != currentThreadId)
            {
                Monitor.Wait(_gate);
            }
        }
    }
}
