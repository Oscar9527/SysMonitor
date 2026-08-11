using System.Text.Json;
using SysMonitor.Models;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void Load_LegacySettingsDefaultCultureAndPreserveAppearance()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        Directory.CreateDirectory(directory.Path);
        File.WriteAllText(
            service.SettingsPath,
            """
            {
              "BandFontFamily": "Consolas",
              "BandFontSize": 15,
              "BandItemSpacingDip": 7,
              "PanelTopmost": true
            }
            """);

        AppSettings loaded = service.Load();

        Assert.Equal("system", loaded.UiCulture);
        Assert.Equal("Consolas", loaded.BandFontFamily);
        Assert.Equal(15, loaded.BandFontSize);
        Assert.Equal(7, loaded.BandItemSpacingDip);
        Assert.True(loaded.PanelTopmost);
    }

    [Fact]
    public void SaveAndLoad_PersistValidCultureAndAppearance()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        var settings = new AppSettings
        {
            UiCulture = "zh-CN",
            BandVisible = false,
            BandFontFamily = "Segoe UI",
            BandFontSize = 14,
            BandHorizontalPositionPercent = 42,
            PanelLeft = 120,
            PanelTop = 80,
            PanelTopmost = true
        };

        service.Save(settings);
        AppSettings loaded = service.Load();

        Assert.Equal("zh-CN", loaded.UiCulture);
        Assert.False(loaded.BandVisible);
        Assert.Equal("Segoe UI", loaded.BandFontFamily);
        Assert.Equal(42, loaded.BandHorizontalPositionPercent);
        Assert.Equal(120, loaded.PanelLeft);
        Assert.Equal(80, loaded.PanelTop);
        Assert.True(loaded.PanelTopmost);
    }

    [Fact]
    public void Load_InvalidCultureNormalizesToSystem()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        Directory.CreateDirectory(directory.Path);
        File.WriteAllText(service.SettingsPath, "{\"UiCulture\":\"es-ES\"}");

        Assert.Equal("system", service.Load().UiCulture);
    }

    [Fact]
    public void Load_DamagedFileReturnsSafeDefaults()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        Directory.CreateDirectory(directory.Path);
        File.WriteAllText(service.SettingsPath, "{ definitely not json");

        AppSettings loaded = service.Load();

        Assert.Equal("system", loaded.UiCulture);
        Assert.True(loaded.BandVisible);
        Assert.Equal("Segoe UI Variable Text", loaded.BandFontFamily);
        Assert.Equal(13, loaded.BandFontSize);
    }

    [Fact]
    public void Save_NormalizesInvalidCultureBeforeSerialization()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        var settings = new AppSettings { UiCulture = "invalid" };

        service.Save(settings);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(service.SettingsPath));
        Assert.Equal("system", document.RootElement.GetProperty("UiCulture").GetString());
        Assert.Equal("system", settings.UiCulture);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SysMonitor.Tests.{Guid.NewGuid():N}");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
