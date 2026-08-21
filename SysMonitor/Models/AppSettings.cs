namespace SysMonitor.Models;

public sealed class AppSettings
{
    public const string SystemThemeId = "system";
    public const string DefaultThemeId = SystemThemeId;
    public const string BuiltInDefaultThemeId = "builtin.default";

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

    public string BandFontFamily { get; set; } = "Microsoft YaHei UI";

    public double BandFontSize { get; set; } = 13d;

    public double BandItemSpacingDip { get; set; } = 10d;

    public double BandHorizontalOffsetDip { get; set; } = 0d;

    public double? BandHorizontalPositionPercent { get; set; }

    public BandMetricVisibilitySettings? BandMetricVisibility { get; set; } = new();

    public string ActiveThemeId { get; set; } = SystemThemeId;

    public bool PanelTopmost { get; set; }

    public double? PanelLeft { get; set; }

    public double? PanelTop { get; set; }

    public double? PanelWidth { get; set; }

    public double? PanelHeight { get; set; }
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
    public bool? CpuPower { get; set; } = false;
    public bool? GpuPower { get; set; } = false;
    public bool? CpuFrequency { get; set; } = true;
    public bool? GpuClock { get; set; } = true;
    public bool? GpuMemoryClock { get; set; } = false;
    public bool? GpuMemory { get; set; } = true;
    public bool? MemoryFrequency { get; set; } = true;
    public bool? CpuTemperature { get; set; } = true;
    public bool? GpuTemperature { get; set; } = true;
    public List<string>? Order { get; set; }
    public List<string>? CpuItemOrder { get; set; }
    public List<string>? GpuItemOrder { get; set; }
    public List<string>? MemoryItemOrder { get; set; }
    public List<string>? NetworkItemOrder { get; set; }

    public GameOverlayMetricVisibility ToEffective() => new(
        FrameRate ?? true,
        Cpu ?? true,
        Gpu ?? true,
        Memory ?? true,
        Network ?? false,
        CpuPower ?? false,
        GpuPower ?? false,
        CpuFrequency ?? true,
        GpuClock ?? true,
        GpuMemory ?? true,
        MemoryFrequency ?? true,
        CpuTemperature ?? true,
        GpuTemperature ?? true,
        GpuMemoryClock ?? false)
        {
            Order = GameOverlayMetricOrder.Normalize(Order),
            CpuItemOrder = SubItemOrderHelper.Normalize(CpuItemOrder, SubItemDefaults.Cpu),
            GpuItemOrder = SubItemOrderHelper.Normalize(GpuItemOrder, SubItemDefaults.Gpu),
            MemoryItemOrder = SubItemOrderHelper.Normalize(MemoryItemOrder, SubItemDefaults.Memory),
            NetworkItemOrder = SubItemOrderHelper.Normalize(NetworkItemOrder, SubItemDefaults.Network)
        };

    public static GameOverlayMetricVisibilitySettings FromEffective(GameOverlayMetricVisibility value) => new()
    {
        FrameRate = value.FrameRate,
        Cpu = value.Cpu,
        Gpu = value.Gpu,
        Memory = value.Memory,
        Network = value.Network,
        CpuPower = value.CpuPower,
        GpuPower = value.GpuPower,
        CpuFrequency = value.CpuFrequency,
        GpuClock = value.GpuClock,
        GpuMemoryClock = value.GpuMemoryClock,
        GpuMemory = value.GpuMemory,
        MemoryFrequency = value.MemoryFrequency,
        CpuTemperature = value.CpuTemperature,
        GpuTemperature = value.GpuTemperature,
        Order = value.Order.ToList(),
        CpuItemOrder = value.CpuItemOrder.ToList(),
        GpuItemOrder = value.GpuItemOrder.ToList(),
        MemoryItemOrder = value.MemoryItemOrder.ToList(),
        NetworkItemOrder = value.NetworkItemOrder.ToList()
    };
}

