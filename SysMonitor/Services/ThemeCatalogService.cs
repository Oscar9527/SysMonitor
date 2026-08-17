using System.Collections.Immutable;
using System.IO;
using SysMonitor.Models;

namespace SysMonitor.Services;

public sealed class ThemeCatalogService
{
    public const string DefaultThemeId = "builtin.default";
    public const string MidnightThemeId = "builtin.midnight";

    private readonly ThemePackageService _packages;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private ImmutableDictionary<string, ResolvedTheme> _themes = BuiltIns;

    public ThemeCatalogService(string? themesRoot = null, Version? applicationVersion = null)
    {
        _packages = new ThemePackageService(themesRoot, applicationVersion);
    }

    public ImmutableArray<ResolvedTheme> Themes =>
        _themes.Values
            .OrderByDescending(theme => theme.IsBuiltIn)
            .ThenBy(theme => theme.Identity.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToImmutableArray();

    public ThemeCatalogSnapshot Catalog =>
        new(
            Themes.Select(theme => new ThemeCatalogItem(
                    theme.Identity.Id,
                    theme.Identity.Name,
                    theme.Identity.Author,
                    theme.Identity.Version,
                    theme.IdentityToken,
                    theme.IsBuiltIn,
                    theme.PreviewPath))
                .ToImmutableArray(),
            DefaultThemeId);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(
                () => _packages.PrepareRootAndCleanStaging(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _themes = BuiltIns;
            return;
        }

        var builder = BuiltIns.ToBuilder();
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(
                _packages.ThemesRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch
        {
            _themes = builder.ToImmutable();
            return;
        }

        foreach (string directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string folderName = Path.GetFileName(directory);
            if (folderName.StartsWith(".staging-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ResolvedTheme? theme = await _packages.ValidateInstalledAsync(
                directory, cancellationToken).ConfigureAwait(false);
            if (theme is null || theme.IsBuiltIn ||
                !string.Equals(folderName, theme.Identity.Id, StringComparison.Ordinal) ||
                builder.ContainsKey(theme.Identity.Id))
            {
                continue;
            }

            builder[theme.Identity.Id] = theme;
        }

        _themes = builder.ToImmutable();
    }

    public bool TryResolve(string? id, out ResolvedTheme theme)
    {
        if (id is not null && _themes.TryGetValue(id, out ResolvedTheme? resolved))
        {
            theme = resolved;
            return true;
        }

        theme = _themes[DefaultThemeId];
        return false;
    }

    public ResolvedTheme ResolveOrDefault(string? id) =>
        TryResolve(id, out ResolvedTheme theme) ? theme : _themes[DefaultThemeId];

    public async Task<ThemeImportResult> ImportAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ThemeImportResult.Failed(ThemeImportErrorCode.Cancelled);
        }

        try
        {
            ThemeImportResult result = await _packages.ImportAsync(
                packagePath, cancellationToken).ConfigureAwait(false);
            if (!result.Success || result.Theme is null)
            {
                return result;
            }

            ImmutableDictionary<string, ResolvedTheme> updated =
                _themes.SetItem(result.Theme.Identity.Id, result.Theme);
            _themes = updated;
            return result;
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private static readonly ImmutableDictionary<string, ResolvedTheme> BuiltIns =
        new[]
        {
            CreateBuiltIn(
                DefaultThemeId,
                "Default",
                "builtin-default-v1",
                new ThemePalette(
                    "#F5F6F8", "#FFFFFFFF", "#1D1D1F", "#62666E",
                    "#858A93", "#D9DCE2", "#F0F1F4", "#0A66E8"),
                "#00000000",
                null,
                null,
                10),
            CreateBuiltIn(
                MidnightThemeId,
                "Midnight",
                "builtin-midnight-v1",
                new ThemePalette(
                    "#17181B", "#202125", "#F5F5F7", "#B8BBC2",
                    "#8E939C", "#45474F", "#32343A", "#70B5FF"),
                "#F017181B",
                "#F5F5F7",
                "#66FFFFFF",
                10)
        }.ToImmutableDictionary(theme => theme.Identity.Id, StringComparer.OrdinalIgnoreCase);

    private static ResolvedTheme CreateBuiltIn(
        string id,
        string name,
        string token,
        ThemePalette colors,
        string bandBackground,
        string? bandText,
        string? bandSeparator,
        double groupCornerRadius)
    {
        var definition = new ThemeDefinition(
            colors,
            new ThemeMetricPalette("#1976D2", "#D97706", "#7E57C2", "#D97706", "#C6262E"),
            new ThemeShape(groupCornerRadius, 0),
            new ThemeBandStyle(bandBackground, 0, bandText, bandSeparator, null),
            null);
        return new ResolvedTheme(
            new ThemeIdentity(id, name, "SysMonitor", "1.0", null),
            definition,
            token,
            true,
            null,
            null,
            null);
    }
}
