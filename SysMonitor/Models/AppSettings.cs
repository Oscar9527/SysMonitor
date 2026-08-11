namespace SysMonitor.Models;

public sealed class AppSettings
{
    public string UiCulture { get; set; } = "system";

    public bool BandVisible { get; set; } = true;

    public string BandFontFamily { get; set; } = "Segoe UI Variable Text";

    public double BandFontSize { get; set; } = 13d;

    public double BandItemSpacingDip { get; set; } = 10d;

    public double BandHorizontalOffsetDip { get; set; } = 0d;

    public double? BandHorizontalPositionPercent { get; set; }

    public BandMetricVisibilitySettings? BandMetricVisibility { get; set; } = new();

    public bool PanelTopmost { get; set; }

    public double? PanelLeft { get; set; }

    public double? PanelTop { get; set; }
}

public sealed class BandMetricVisibilitySettings
{
    public bool? Cpu { get; set; } = true;

    public bool? Memory { get; set; } = true;

    public bool? Gpu { get; set; } = true;

    public bool? Download { get; set; } = true;

    public bool? Upload { get; set; } = true;

    public bool? SystemDisk { get; set; } = true;

    public BandMetricVisibility ToEffective() =>
        new(
            Cpu ?? true,
            Memory ?? true,
            Gpu ?? true,
            Download ?? true,
            Upload ?? true,
            SystemDisk ?? true);

    public static BandMetricVisibilitySettings FromEffective(BandMetricVisibility value) =>
        new()
        {
            Cpu = value.Cpu,
            Memory = value.Memory,
            Gpu = value.Gpu,
            Download = value.Download,
            Upload = value.Upload,
            SystemDisk = value.SystemDisk
        };
}

public sealed record BandMetricVisibility(
    bool Cpu = true,
    bool Memory = true,
    bool Gpu = true,
    bool Download = true,
    bool Upload = true,
    bool SystemDisk = true)
{
    public static BandMetricVisibility All { get; } = new();
}