public sealed record GameOverlayMetricVisibility(
    bool FrameRate = true,
    bool Cpu = true,
    bool Gpu = true,
    bool Memory = true,
    bool Network = false,
    bool CpuPower = false,
    bool GpuPower = false,
    bool CpuFrequency = true,
    bool GpuClock = true,
    bool GpuMemory = true,
    bool MemoryFrequency = true,
    bool CpuTemperature = true,
    bool GpuTemperature = true,
    bool GpuMemoryClock = false)
{
    public IReadOnlyList<string> Order { get; init; } = GameOverlayMetricOrder.Default;
    public IReadOnlyList<string> CpuItemOrder { get; init; } = SubItemDefaults.Cpu;
    public IReadOnlyList<string> GpuItemOrder { get; init; } = SubItemDefaults.Gpu;
    public IReadOnlyList<string> MemoryItemOrder { get; init; } = SubItemDefaults.Memory;
    public IReadOnlyList<string> NetworkItemOrder { get; init; } = SubItemDefaults.Network;
}

public static class SubItemDefaults
{
    public static IReadOnlyList<string> Cpu { get; } = ["usage", "temp", "power", "freq"];
    public static IReadOnlyList<string> Gpu { get; } = ["usage", "temp", "power", "clock", "memClock", "memUsed"];
    public static IReadOnlyList<string> Memory { get; } = ["usage", "capacity", "freq"];
    public static IReadOnlyList<string> Network { get; } = ["download", "upload"];
    public static IReadOnlyList<string> Io { get; } = ["download", "upload", "disk"];
}

public static class SubItemOrderHelper
{
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? values, IReadOnlyList<string> defaults)
    {
        List<string> rawList = values?.Select(v => v?.Trim().ToLowerInvariant()).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList() ?? [];
        if (rawList.Count == 0) return defaults;
        var result = new List<string>();
        foreach (string id in rawList)
        {
            if (defaults.Contains(id, StringComparer.Ordinal) && !result.Contains(id, StringComparer.Ordinal))
            {
                result.Add(id);
            }
        }
        foreach (string id in defaults)
        {
            if (!result.Contains(id, StringComparer.Ordinal))
            {
                result.Add(id);
            }
        }
        return result.SequenceEqual(defaults, StringComparer.Ordinal) ? defaults : result;
    }
}

public static class GameOverlayMetricOrder
{
    private static readonly string[] s_legacyOrder1 = ["gpu", "cpu", "fps", "memory", "network"];
    private static readonly string[] s_legacyOrder2 = ["fps", "gpu", "cpu", "memory", "network"];

    public static IReadOnlyList<string> Default { get; } = ["cpu", "gpu", "memory", "fps", "network"];

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? values)
    {
        List<string> rawList = values?.Select(v => v?.Trim().ToLowerInvariant()).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList() ?? [];
        if (rawList.Count == 0 ||
            rawList.SequenceEqual(s_legacyOrder1, StringComparer.Ordinal) ||
            rawList.SequenceEqual(s_legacyOrder2, StringComparer.Ordinal))
        {
            return Default;
        }

        var result = new List<string>();
        foreach (string id in rawList)
        {
            if (Default.Contains(id, StringComparer.Ordinal) && !result.Contains(id, StringComparer.Ordinal))
            {
                result.Add(id);
            }
        }
        foreach (string id in Default)
        {
            if (!result.Contains(id, StringComparer.Ordinal))
            {
                result.Add(id);
            }
        }
        return result.SequenceEqual(Default, StringComparer.Ordinal) ? Default : result;
    }
}

