using System.Diagnostics;

namespace SysMonitor.Services;

public sealed class ForegroundTargetTracker
{
    public static readonly TimeSpan RecentTargetLifetime = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StabilityInterval = TimeSpan.FromMilliseconds(250);
    public const int RequiredStableSamples = 3;

    private readonly IForegroundWindowSource _source;
    private readonly int _currentProcessId;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private ForegroundTarget? _lastQualified;

    public ForegroundTargetTracker(
        IForegroundWindowSource source,
        int? currentProcessId = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _currentProcessId = currentProcessId ?? Environment.ProcessId;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public event EventHandler? StateChanged;

    public ForegroundTargetState State { get; private set; }

    public ForegroundTarget? LastQualified => _lastQualified;

    public ForegroundTarget? SnapshotTriggerCandidate() => CaptureQualified(record: false);

    public ForegroundTarget? TryGetRecentTarget()
    {
        ForegroundTarget? target = _lastQualified;
        if (target is null ||
            _clock() - target.QualifiedAt > RecentTargetLifetime ||
            !_source.IsCurrentIdentity(target))
        {
            return null;
        }

        return target;
    }

    public async Task<ForegroundTarget?> StabilizeTriggerCandidateAsync(
        ForegroundTarget? triggerBefore,
        CancellationToken cancellationToken)
    {
        if (triggerBefore is null)
        {
            return null;
        }

        ForegroundTarget stable = triggerBefore;
        for (int sample = 0; sample < RequiredStableSamples; sample++)
        {
            if (sample > 0)
            {
                await _delay(StabilityInterval, cancellationToken).ConfigureAwait(false);
            }

            ForegroundTarget? current = CaptureQualified(record: false);
            if (!stable.SameIdentity(current))
            {
                return null;
            }
        }

        return Record(stable);
    }

    public async Task<ForegroundTarget> WaitForTargetAsync(CancellationToken cancellationToken)
    {
        SetState(ForegroundTargetState.WaitingForTarget);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ForegroundTarget? first = CaptureQualified(record: false);
                ForegroundTarget? stable = await StabilizeTriggerCandidateAsync(
                    first,
                    cancellationToken).ConfigureAwait(false);
                if (stable is not null)
                {
                    SetState(ForegroundTargetState.Ready);
                    return stable;
                }

                await _delay(StabilityInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            SetState(ForegroundTargetState.Idle);
            throw;
        }
    }

    public void MarkTargetExited()
    {
        _lastQualified = null;
        SetState(ForegroundTargetState.WaitingForTarget);
    }

    public void ResetState() => SetState(ForegroundTargetState.Idle);

    private ForegroundTarget? CaptureQualified(bool record)
    {
        ForegroundWindowCandidate? candidate = _source.Capture();
        if (!ForegroundTargetPolicy.IsQualified(candidate, _currentProcessId))
        {
            return null;
        }

        var target = new ForegroundTarget(
            candidate!.WindowHandle,
            candidate.ProcessId,
            candidate.ProcessStartedAt,
            _clock());
        return record ? Record(target) : target;
    }

    private ForegroundTarget Record(ForegroundTarget target)
    {
        _lastQualified = target with { QualifiedAt = _clock() };
        SetState(ForegroundTargetState.Ready);
        return _lastQualified;
    }

    private void SetState(ForegroundTargetState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
