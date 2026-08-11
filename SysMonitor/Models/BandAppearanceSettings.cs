namespace SysMonitor.Models;

public sealed record BandAppearanceSettings(
    string FontFamily,
    double FontSize,
    double? HorizontalPositionPercent = 100,
    double ItemSpacingDip = 10,
    double LegacyHorizontalOffsetDip = 0,
    BandMetricVisibility? MetricVisibility = null)
{
    public BandMetricVisibility EffectiveMetricVisibility =>
        MetricVisibility ?? BandMetricVisibility.All;
}
