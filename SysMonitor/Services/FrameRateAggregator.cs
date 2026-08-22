namespace SysMonitor.Services;

internal sealed class FrameRateAggregator
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CurrentChainFreshness = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ReceiveFreshness = TimeSpan.FromSeconds(2);
    private const int MaximumChains = 256;
    private const double ChallengerRatio = 1.25d;
    private readonly Dictionary<ulong, ChainState> _chains = new();
    private ulong? _currentChain;
    private ulong? _challengerChain;
    private long _challengerVersion;
    private int _challengerWindows;
    private DateTimeOffset? _lastReceivedAt;

    internal bool HasReceivedFrames => _lastReceivedAt is not null;
    internal int ChainCount => _chains.Count;

    internal bool Add(PresentMonFrame frame, DateTimeOffset receivedAt)
    {
        PruneChains(receivedAt);

        if (!_chains.TryGetValue(frame.SwapChainAddress, out ChainState? chain))
        {
            chain = new ChainState();
            _chains.Add(frame.SwapChainAddress, chain);
        }

        if (chain.LastPresentTime is { } lastTime && frame.TimeInSeconds <= lastTime)
        {
            return false;
        }

        chain.LastPresentTime = frame.TimeInSeconds;
        chain.LastReceivedAt = receivedAt;
        chain.WindowVersion++;
        _lastReceivedAt = receivedAt;
        if (frame.MillisecondsBetweenPresents > 0d)
        {
            chain.Intervals.Enqueue(new Interval(
                frame.TimeInSeconds,
                frame.MillisecondsBetweenPresents));
        }

        Trim(chain, frame.TimeInSeconds);
        EnforceChainLimit(frame.SwapChainAddress);
        return true;
    }

    internal double? Read(DateTimeOffset now)
    {
        PruneChains(now);

        if (_lastReceivedAt is null || now - _lastReceivedAt.Value > ReceiveFreshness)
        {
            ResetSelection();
            return null;
        }

        Candidate[] allCandidates = _chains
            .Select(pair => new Candidate(pair.Key, pair.Value, CalculateFps(pair.Value)))
            .Where(candidate => candidate.Fps is not null)
            .OrderByDescending(candidate => candidate.Fps)
            .ThenBy(candidate => candidate.Address)
            .ToArray();
        Candidate[] candidates = allCandidates
            .Where(candidate => now - candidate.State.LastReceivedAt <= CurrentChainFreshness)
            .ToArray();
        if (candidates.Length == 0)
        {
            return _currentChain is { } staleCurrent
                ? allCandidates.FirstOrDefault(candidate => candidate.Address == staleCurrent).Fps
                : allCandidates.FirstOrDefault().Fps;
        }

        Candidate best = candidates[0];
        Candidate? current = null;
        if (_currentChain is { } currentAddress)
        {
            foreach (Candidate candidate in candidates)
            {
                if (candidate.Address == currentAddress)
                {
                    current = candidate;
                    break;
                }
            }
        }
        bool currentIsFresh = current is not null &&
                              now - current.Value.State.LastReceivedAt <= CurrentChainFreshness;
        if (!currentIsFresh)
        {
            SetCurrent(best.Address);
            return best.Fps;
        }

        if (best.Address == current!.Value.Address ||
            best.Fps!.Value < current.Value.Fps!.Value * ChallengerRatio)
        {
            ClearChallenger();
            return current.Value.Fps;
        }

        if (_challengerChain == best.Address)
        {
            if (_challengerVersion != best.State.WindowVersion)
            {
                _challengerWindows++;
                _challengerVersion = best.State.WindowVersion;
            }
        }
        else
        {
            _challengerChain = best.Address;
            _challengerWindows = 1;
            _challengerVersion = best.State.WindowVersion;
        }

        if (_challengerWindows >= 2)
        {
            SetCurrent(best.Address);
            return best.Fps;
        }

        return current.Value.Fps;
    }

    private static void Trim(ChainState chain, double latestTime)
    {
        double minimumTime = latestTime - WindowLength.TotalSeconds;
        while (chain.Intervals.TryPeek(out Interval interval) && interval.PresentTime < minimumTime)
        {
            chain.Intervals.Dequeue();
        }
    }

    private static double? CalculateFps(ChainState chain)
    {
        if (chain.Intervals.Count < 2)
        {
            return null;
        }

        double totalMilliseconds = chain.Intervals.Sum(interval => interval.Milliseconds);
        double fps = 1000d * chain.Intervals.Count / totalMilliseconds;
        return totalMilliseconds > 0d && double.IsFinite(fps) && fps >= 0d ? fps : null;
    }

    private void PruneChains(DateTimeOffset now)
    {
        foreach (ulong address in _chains
                     .Where(pair => now - pair.Value.LastReceivedAt > ReceiveFreshness)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            RemoveChain(address);
        }

        if (_currentChain is null)
        {
            ClearChallenger();
        }
    }

    private void EnforceChainLimit(ulong justAddedAddress)
    {
        while (_chains.Count > MaximumChains)
        {
            IEnumerable<KeyValuePair<ulong, ChainState>> candidates = _chains
                .Where(pair => pair.Key != justAddedAddress);

            // Prefer evicting an unselected chain, but never evict the chain that
            // was just updated. If every other chain is selected, the selected
            // chain is still eligible so the hard cap remains absolute.
            if (candidates.Any(pair => !IsSelected(pair.Key)))
            {
                candidates = candidates.Where(pair => !IsSelected(pair.Key));
            }

            KeyValuePair<ulong, ChainState> oldest = candidates
                .OrderBy(pair => pair.Value.LastReceivedAt)
                .ThenBy(pair => pair.Key)
                .First();
            RemoveChain(oldest.Key);
        }
    }

    private bool IsSelected(ulong address) =>
        _currentChain == address || _challengerChain == address;

    private void RemoveChain(ulong address)
    {
        if (!_chains.Remove(address))
        {
            return;
        }

        if (_currentChain == address)
        {
            _currentChain = null;
        }

        if (_challengerChain == address || _currentChain is null)
        {
            ClearChallenger();
        }
    }

    private void SetCurrent(ulong address)
    {
        _currentChain = address;
        ClearChallenger();
    }

    private void ResetSelection()
    {
        _currentChain = null;
        ClearChallenger();
    }

    private void ClearChallenger()
    {
        _challengerChain = null;
        _challengerWindows = 0;
        _challengerVersion = 0;
    }

    private sealed class ChainState
    {
        internal Queue<Interval> Intervals { get; } = new();
        internal double? LastPresentTime { get; set; }
        internal DateTimeOffset LastReceivedAt { get; set; }
        internal long WindowVersion { get; set; }
    }

    private readonly record struct Interval(double PresentTime, double Milliseconds);
    private readonly record struct Candidate(ulong Address, ChainState State, double? Fps);
}
