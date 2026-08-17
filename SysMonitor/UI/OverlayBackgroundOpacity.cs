namespace SysMonitor.UI;

internal static class OverlayBackgroundOpacity
{
    internal const double Default = 0.8d;

    internal static byte ToAlpha(double opacity)
    {
        double normalized = double.IsFinite(opacity)
            ? Math.Clamp(opacity, 0d, 1d)
            : Default;
        return (byte)Math.Round(normalized * byte.MaxValue, MidpointRounding.AwayFromZero);
    }
}
