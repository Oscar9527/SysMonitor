namespace SysMonitor.Models;

public sealed class AppSettings
{
    public const string DefaultThemeId = "builtin.default";

    public string UiCulture { get; set; } = "system";

    public bool BandVisible { get; set; } = true;

    /// <summary>
    /// Keeps compatibility hardware sensors disabled. Older settings files do not contain this
    /// property, so the initializer intentionally migrates them to the safe default.
    /// </summary>
    public bool GameSafeMode { get; set; } = true;

    /// <summary>Horizontal placement of the game overlay on the target monitor.</summary>
    public double GameOverlayHorizontalPositionPercent { get; set; } = 50d;

    public string GameOverlayPreset { get; set; } = "rivatuner";

    /// <summary>HUD flow direction: vertical or horizontal.</summary>
    public string GameOverlayLayoutMode { get; set; } = "vertical";

    /// <summary>Exact physical-pixel coordinates remembered independently per monitor.</summary>
    public List<GameOverlayMonitorPositionSettings>? GameOverlayMonitorPositions { get; set; } = [];

    public GameOverlayMetricVisibilitySettings? GameOverlayMetrics { get; set; } = new();

    /// <summary>Visual settings for the independent game HUD.</summary>
    public GameOverlayAppearanceSettings? GameOverlayAppearance { get; set; } = new();

    /// <summary>HUD sampling cadence: low, standard, or high.</summary>
    public string GameOverlaySampling { get; set; } = "standard";

    public string BandFontFamily { get; set; } = "Segoe UI Variable Text";

    public double BandFontSize { get; set; } = 13d;

    public double BandItemSpacingDip { get; set; } = 10d;

    public double BandHorizontalOffsetDip { get; set; } = 0d;

    public double? BandHorizontalPositionPercent { get; set; }

    public BandMetricVisibilitySettings? BandMetricVisibility { get; set; } = new();

    public string ActiveThemeId { get; set; } = DefaultThemeId;

    public bool PanelTopmost { get; set; }

    public double? PanelLeft { get; set; }

    public double? PanelTop { get; set; }
}

public sealed class GameOverlayMonitorPositionSettings
{
    public string StableMonitorId { get; set; } = string.Empty;
    public string GdiDeviceName { get; set; } = string.Empty;
    public bool IsFallbackIdentity { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class GameOverlayMetricVisibilitySettings
{
    public bool? FrameRate { get; set; } = true;
    public bool? Cpu { get; set; } = true;
    public bool? Gpu { get; set; } = true;
    public bool? Memory { get; set; } = true;
    public bool? Network { get; set; }
    public List<string>? Order { get; set; }

    public GameOverlayMetricVisibility ToEffective() => new(
        FrameRate ?? true,
        Cpu ?? true,
        Gpu ?? true,
        Memory ?? true,
        Network ?? false)
        { Order = GameOverlayMetricOrder.Normalize(Order) };

    public static GameOverlayMetricVisibilitySettings FromEffective(GameOverlayMetricVisibility value) => new()
    {
        FrameRate = value.FrameRate,
        Cpu = value.Cpu,
        Gpu = value.Gpu,
        Memory = value.Memory,
        Network = value.Network,
        Order = value.Order.ToList()
    };
}

public sealed record GameOverlayMetricVisibility(
    bool FrameRate = true,
    bool Cpu = true,
    bool Gpu = true,
    bool Memory = true,
    bool Network = false)
{
    public IReadOnlyList<string> Order { get; init; } = GameOverlayMetricOrder.Default;
}

public static class GameOverlayMetricOrder
{
    public static IReadOnlyList<string> Default { get; } = new[] { "gpu", "cpu", "fps", "memory", "network" };

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? values)
    {
        var result = new List<string>();
        foreach (string id in values ?? Array.Empty<string>())
        {
            string normalized = id.Trim().ToLowerInvariant();
            if (Default.Contains(normalized, StringComparer.Ordinal) && !result.Contains(normalized, StringComparer.Ordinal))
                result.Add(normalized);
        }
        foreach (string id in Default)
            if (!result.Contains(id, StringComparer.Ordinal)) result.Add(id);
        // Reuse the canonical default instance. This keeps settings written by
        // older versions behaviorally and structurally identical after load.
        return result.SequenceEqual(Default, StringComparer.Ordinal) ? Default : result;
    }
}

public sealed class GameOverlayAppearanceSettings
{
    public string FontFamily { get; set; } = "Segoe UI Variable Text";
    public double FontSize { get; set; } = 13d;
    public string LabelColor { get; set; } = "#FFC2C7D0";
    public string ValueColor { get; set; } = "#FFF4F5F7";
    public string OutlineColor { get; set; } = "#FF000000";
    public double OutlineThickness { get; set; } = 0d;
    public string ShadowColor { get; set; } = "#CC000000";
    public double ShadowOpacity { get; set; } = 0d;
    public double ShadowDepth { get; set; } = 0d;
    public string GpuColor { get; set; } = "#FF7E57C2";
    public string CpuColor { get; set; } = "#FF1976D2";
    public string FpsColor { get; set; } = "#FF1B9A5A";
    public string MemoryColor { get; set; } = "#FFD97706";
    public string NetworkColor { get; set; } = "#FF0097A7";
    public double BackgroundOpacity { get; set; } = 0.8d;

    public GameOverlayAppearance ToEffective() => new(
        FontFamily,
        FontSize,
        LabelColor,
        ValueColor,
        OutlineColor,
        OutlineThickness,
        ShadowColor,
        ShadowOpacity,
        ShadowDepth,
        GpuColor,
        CpuColor,
        FpsColor,
        MemoryColor,
        NetworkColor,
        BackgroundOpacity);

    public static GameOverlayAppearanceSettings FromEffective(GameOverlayAppearance value) => new()
    {
        FontFamily = value.FontFamily,
        FontSize = value.FontSize,
        LabelColor = value.LabelColor,
        ValueColor = value.ValueColor,
        OutlineColor = value.OutlineColor,
        OutlineThickness = value.OutlineThickness,
        ShadowColor = value.ShadowColor,
        ShadowOpacity = value.ShadowOpacity,
        ShadowDepth = value.ShadowDepth,
        GpuColor = value.GpuColor,
        CpuColor = value.CpuColor,
        FpsColor = value.FpsColor,
        MemoryColor = value.MemoryColor,
        NetworkColor = value.NetworkColor,
        BackgroundOpacity = value.BackgroundOpacity
    };
}

public sealed record GameOverlayAppearance(
    string FontFamily = "Segoe UI Variable Text",
    double FontSize = 13d,
    string LabelColor = "#FFC2C7D0",
    string ValueColor = "#FFF4F5F7",
    string OutlineColor = "#FF000000",
    double OutlineThickness = 0d,
    string ShadowColor = "#CC000000",
    double ShadowOpacity = 0d,
    double ShadowDepth = 0d,
    string GpuColor = "#FF7E57C2",
    string CpuColor = "#FF1976D2",
    string FpsColor = "#FF1B9A5A",
    string MemoryColor = "#FFD97706",
    string NetworkColor = "#FF0097A7",
    double BackgroundOpacity = 0.8d);

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
