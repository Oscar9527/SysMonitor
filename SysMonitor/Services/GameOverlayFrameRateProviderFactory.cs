namespace SysMonitor.Services;

/// <summary>
/// Keeps the production FPS source policy explicit and directly testable.
/// SysMonitor reads an existing RTSS mapping first and starts its own
/// PresentMon collector only when RTSS has no usable sample.
/// </summary>
internal static class GameOverlayFrameRateProviderFactory
{
    internal static IFrameRateProvider Create() => new AdaptiveFrameRateProvider();
}
