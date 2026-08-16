using System.Text.Json;
using SysMonitor.Models;
using SysMonitor.Services;
using SysMonitor.UI;

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
        Assert.Equal(BandMetricVisibility.All, loaded.BandMetricVisibility!.ToEffective());
        Assert.Equal(AppSettings.DefaultThemeId, loaded.ActiveThemeId);
        Assert.True(loaded.GameSafeMode);
    }

    [Fact]
    public void Load_MissingGameSafeMode_MigratesToSafeDefault()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        Directory.CreateDirectory(directory.Path);
        File.WriteAllText(service.SettingsPath, "{}");

        Assert.True(service.Load().GameSafeMode);
    }

    [Fact]
    public void SaveAndLoad_ExplicitDisabledGameSafeModeRoundTrips()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);

        service.Save(new AppSettings { GameSafeMode = false });

        Assert.False(service.Load().GameSafeMode);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(service.SettingsPath));
        Assert.False(document.RootElement.GetProperty("GameSafeMode").GetBoolean());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ActiveThemeId\":null}")]
    [InlineData("{\"ActiveThemeId\":\"   \"}")]
    public void Load_MigratesMissingOrBlankThemeToBuiltInDefault(string json)
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        Directory.CreateDirectory(directory.Path);
        File.WriteAllText(service.SettingsPath, json);

        Assert.Equal(AppSettings.DefaultThemeId, service.Load().ActiveThemeId);
    }

    [Fact]
    public void TrySave_ReturnsFalseWhenSettingsDirectoryCannotBeCreated()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        string occupiedPath = Path.Combine(directory.Path, "occupied");
        File.WriteAllText(occupiedPath, "not a directory");
        var service = new SettingsService(occupiedPath);

        bool saved = service.TrySave(new AppSettings());

        Assert.False(saved);
    }

    [Fact]
    public void Load_TrimsExplicitThemeId()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        Directory.CreateDirectory(directory.Path);
        File.WriteAllText(service.SettingsPath, "{\"ActiveThemeId\":\"  custom.blue  \"}");

        Assert.Equal("custom.blue", service.Load().ActiveThemeId);
    }

    [Theory]
    [InlineData("{}", true, true, true, true, true, true)]
    [InlineData("{\"BandMetricVisibility\":null}", true, true, true, true, true, true)]
    [InlineData("{\"BandMetricVisibility\":{\"Cpu\":false}}", false, true, true, true, true, true)]
    [InlineData("{\"BandMetricVisibility\":{\"Cpu\":null,\"Gpu\":false,\"FutureMetric\":false}}", true, true, false, true, true, true)]
    public void Load_MigratesVisibilityFieldsWithoutOverwritingExplicitFalse(
        string json,
        bool cpu,
        bool memory,
        bool gpu,
        bool download,
        bool upload,
        bool systemDisk)
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        Directory.CreateDirectory(directory.Path);
        File.WriteAllText(service.SettingsPath, json);

        BandMetricVisibility visibility = service.Load().BandMetricVisibility!.ToEffective();

        Assert.Equal(
            new BandMetricVisibility(cpu, memory, gpu, download, upload, systemDisk),
            visibility);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsAllVisibilityValues()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        var expected = new BandMetricVisibility(false, true, false, true, false, true);
        var settings = new AppSettings
        {
            BandMetricVisibility = BandMetricVisibilitySettings.FromEffective(expected)
        };

        service.Save(settings);
        AppSettings loaded = service.Load();

        Assert.Equal(expected, loaded.BandMetricVisibility!.ToEffective());
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(service.SettingsPath));
        JsonElement persisted = document.RootElement.GetProperty("BandMetricVisibility");
        Assert.False(persisted.GetProperty("Cpu").GetBoolean());
        Assert.True(persisted.GetProperty("SystemDisk").GetBoolean());
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

    [Fact]
    public void SaveAndLoad_RoundTripsHudPresetAndMetricSelection()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        var expected = new GameOverlayMetricVisibility(true, false, true, false, true);

        service.Save(new AppSettings
        {
            GameOverlayPreset = "detailed",
            GameOverlayMetrics = GameOverlayMetricVisibilitySettings.FromEffective(expected)
        });

        AppSettings loaded = service.Load();
        Assert.Equal("detailed", loaded.GameOverlayPreset);
        Assert.Equal(expected, loaded.GameOverlayMetrics!.ToEffective());
    }

    [Fact]
    public void Load_InvalidHudPresetUsesRivatunerDefault()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        Directory.CreateDirectory(directory.Path);
        File.WriteAllText(service.SettingsPath, "{\"GameOverlayPreset\":\"unknown\"}");

        Assert.Equal("rivatuner", service.Load().GameOverlayPreset);
    }

    [Fact]
    public void Load_OldSettingsUseVerticalLayoutAndLegacyPositionFallback()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        Directory.CreateDirectory(directory.Path);
        File.WriteAllText(service.SettingsPath, "{\"GameOverlayHorizontalPositionPercent\":75}");

        AppSettings loaded = service.Load();

        Assert.Equal("vertical", loaded.GameOverlayLayoutMode);
        Assert.Equal(75, loaded.GameOverlayHorizontalPositionPercent);
        Assert.Empty(loaded.GameOverlayMonitorPositions!);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsHorizontalLayoutAndPerMonitorCoordinates()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        service.Save(new AppSettings
        {
            GameOverlayLayoutMode = "HORIZONTAL",
            GameOverlayMonitorPositions =
            [
                new GameOverlayMonitorPositionSettings
                {
                    StableMonitorId = " monitor-path ",
                    GdiDeviceName = @" \\.\display2 ",
                    Left = -2560,
                    Top = 0,
                    Right = 0,
                    Bottom = 1440,
                    X = -2500,
                    Y = 100
                }
            ]
        });

        AppSettings loaded = service.Load();
        GameOverlayMonitorPositionSettings position = Assert.Single(loaded.GameOverlayMonitorPositions!);
        Assert.Equal("horizontal", loaded.GameOverlayLayoutMode);
        Assert.Equal("MONITOR-PATH", position.StableMonitorId);
        Assert.Equal(@"\\.\DISPLAY2", position.GdiDeviceName);
        Assert.Equal(-2500, position.X);
        Assert.Equal(100, position.Y);
    }

    [Fact]
    public void LivePreviewLeavesPersistedSettingsBytesAndInMemoryMapUnchanged()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        var position = new GameOverlayMonitorPositionSettings
        {
            StableMonitorId = "MONITOR-A",
            GdiDeviceName = @"\\.\DISPLAY1",
            Left = 0,
            Top = 0,
            Right = 1920,
            Bottom = 1080,
            X = 10,
            Y = 20
        };
        var settings = new AppSettings
        {
            GameOverlayLayoutMode = "vertical",
            GameOverlayMonitorPositions = [position]
        };
        service.Save(settings);
        byte[] beforeFile = File.ReadAllBytes(service.SettingsPath);
        string beforeMap = JsonSerializer.Serialize(settings.GameOverlayMonitorPositions);
        OverlayMonitorIdentity identity = OverlayMonitorIdentity.CreateStable(
            "monitor-a", @"\\.\DISPLAY1", "A", new ScreenPixelBounds(0, 0, 1920, 1080));

        IReadOnlyList<GameOverlayMonitorPositionSettings> preview =
            GameOverlayWindow.BuildPreviewMonitorPositions(
                settings.GameOverlayMonitorPositions, identity, true, 500, 600);

        Assert.Equal(beforeFile, File.ReadAllBytes(service.SettingsPath));
        Assert.Equal(beforeMap, JsonSerializer.Serialize(settings.GameOverlayMonitorPositions));
        Assert.Equal(500, Assert.Single(preview).X);
        Assert.Equal(10, Assert.Single(settings.GameOverlayMonitorPositions!).X);
    }

    [Fact]
    public void Load_InvalidLayoutAndMonitorEntryFailClosed()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path);
        Directory.CreateDirectory(directory.Path);
        File.WriteAllText(service.SettingsPath,
            "{\"GameOverlayLayoutMode\":\"diagonal\",\"GameOverlayMonitorPositions\":[{\"StableMonitorId\":\"x\",\"GdiDeviceName\":\"display\",\"Left\":0,\"Top\":0,\"Right\":0,\"Bottom\":0}]}");

        AppSettings loaded = service.Load();

        Assert.Equal("vertical", loaded.GameOverlayLayoutMode);
        Assert.Empty(loaded.GameOverlayMonitorPositions!);
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
