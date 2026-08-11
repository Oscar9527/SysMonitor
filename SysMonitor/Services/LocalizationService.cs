using System.Globalization;
using System.Resources;

namespace SysMonitor.Services;

public sealed class LocalizationService
{
    public const string SystemCulture = "system";
    public const string EnglishCulture = "en-US";
    public const string SimplifiedChineseCulture = "zh-CN";

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo(EnglishCulture);
    private static readonly CultureInfo SimplifiedChinese =
        CultureInfo.GetCultureInfo(SimplifiedChineseCulture);
    private static readonly CultureInfo StartupUiCulture =
        CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentUICulture.Clone());
    private readonly ResourceManager _resources =
        new("SysMonitor.Resources.Strings", typeof(LocalizationService).Assembly);

    private LocalizationService()
    {
    }

    public static LocalizationService Current { get; } = new();

    public event EventHandler? CultureChanged;

    public string CulturePreference { get; private set; } = SystemCulture;

    public CultureInfo ActiveCulture { get; private set; } = English;

    internal static CultureInfo StartupUiCultureSnapshot => StartupUiCulture;

    public void ApplyCulture(string? preference, CultureInfo? systemCulture = null)
    {
        string normalized = NormalizeCulturePreference(preference);
        CultureInfo resolved = ResolveCulture(normalized, systemCulture);
        bool changed = !string.Equals(CulturePreference, normalized, StringComparison.Ordinal) ||
            !string.Equals(ActiveCulture.Name, resolved.Name, StringComparison.OrdinalIgnoreCase);

        CulturePreference = normalized;
        ActiveCulture = resolved;
        CultureInfo.DefaultThreadCurrentCulture = resolved;
        CultureInfo.DefaultThreadCurrentUICulture = resolved;
        CultureInfo.CurrentCulture = resolved;
        CultureInfo.CurrentUICulture = resolved;

        if (changed)
        {
            CultureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _resources.GetString(key, ActiveCulture) ??
            _resources.GetString(key, English) ??
            key;
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(ActiveCulture, GetString(key), arguments);

    public static string NormalizeCulturePreference(string? preference)
    {
        if (string.Equals(preference, EnglishCulture, StringComparison.OrdinalIgnoreCase))
        {
            return EnglishCulture;
        }

        if (string.Equals(preference, SimplifiedChineseCulture, StringComparison.OrdinalIgnoreCase))
        {
            return SimplifiedChineseCulture;
        }

        return SystemCulture;
    }

    public static CultureInfo ResolveCulture(string? preference, CultureInfo? systemCulture = null)
    {
        string normalized = NormalizeCulturePreference(preference);
        if (normalized == EnglishCulture)
        {
            return English;
        }

        if (normalized == SimplifiedChineseCulture)
        {
            return SimplifiedChinese;
        }

        CultureInfo detected = systemCulture ?? StartupUiCulture;
        return string.Equals(detected.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase)
            ? SimplifiedChinese
            : English;
    }
}