public sealed class GameOverlayAppearanceSettings
{
    public string FontFamily { get; set; } = "Consolas";
    public double FontSize { get; set; } = 16d;
    public string LabelColor { get; set; } = "#FFFF8C00";
    public string ValueColor { get; set; } = "#FFFF8C00";
    public string OutlineColor { get; set; } = "#FF000000";
    public double OutlineThickness { get; set; } = 1.5d;
    public string ShadowColor { get; set; } = "#CC000000";
    public double ShadowOpacity { get; set; } = 0.95d;
    public double ShadowDepth { get; set; } = 1d;
    public string GpuColor { get; set; } = "#FFFF8C00";
    public string CpuColor { get; set; } = "#FF00E5FF";
    public string FpsColor { get; set; } = "#FF00E676";
    public string MemoryColor { get; set; } = "#FFFFD600";
    public string NetworkColor { get; set; } = "#FFE040FB";

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
        NetworkColor);

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
        NetworkColor = value.NetworkColor
    };
}

public sealed record GameOverlayAppearance(
    string FontFamily = "Consolas",
    double FontSize = 16d,
    string LabelColor = "#FFFF8C00",
    string ValueColor = "#FFFF8C00",
    string OutlineColor = "#FF000000",
    double OutlineThickness = 1.5d,
    string ShadowColor = "#CC000000",
    double ShadowOpacity = 0.95d,
    double ShadowDepth = 1d,
    string GpuColor = "#FFFF8C00",
    string CpuColor = "#FF00E5FF",
    string FpsColor = "#FF00E676",
    string MemoryColor = "#FFFFD600",
    string NetworkColor = "#FFE040FB");

public sealed class BandMetricVisibilitySettings
{
    public bool? Cpu { get; set; } = true;
    public bool? CpuUsage { get; set; } = true;
    public bool? CpuTemperature { get; set; } = true;
    public bool? CpuPower { get; set; } = false;

    public bool? Memory { get; set; } = true;
    public bool? MemoryUsage { get; set; } = true;
    public bool? MemoryUsedCapacity { get; set; } = false;

    public bool? Gpu { get; set; } = true;
    public bool? GpuUsage { get; set; } = true;
    public bool? GpuTemperature { get; set; } = true;
    public bool? GpuPower { get; set; } = false;

    public bool? Download { get; set; } = true;

    public bool? Upload { get; set; } = true;

    public bool? SystemDisk { get; set; } = false;

    public BandMetricVisibility ToEffective() =>
        new(
            Cpu ?? true,
            Memory ?? true,
            Gpu ?? true,
            Download ?? true,
            Upload ?? true,
            SystemDisk ?? false,
            CpuUsage ?? true,
            CpuTemperature ?? true,
            CpuPower ?? false,
            MemoryUsage ?? true,
            MemoryUsedCapacity ?? false,
            GpuUsage ?? true,
            GpuTemperature ?? true,
            GpuPower ?? false);

    public static BandMetricVisibilitySettings FromEffective(BandMetricVisibility value) =>
        new()
        {
            Cpu = value.Cpu,
            CpuUsage = value.CpuUsage,
            CpuTemperature = value.CpuTemperature,
            CpuPower = value.CpuPower,
            Memory = value.Memory,
            MemoryUsage = value.MemoryUsage,
            MemoryUsedCapacity = value.MemoryUsedCapacity,
            Gpu = value.Gpu,
            GpuUsage = value.GpuUsage,
            GpuTemperature = value.GpuTemperature,
            GpuPower = value.GpuPower,
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
    bool SystemDisk = false,
    bool CpuUsage = true,
    bool CpuTemperature = true,
    bool CpuPower = false,
    bool MemoryUsage = true,
    bool MemoryUsedCapacity = false,
    bool GpuUsage = true,
    bool GpuTemperature = true,
    bool GpuPower = false)
{
    public static readonly BandMetricVisibility All = new(
        Cpu: true,
        Memory: true,
        Gpu: true,
        Download: true,
        Upload: true,
        SystemDisk: false,
        CpuUsage: true,
        CpuTemperature: true,
        CpuPower: false,
        MemoryUsage: true,
        MemoryUsedCapacity: false,
        GpuUsage: true,
        GpuTemperature: true,
        GpuPower: false);
}
