using SysMonitor.Models;

namespace SysMonitor.Services;

/// <summary>
/// Keeps the overlay UI independent from the bundled PresentMon process and
/// translates provider-specific states into the small UI contract.
/// </summary>
internal sealed class GameOverlayFrameProviderAdapter :
    IGameOverlayFrameProvider,
    IAsyncDisposable
{
    private readonly IFrameRateProvider _provider;
    private GameOverlayFrameSnapshot _latest;
    private bool _disposed;

    internal GameOverlayFrameProviderAdapter(IFrameRateProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _latest = Map(provider.Latest);
        _provider.SnapshotUpdated += OnSnapshotUpdated;
    }

    public GameOverlayFrameSnapshot Latest => Volatile.Read(ref _latest);

    public event EventHandler<GameOverlayFrameSnapshot>? SnapshotUpdated;

    public Task StartAsync(int processId, CancellationToken cancellationToken) =>
        _provider.StartAsync(processId, cancellationToken);

    public Task StopAsync() => _provider.StopAsync();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _provider.SnapshotUpdated -= OnSnapshotUpdated;
        await _provider.DisposeAsync().ConfigureAwait(false);
    }

    private void OnSnapshotUpdated(object? sender, FrameRateSnapshot snapshot)
    {
        GameOverlayFrameSnapshot mapped = Map(snapshot);
        Volatile.Write(ref _latest, mapped);
        SnapshotUpdated?.Invoke(this, mapped);
    }

    private static GameOverlayFrameSnapshot Map(FrameRateSnapshot snapshot) =>
        new(
            snapshot.Status == FrameRateStatus.Active ? snapshot.PresentFps : null,
            snapshot.Status switch
            {
                FrameRateStatus.Active => GameOverlayFrameStatus.Active,
                FrameRateStatus.NoTarget => GameOverlayFrameStatus.WaitingForTarget,
                FrameRateStatus.Starting or
                FrameRateStatus.WaitingForFrames or
                FrameRateStatus.Stopping => GameOverlayFrameStatus.Starting,
                FrameRateStatus.Disabled => GameOverlayFrameStatus.Unavailable,
                _ => GameOverlayFrameStatus.Faulted,
            },
            snapshot.SampledAt);
}
