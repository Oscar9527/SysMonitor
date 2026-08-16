using System.IO;
using System.Text.Json;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using SysMonitor.Models;

namespace SysMonitor.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SettingsService(string? settingsDirectory = null)
    {
        SettingsDirectory = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysMonitor");
        SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    }

    public string SettingsDirectory { get; }

    public string SettingsPath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(SettingsPath);
            AppSettings settings =
                JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            Normalize(settings);
            return settings;
        }
        catch
        {
            // A damaged or temporarily unavailable preferences file must never prevent startup.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        _ = TrySave(settings);
    }

    public bool TrySave(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize(settings);

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            temporaryPath = Path.Combine(
                SettingsDirectory,
                $".settings.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, true);
            temporaryPath = null;
            return true;
        }
        catch
        {
            // Preferences are best-effort; monitoring should continue if persistence fails.
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }

    internal static void Normalize(AppSettings settings)
    {
        settings.UiCulture = LocalizationService.NormalizeCulturePreference(settings.UiCulture);
        settings.ActiveThemeId = string.IsNullOrWhiteSpace(settings.ActiveThemeId)
            ? AppSettings.DefaultThemeId
            : settings.ActiveThemeId.Trim();
        settings.BandFontFamily = string.IsNullOrWhiteSpace(settings.BandFontFamily)
            ? "Segoe UI Variable Text"
            : settings.BandFontFamily.Trim();
        settings.BandFontSize = double.IsFinite(settings.BandFontSize)
            ? Math.Clamp(Math.Round(settings.BandFontSize), 9, 20)
            : 13;
        settings.BandItemSpacingDip = double.IsFinite(settings.BandItemSpacingDip)
            ? Math.Clamp(
                Math.Round(settings.BandItemSpacingDip, MidpointRounding.AwayFromZero),
                0,
                18)
            : 10;
        settings.BandHorizontalOffsetDip = double.IsFinite(settings.BandHorizontalOffsetDip)
            ? Math.Clamp(
                Math.Round(settings.BandHorizontalOffsetDip, MidpointRounding.AwayFromZero),
                -100,
                100)
            : 0;
        settings.BandHorizontalPositionPercent =
            settings.BandHorizontalPositionPercent is double position &&
            double.IsFinite(position)
                ? Math.Clamp(
                    Math.Round(position, 2, MidpointRounding.AwayFromZero),
                    0,
                    100)
                : null;
        settings.GameOverlayHorizontalPositionPercent = double.IsFinite(settings.GameOverlayHorizontalPositionPercent)
            ? Math.Clamp(
                Math.Round(settings.GameOverlayHorizontalPositionPercent, 2, MidpointRounding.AwayFromZero),
                0,
                100)
            : 50d;
        settings.GameOverlayPreset = settings.GameOverlayPreset?.Trim().ToLowerInvariant() switch
        {
            "compact" or "detailed" or "rivatuner" => settings.GameOverlayPreset.Trim().ToLowerInvariant(),
            _ => "rivatuner"
        };
        settings.GameOverlaySampling = settings.GameOverlaySampling?.Trim().ToLowerInvariant() switch
        {
            "low" or "standard" or "high" => settings.GameOverlaySampling.Trim().ToLowerInvariant(),
            _ => "standard"
        };
        GameOverlayMetricVisibility overlayMetrics =
            (settings.GameOverlayMetrics ?? new GameOverlayMetricVisibilitySettings()).ToEffective();
        settings.GameOverlayMetrics = GameOverlayMetricVisibilitySettings.FromEffective(overlayMetrics);
        GameOverlayAppearance appearance = NormalizeOverlayAppearance(
            (settings.GameOverlayAppearance ?? new GameOverlayAppearanceSettings()).ToEffective());
        settings.GameOverlayAppearance = GameOverlayAppearanceSettings.FromEffective(appearance);
        BandMetricVisibility effective =
            (settings.BandMetricVisibility ?? new BandMetricVisibilitySettings()).ToEffective();
        settings.BandMetricVisibility = BandMetricVisibilitySettings.FromEffective(effective);
    }

    internal static GameOverlayAppearance NormalizeOverlayAppearance(GameOverlayAppearance value) => new(
        string.IsNullOrWhiteSpace(value.FontFamily) ? "Consolas" : value.FontFamily.Trim(),
        double.IsFinite(value.FontSize) ? Math.Clamp(Math.Round(value.FontSize), 10, 28) : 13d,
        NormalizeColor(value.LabelColor, "#FF66D9FF"),
        NormalizeColor(value.ValueColor, "#FFFFFFFF"),
        NormalizeColor(value.OutlineColor, "#FF000000"),
        double.IsFinite(value.OutlineThickness) ? Math.Clamp(value.OutlineThickness, 0, 4) : 1d,
        NormalizeColor(value.ShadowColor, "#CC000000"),
        double.IsFinite(value.ShadowOpacity) ? Math.Clamp(value.ShadowOpacity, 0, 1) : 0.85d,
        double.IsFinite(value.ShadowDepth) ? Math.Clamp(value.ShadowDepth, 0, 8) : 1d,
        NormalizeColor(value.GpuColor, "#FF66D9FF"),
        NormalizeColor(value.CpuColor, "#FF8BE9FD"),
        NormalizeColor(value.FpsColor, "#FF50FA7B"),
        NormalizeColor(value.MemoryColor, "#FFF1FA8C"),
        NormalizeColor(value.NetworkColor, "#FFFFB86C"));

    private static string NormalizeColor(string? value, string fallback)
    {
        try
        {
            MediaColor color = (MediaColor)MediaColorConverter.ConvertFromString(value ?? fallback)!;
            return color.ToString();
        }
        catch
        {
            return fallback;
        }
    }
}
