using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SysMonitor.Models;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using WpfApplication = System.Windows.Application;

namespace SysMonitor.Services;

public sealed class ThemeResourceApplier
{
    public string? AppliedIdentityToken { get; private set; }

    public bool Apply(ResolvedTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        WpfApplication application = WpfApplication.Current ??
            throw new InvalidOperationException("The WPF application is not available.");
        application.Dispatcher.VerifyAccess();
        if (string.Equals(
                AppliedIdentityToken,
                theme.IdentityToken,
                StringComparison.Ordinal))
        {
            return false;
        }

        ThemeDefinition definition = theme.Definition;
        ThemePalette colors = definition.Colors;
        ThemeMetricPalette metrics = definition.Metrics;
        bool isDefault = string.Equals(
            theme.Identity.Id,
            AppSettings.DefaultThemeId,
            StringComparison.OrdinalIgnoreCase);
        ResourceDictionary resources = application.Resources;
        var replacements = new Dictionary<string, object>
        {
            ["AppBackgroundBrush"] = CreateBrush(colors.AppBackground),
            ["AppSurfaceBrush"] = CreateBrush(colors.Surface),
            ["AppTextBrush"] = CreateBrush(colors.Text),
            ["AppSecondaryTextBrush"] = CreateBrush(colors.Secondary),
            ["AppTertiaryTextBrush"] = CreateBrush(colors.Tertiary),
            ["AppSeparatorBrush"] = CreateBrush(colors.Separator),
            ["AppControlBrush"] = CreateBrush(colors.Control),
            ["AppAccentBrush"] = CreateBrush(colors.Accent),
            ["CpuMetricBrush"] = CreateBrush(metrics.Cpu),
            ["MemoryMetricBrush"] = CreateBrush(metrics.Memory),
            ["GpuMetricBrush"] = CreateBrush(metrics.Gpu),
            ["WarningMetricBrush"] = CreateBrush(metrics.Warning),
            ["CriticalMetricBrush"] = CreateBrush(metrics.Critical),
            ["CpuMetricSoftBrush"] = isDefault ? CreateBrush("#E3F0FD") : CreateSoftBrush(metrics.Cpu),
            ["MemoryMetricSoftBrush"] = isDefault ? CreateBrush("#FFF0DC") : CreateSoftBrush(metrics.Memory),
            ["GpuMetricSoftBrush"] = isDefault ? CreateBrush("#EFE8FA") : CreateSoftBrush(metrics.Gpu),
            ["DetailPinBackgroundBrush"] = isDefault ? CreateBrush("#DCEBFF") : CreateSoftBrush(colors.Accent),
            ["DetailPinForegroundBrush"] = CreateBrush(colors.Accent),
            ["DetailUnpinnedForegroundBrush"] = CreateBrush(colors.Secondary),
            ["MetricTrackBrush"] = isDefault ? CreateBrush("#F0F1F4") : CreateBrush(colors.Separator),
            ["AppGroupCornerRadius"] = new CornerRadius(definition.Shape.GroupCornerRadius),
            ["AppShadowOpacity"] = definition.Shape.ShadowOpacity,
            ["BandCornerRadius"] = new CornerRadius(definition.Band.CornerRadius),
            ["BandBackgroundBrush"] = CreateBandBackground(theme)
        };
        foreach ((string key, object value) in replacements)
        {
            resources[key] = value;
        }

        AppliedIdentityToken = theme.IdentityToken;
        return true;
    }

    private static MediaBrush CreateBandBackground(ResolvedTheme theme)
    {
        if (!string.IsNullOrWhiteSpace(theme.BandBackgroundPath))
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(theme.BandBackgroundPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            var imageBrush = new ImageBrush(image)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            imageBrush.Freeze();
            return imageBrush;
        }

        return CreateBrush(theme.Definition.Band.BackgroundColor);
    }

    private static SolidColorBrush CreateSoftBrush(string value)
    {
        MediaColor color = ParseColor(value);
        return CreateBrush(MediaColor.FromArgb(24, color.R, color.G, color.B));
    }

    private static SolidColorBrush CreateBrush(string value) =>
        CreateBrush(ParseColor(value));

    private static SolidColorBrush CreateBrush(MediaColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static MediaColor ParseColor(string value) =>
        (MediaColor)MediaColorConverter.ConvertFromString(value);
}
