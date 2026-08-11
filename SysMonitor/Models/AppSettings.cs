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

    public bool PanelTopmost { get; set; }

    public double? PanelLeft { get; set; }

    public double? PanelTop { get; set; }
}
