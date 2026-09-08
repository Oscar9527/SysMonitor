namespace SysMonitor.Services;

public readonly record struct TaskbarConstraintKey(
    nint TaskbarHandle,
    int Width,
    int Height,
    uint Dpi);

public readonly record struct TaskbarSafeConstraint(
    TaskbarConstraintKey Key,
    int Left,
    int Right)
{
    public bool IsValid => Key.TaskbarHandle != nint.Zero && Left < Right;
}

/// <summary>
/// Keeps trusted taskbar boundaries conservative. Boundaries can contract at
/// once, but an expansion must be observed twice without an intervening miss.
/// </summary>
public sealed class TaskbarSafeConstraintTracker
{
    private TaskbarSafeConstraint? _current;
    private int? _pendingExpandedLeft;
    private int? _pendingExpandedRight;
    private long _lastObservedGeneration = long.MinValue;

    public TaskbarSafeConstraint? Current => _current;
    public bool HasPendingExpansion =>
        _pendingExpandedLeft is not null || _pendingExpandedRight is not null;

    public TaskbarSafeConstraint? Observe(TaskbarRegionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int width = snapshot.TaskbarRight - snapshot.TaskbarLeft;
        int height = snapshot.TaskbarBottom - snapshot.TaskbarTop;
        var key = new TaskbarConstraintKey(
            snapshot.TaskbarHandle,
            width,
            height,
            snapshot.TaskbarDpi == 0 ? 96 : snapshot.TaskbarDpi);

        if (_current is { } current && current.Key != key)
        {
            Reset();
        }

        // Repositioning can consume the same cached snapshot many times. Only
        // a new monitor generation is a new observation and may confirm an
        // outward expansion.
        if (snapshot.Generation == _lastObservedGeneration)
        {
            return _current;
        }
        _lastObservedGeneration = snapshot.Generation;

        if (!snapshot.IsValid ||
            key.TaskbarHandle == nint.Zero ||
            width <= 0 ||
            height <= 0)
        {
            if (!snapshot.IsValid && snapshot.HasTrustedBounds)
            {
                // The probe positively identified conflicting/overlapping
                // occupied bounds. Keeping an older, wider interval here could
                // cover taskbar icons, so Unsafe invalidates it immediately.
                _current = null;
            }
            ClearPendingExpansions();
            return _current;
        }

        int candidateLeft = snapshot.SafeLeft - snapshot.TaskbarLeft;
        int candidateRight = snapshot.SafeRight - snapshot.TaskbarLeft;
        if (candidateLeft < 0 || candidateRight > width || candidateLeft >= candidateRight)
        {
            ClearPendingExpansions();
            return _current;
        }

        if (_current is null)
        {
            _current = new TaskbarSafeConstraint(key, candidateLeft, candidateRight);
            ClearPendingExpansions();
            return _current;
        }

        current = _current.Value;
        int acceptedLeft = current.Left;
        int acceptedRight = current.Right;

        if (candidateLeft >= current.Left)
        {
            acceptedLeft = candidateLeft;
            _pendingExpandedLeft = null;
        }
        else if (_pendingExpandedLeft == candidateLeft)
        {
            acceptedLeft = candidateLeft;
            _pendingExpandedLeft = null;
        }
        else
        {
            _pendingExpandedLeft = candidateLeft;
        }

        if (candidateRight <= current.Right)
        {
            acceptedRight = candidateRight;
            _pendingExpandedRight = null;
        }
        else if (_pendingExpandedRight == candidateRight)
        {
            acceptedRight = candidateRight;
            _pendingExpandedRight = null;
        }
        else
        {
            _pendingExpandedRight = candidateRight;
        }

        _current = new TaskbarSafeConstraint(key, acceptedLeft, acceptedRight);
        return _current;
    }

    public void Reset()
    {
        _current = null;
        _lastObservedGeneration = long.MinValue;
        ClearPendingExpansions();
    }

    public void RejectPendingExpansion(long observedGeneration)
    {
        _lastObservedGeneration = Math.Max(
            _lastObservedGeneration,
            observedGeneration);
        ClearPendingExpansions();
    }

    private void ClearPendingExpansions()
    {
        _pendingExpandedLeft = null;
        _pendingExpandedRight = null;
    }
}

public readonly record struct TaskbarBandRect(
    int X,
    int Y,
    int Width,
    int Height);

public readonly record struct TaskbarPlacementDecision(
    bool HideRequested,
    bool SetWindowPosition,
    TaskbarBandRect Rect)
{
    public static TaskbarPlacementDecision Hide() => new(true, false, default);
}

public static class TaskbarPlacementStabilizer
{
    public static bool IsHorizontal(int taskbarWidth, int taskbarHeight) =>
        taskbarWidth > 0 && taskbarHeight > 0 && taskbarWidth >= taskbarHeight;

    public static int CenteredLocalY(int taskbarHeight, int bandHeight) =>
        taskbarHeight >= bandHeight
            ? (taskbarHeight - bandHeight) / 2
            : 0;

    public static TaskbarPlacementDecision Decide(
        TaskbarSafeConstraint constraint,
        int taskbarHeight,
        int desiredWidth,
        int desiredHeight,
        double positionPercent,
        TaskbarBandRect? current,
        bool explicitLayoutChange)
    {
        if (!constraint.IsValid ||
            desiredWidth <= 0 ||
            desiredHeight <= 0 ||
            !IsHorizontal(constraint.Key.Width, constraint.Key.Height))
        {
            return TaskbarPlacementDecision.Hide();
        }

        int minimumX = constraint.Left;
        int maximumX = constraint.Right - desiredWidth;
        if (minimumX > maximumX)
        {
            return TaskbarPlacementDecision.Hide();
        }

        int desiredY = CenteredLocalY(taskbarHeight, desiredHeight);
        double percent = double.IsFinite(positionPercent)
            ? Math.Clamp(positionPercent, 0, 100)
            : 100;
        int configuredX = minimumX + (int)Math.Round(
            (maximumX - minimumX) * percent / 100d,
            MidpointRounding.AwayFromZero);
        if (!explicitLayoutChange && current is { } existing)
        {
            int clampedX = Math.Clamp(existing.X, minimumX, maximumX);
            bool edgeAnchored = percent <= 0 || percent >= 100;
            int resolvedX = edgeAnchored &&
                Math.Abs((long)configuredX - existing.X) > 2
                    ? configuredX
                    : clampedX;
            bool sameGeometry = existing.X == resolvedX &&
                existing.Y == desiredY &&
                existing.Width == desiredWidth &&
                existing.Height == desiredHeight;
            if (sameGeometry)
            {
                return new TaskbarPlacementDecision(false, false, existing);
            }

            // Preserve the physical X whenever possible. If a boundary moved
            // across the Band, move only as far as needed to become safe.
            return new TaskbarPlacementDecision(
                false,
                true,
                new TaskbarBandRect(resolvedX, desiredY, desiredWidth, desiredHeight));
        }

        return new TaskbarPlacementDecision(
            false,
            true,
            new TaskbarBandRect(
                Math.Clamp(configuredX, minimumX, maximumX),
                desiredY,
                desiredWidth,
                desiredHeight));
    }
}
