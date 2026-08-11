using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using SysMonitor.Models;
using SysMonitor.Services;
using SysMonitor.UI;

namespace SysMonitor.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalizationCollection
{
    public const string Name = "Localization";
}

[Collection(LocalizationCollection.Name)]
public sealed class LocalizationTests
{
    [Fact]
    public void ResolveCulture_MapsSystemChineseVariantsToSimplifiedChinese()
    {
        CultureInfo resolved = LocalizationService.ResolveCulture(
            "system",
            CultureInfo.GetCultureInfo("zh-TW"));

        Assert.Equal("zh-CN", resolved.Name);
    }

    [Fact]
    public void ResolveCulture_MapsNonChineseSystemCultureToEnglish()
    {
        CultureInfo resolved = LocalizationService.ResolveCulture(
            "system",
            CultureInfo.GetCultureInfo("fr-FR"));

        Assert.Equal("en-US", resolved.Name);
    }

    [Fact]
    public void ResolveCulture_SystemUsesStartupUiCultureSnapshotAfterThreadCultureChanges()
    {
        CultureInfo originalThreadCulture = CultureInfo.CurrentUICulture;
        CultureInfo startupCulture = LocalizationService.StartupUiCultureSnapshot;
        string expected = string.Equals(
            startupCulture.TwoLetterISOLanguageName,
            "zh",
            StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : "en-US";
        CultureInfo replacement = expected == "zh-CN"
            ? CultureInfo.GetCultureInfo("en-US")
            : CultureInfo.GetCultureInfo("zh-CN");

        try
        {
            CultureInfo.CurrentUICulture = replacement;

            Assert.Equal(expected, LocalizationService.ResolveCulture("system").Name);
            Assert.Equal(
                replacement.Name == "zh-CN" ? "zh-CN" : "en-US",
                LocalizationService.ResolveCulture("system", replacement).Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalThreadCulture;
        }
    }

    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("EN-us", "en-US")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("ZH-cn", "zh-CN")]
    public void ResolveCulture_HonorsExplicitSupportedCultures(string preference, string expected)
    {
        Assert.Equal(expected, LocalizationService.ResolveCulture(preference).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("de-DE")]
    public void NormalizeCulturePreference_InvalidValuesUseSystem(string? preference)
    {
        Assert.Equal("system", LocalizationService.NormalizeCulturePreference(preference));
    }

    [Fact]
    public void ResourceCultures_HaveMatchingKeysAndFormatPlaceholders()
    {
        var manager = new ResourceManager(
            "SysMonitor.Resources.Strings",
            typeof(LocalizationService).Assembly);
        ResourceSet english = Assert.IsAssignableFrom<ResourceSet>(manager.GetResourceSet(
            CultureInfo.GetCultureInfo("en-US"), true, true));
        ResourceSet chinese = Assert.IsAssignableFrom<ResourceSet>(manager.GetResourceSet(
            CultureInfo.GetCultureInfo("zh-CN"), true, false));

        Dictionary<string, string> englishValues = ReadValues(english);
        Dictionary<string, string> chineseValues = ReadValues(chinese);
        Assert.Equal(englishValues.Keys.Order(), chineseValues.Keys.Order());

        foreach (string key in englishValues.Keys)
        {
            Assert.Equal(
                ExtractPlaceholders(englishValues[key]),
                ExtractPlaceholders(chineseValues[key]));
        }
    }

    [Fact]
    public void DynamicDetailText_ChangesLanguageAndKeepsNeutralUnits()
    {
        LocalizationService localization = LocalizationService.Current;
        try
        {
            localization.ApplyCulture("en-US");
            string englishCpu = DetailWindow.BuildCpuDetails(12, 55);
            string englishGpu = DetailWindow.BuildGpuDetails(CreateGpu());

            localization.ApplyCulture("zh-CN");
            string chineseCpu = DetailWindow.BuildCpuDetails(12, 55);
            string chineseGpu = DetailWindow.BuildGpuDetails(CreateGpu());

            Assert.Contains("logical processors", englishCpu);
            Assert.Contains("个逻辑处理器", chineseCpu);
            Assert.Contains("°C", englishCpu);
            Assert.Contains("°C", chineseCpu);
            Assert.Contains("GB VRAM", englishGpu);
            Assert.Contains("GB VRAM", chineseGpu);
        }
        finally
        {
            localization.ApplyCulture("system");
        }
    }

    [Fact]
    public void ApplyCulture_RaisesCultureChangedWhenEffectiveCultureChanges()
    {
        LocalizationService localization = LocalizationService.Current;
        int raised = 0;
        EventHandler handler = (_, _) => raised++;
        localization.CultureChanged += handler;
        try
        {
            localization.ApplyCulture("en-US");
            raised = 0;
            localization.ApplyCulture("zh-CN");
            Assert.Equal(1, raised);

            localization.ApplyCulture("zh-CN");
            Assert.Equal(1, raised);
        }
        finally
        {
            localization.CultureChanged -= handler;
            localization.ApplyCulture("system");
        }
    }

    private static GpuSnapshot CreateGpu() => new(
        0,
        string.Empty,
        50,
        62,
        2L * 1024 * 1024 * 1024,
        4L * 1024 * 1024 * 1024,
        DateTimeOffset.UtcNow);

    private static Dictionary<string, string> ReadValues(ResourceSet resourceSet) =>
        resourceSet.Cast<DictionaryEntry>().ToDictionary(
            entry => Assert.IsType<string>(entry.Key),
            entry => Assert.IsType<string>(entry.Value));

    private static string[] ExtractPlaceholders(string value) =>
        Regex.Matches(value, @"\{\d+(?:[^}]*)?\}")
            .Select(match => Regex.Match(match.Value, @"\d+").Value)
            .Order()
            .ToArray();
}
