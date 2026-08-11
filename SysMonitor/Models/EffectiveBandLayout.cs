namespace SysMonitor.Models;

public enum BandMetric
{
    Cpu,
    Memory,
    Gpu,
    Download,
    Upload,
    SystemDisk
}

public sealed record EffectiveBandLayout(
    int ActiveMask,
    bool Compact,
    bool Wide,
    double ItemSpacingDip,
    double TargetWidthDip)
{
    private static readonly BandMetric[] OrderedMetrics = Enum.GetValues<BandMetric>();
    private static readonly IReadOnlyDictionary<BandMetric, double> CompactSlotWidths =
        new Dictionary<BandMetric, double>
        {
            [BandMetric.Cpu] = 50,
            [BandMetric.Memory] = 50,
            [BandMetric.Gpu] = 58,
            [BandMetric.Download] = 68,
            [BandMetric.Upload] = 68,
            [BandMetric.SystemDisk] = 50
        };

    private static readonly IReadOnlyDictionary<BandMetric, double> NormalSlotWidths =
        new Dictionary<BandMetric, double>
        {
            [BandMetric.Cpu] = 62,
            [BandMetric.Memory] = 54,
            [BandMetric.Gpu] = 62,
            [BandMetric.Download] = 72,
            [BandMetric.Upload] = 72,
            [BandMetric.SystemDisk] = 54
        };

    private static readonly IReadOnlyDictionary<BandMetric, double> WideSlotWidths =
        new Dictionary<BandMetric, double>
        {
            [BandMetric.Cpu] = 68,
            [BandMetric.Memory] = 58,
            [BandMetric.Gpu] = 68,
            [BandMetric.Download] = 78,
            [BandMetric.Upload] = 78,
            [BandMetric.SystemDisk] = 58
        };

    public IReadOnlyList<BandMetric> ActiveGroups =>
        OrderedMetrics.Where(IsVisible).ToArray();

    public int ActiveGroupCount => OrderedMetrics.Count(IsVisible);

    public int SeparatorCount => Math.Max(0, ActiveGroupCount - 1);

    public bool HasVisibleGroups => ActiveMask != 0;

    public double SlotWidth(BandMetric metric) =>
        (Compact ? CompactSlotWidths : Wide ? WideSlotWidths : NormalSlotWidths)[metric];

    public bool IsVisible(BandMetric metric) =>
        (ActiveMask & (1 << (int)metric)) != 0;

    public static EffectiveBandLayout Create(
        BandMetricVisibility visibility,
        bool compact,
        bool wide,
        bool gpuCapable,
        double itemSpacingDip)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        double spacing = double.IsFinite(itemSpacingDip)
            ? Math.Clamp(Math.Round(itemSpacingDip, MidpointRounding.AwayFromZero), 0, 18)
            : 10;
        var active = new List<BandMetric>(6);
        if (visibility.Cpu) active.Add(BandMetric.Cpu);
        if (visibility.Memory) active.Add(BandMetric.Memory);
        if (visibility.Gpu && gpuCapable) active.Add(BandMetric.Gpu);
        if (visibility.Download) active.Add(BandMetric.Download);
        if (visibility.Upload) active.Add(BandMetric.Upload);
        if (visibility.SystemDisk && !compact) active.Add(BandMetric.SystemDisk);

        IReadOnlyDictionary<BandMetric, double> widths =
            compact ? CompactSlotWidths : wide ? WideSlotWidths : NormalSlotWidths;
        double targetWidth = active.Sum(metric => widths[metric] + spacing) +
            Math.Max(0, active.Count - 1);
        int mask = active.Aggregate(0, (value, metric) => value | (1 << (int)metric));
        return new EffectiveBandLayout(
            mask,
            compact,
            wide,
            spacing,
            targetWidth);
    }
}

public sealed class GpuCapabilityStabilizer
{
    private const int ShowThreshold = 2;
    private const int HideThreshold = 5;
    private int _presentCount;
    private int _missingCount;

    public bool IsCapable { get; private set; }

    public bool Observe(bool gpuPresent)
    {
        bool prior = IsCapable;
        if (gpuPresent)
        {
            _missingCount = 0;
            _presentCount = Math.Min(ShowThreshold, _presentCount + 1);
            if (!IsCapable && _presentCount >= ShowThreshold)
            {
                IsCapable = true;
            }
        }
        else
        {
            _presentCount = 0;
            _missingCount = Math.Min(HideThreshold, _missingCount + 1);
            if (IsCapable && _missingCount >= HideThreshold)
            {
                IsCapable = false;
            }
        }

        return prior != IsCapable;
    }
}
