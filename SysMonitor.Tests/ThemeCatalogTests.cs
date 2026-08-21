using System.Text;
using SysMonitor.Models;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class ThemeCatalogTests
{
    [Fact]
    public async Task BuiltInsAreImmutableDefaultsAndMatchModernPalette()
    {
        using var temp = new ThemeTestDirectory();
        var catalog = new ThemeCatalogService(temp.Themes, new Version(1, 3, 0));

        await catalog.InitializeAsync();
        ResolvedTheme theme = catalog.ResolveOrDefault("builtin.default");

        Assert.True(theme.IsBuiltIn);
        Assert.Equal("builtin-default-v1", theme.IdentityToken);
        Assert.Equal("#F5F6F8", theme.Definition.Colors.AppBackground);
        Assert.Equal("#D9DCE2", theme.Definition.Colors.Separator);
        Assert.Equal("#F0F1F4", theme.Definition.Colors.Control);
        Assert.Equal(10, theme.Definition.Shape.GroupCornerRadius);
        Assert.Equal(0, theme.Definition.Shape.ShadowOpacity);
        Assert.Equal("#00000000", theme.Definition.Band.BackgroundColor);
        Assert.Null(theme.Definition.Band.TextColor);
        Assert.Null(theme.Definition.Band.SeparatorColor);

        ResolvedTheme midnight = catalog.ResolveOrDefault("builtin.midnight");
        Assert.True(midnight.IsBuiltIn);
        Assert.Equal("builtin-midnight-v1", midnight.IdentityToken);
        Assert.Equal("#17181B", midnight.Definition.Colors.AppBackground);
        Assert.Equal("#00000000", midnight.Definition.Band.BackgroundColor);
        Assert.Null(midnight.Definition.Band.TextColor);
        Assert.Null(midnight.Definition.Band.SeparatorColor);
    }

    [Fact]
    public async Task DuplicateIdIsRejectedWithoutReplacingInstalledTheme()
    {
        using var temp = new ThemeTestDirectory();
        var catalog = new ThemeCatalogService(temp.Themes, new Version(1, 3, 0));
        await catalog.InitializeAsync();
        string first = ThemeTestPackage.Create(temp.Path, "same-id");
        string second = ThemeTestPackage.Create(temp.Path, "same-id");

        ThemeImportResult installed = await catalog.ImportAsync(first);
        ThemeImportResult duplicate = await catalog.ImportAsync(second);

        Assert.True(installed.Success);
        Assert.False(duplicate.Success);
        Assert.Equal(ThemeImportErrorCode.DuplicateId, duplicate.ErrorCode);
        Assert.Equal(installed.Theme!.IdentityToken, catalog.ResolveOrDefault("same-id").IdentityToken);
    }

    [Fact]
    public async Task ConcurrentDifferentImportsBothCommitAndRemainInCatalog()
    {
        using var temp = new ThemeTestDirectory();
        var catalog = new ThemeCatalogService(temp.Themes, new Version(1, 3, 0));
        await catalog.InitializeAsync();
        string firstPackage = ThemeTestPackage.Create(temp.Path, "parallel-one");
        string secondPackage = ThemeTestPackage.Create(temp.Path, "parallel-two");

        ThemeImportResult[] results = await Task.WhenAll(
            catalog.ImportAsync(firstPackage),
            catalog.ImportAsync(secondPackage));

        Assert.All(results, result => Assert.True(result.Success));
        Assert.True(catalog.TryResolve("parallel-one", out ResolvedTheme first));
        Assert.True(catalog.TryResolve("parallel-two", out ResolvedTheme second));
        Assert.NotEqual(first.IdentityToken, second.IdentityToken);
        Assert.Contains(catalog.Catalog.Items, item => item.Id == "parallel-one");
        Assert.Contains(catalog.Catalog.Items, item => item.Id == "parallel-two");
    }

    [Fact]
    public async Task InitializeCleansSafeStagingAndSkipsCorruptTheme()
    {
        using var temp = new ThemeTestDirectory();
        string staging = Path.Combine(temp.Themes, ".staging-old");
        string corrupt = Path.Combine(temp.Themes, "corrupt");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(corrupt);
        File.WriteAllText(Path.Combine(staging, "partial"), "x");
        File.WriteAllText(Path.Combine(corrupt, "manifest.json"), "not json");
        var catalog = new ThemeCatalogService(temp.Themes, new Version(1, 3, 0));

        await catalog.InitializeAsync();

        Assert.False(Directory.Exists(staging));
        Assert.False(catalog.TryResolve("corrupt", out _));
        Assert.Equal(2, catalog.Themes.Length);
    }

    [Fact]
    public async Task ReinitializedCatalogValidatesInstalledFilesAndPreservesIdentity()
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(temp.Path, "persisted");
        var importer = new ThemeCatalogService(temp.Themes, new Version(1, 3, 0));
        await importer.InitializeAsync();
        ThemeImportResult imported = await importer.ImportAsync(package);

        var reloaded = new ThemeCatalogService(temp.Themes, new Version(1, 3, 0));
        await reloaded.InitializeAsync();

        Assert.True(reloaded.TryResolve("persisted", out ResolvedTheme resolved));
        Assert.Equal(imported.Theme!.IdentityToken, resolved.IdentityToken);
        Assert.Equal(imported.Theme.Identity, resolved.Identity);
    }

    [Fact]
    public async Task CatalogRejectsUnexpectedInstalledFileAndFallsBack()
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(temp.Path, "tampered");
        var importer = new ThemeCatalogService(temp.Themes, new Version(1, 3, 0));
        await importer.InitializeAsync();
        Assert.True((await importer.ImportAsync(package)).Success);
        File.WriteAllText(Path.Combine(temp.Themes, "tampered", "run.exe"), "payload");

        var catalog = new ThemeCatalogService(temp.Themes, new Version(1, 3, 0));
        await catalog.InitializeAsync();

        Assert.False(catalog.TryResolve("tampered", out ResolvedTheme fallback));
        Assert.Equal(ThemeCatalogService.DefaultThemeId, fallback.Identity.Id);
        Assert.Equal(ThemeCatalogService.DefaultThemeId, catalog.ResolveOrDefault("../tampered").Identity.Id);
    }

    [Fact]
    public async Task ReparseThemeDirectoryIsSkippedWhenPlatformAllowsCreation()
    {
        using var temp = new ThemeTestDirectory();
        Directory.CreateDirectory(temp.Themes);
        string target = Path.Combine(temp.Path, "target");
        string link = Path.Combine(temp.Themes, "linked");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var catalog = new ThemeCatalogService(temp.Themes, new Version(1, 3, 0));
        await catalog.InitializeAsync();

        Assert.False(catalog.TryResolve("linked", out _));
    }

    [Fact]
    public async Task CancellationReturnsStructuredErrorForImport()
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(temp.Path, "cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package, cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.Cancelled, result.ErrorCode);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("SYSTEM")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public async Task SystemThemeIdResolvesToValidBuiltInTheme(string systemId)
    {
        using var temp = new ThemeTestDirectory();
        var catalog = new ThemeCatalogService(temp.Themes, new Version(1, 3, 0));
        await catalog.InitializeAsync();

        Assert.True(catalog.TryResolve(systemId, out ResolvedTheme resolved));
        Assert.True(resolved.IsBuiltIn);
        Assert.True(
            resolved.Identity.Id == ThemeCatalogService.DefaultThemeId ||
            resolved.Identity.Id == ThemeCatalogService.MidnightThemeId);
    }

    [Fact]
    public async Task CatalogSnapshotIncludesSystemThemeAsFirstOption()
    {
        using var temp = new ThemeTestDirectory();
        var catalog = new ThemeCatalogService(temp.Themes, new Version(1, 3, 0));
        await catalog.InitializeAsync();

        var items = catalog.Catalog.Items;
        Assert.NotEmpty(items);
        Assert.Equal(ThemeCatalogService.SystemThemeId, items[0].Id);
        Assert.True(items[0].IsBuiltIn);
    }
}
