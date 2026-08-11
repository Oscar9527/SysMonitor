using System.Collections.Immutable;

namespace SysMonitor.Models;

public sealed record ThemePalette(
    string AppBackground,
    string Surface,
    string Text,
    string Secondary,
    string Tertiary,
    string Separator,
    string Control,
    string Accent);

public sealed record ThemeMetricPalette(
    string Cpu,
    string Memory,
    string Gpu,
    string Warning,
    string Critical);

public sealed record ThemeShape(double GroupCornerRadius, double ShadowOpacity);

public sealed record ThemeBandStyle(
    string BackgroundColor,
    double CornerRadius,
    string? TextColor,
    string? SeparatorColor,
    string? BackgroundImagePath);

public sealed record ThemeDefinition(
    ThemePalette Colors,
    ThemeMetricPalette Metrics,
    ThemeShape Shape,
    ThemeBandStyle Band,
    string? TrayIconPath);

public sealed record ThemeIdentity(
    string Id,
    string Name,
    string Author,
    string Version,
    string? MinimumSysMonitorVersion);

public sealed record ResolvedTheme(
    ThemeIdentity Identity,
    ThemeDefinition Definition,
    string IdentityToken,
    bool IsBuiltIn,
    string? PreviewPath,
    string? BandBackgroundPath,
    string? TrayIconPath);

public sealed record ThemeCatalogItem(
    string Id,
    string Name,
    string Author,
    string Version,
    string IdentityToken,
    bool IsBuiltIn,
    string? PreviewPath);

public enum ThemeImportErrorCode
{
    None,
    Cancelled,
    PackageNotFound,
    InvalidPackage,
    InvalidPath,
    LimitExceeded,
    InvalidManifest,
    InvalidTheme,
    InvalidAsset,
    IncompatibleVersion,
    DuplicateId,
    IoFailure
}

public sealed record ThemeImportResult(
    bool Success,
    ThemeImportErrorCode ErrorCode,
    ResolvedTheme? Theme)
{
    public static ThemeImportResult Failed(ThemeImportErrorCode code) =>
        new(false, code, null);

    public static ThemeImportResult Succeeded(ResolvedTheme theme) =>
        new(true, ThemeImportErrorCode.None, theme);
}

public sealed record ThemeCatalogSnapshot(
    ImmutableArray<ThemeCatalogItem> Items,
    string DefaultThemeId);
