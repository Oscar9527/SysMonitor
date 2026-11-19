using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SysMonitor.Models;
using SysMonitor.Services;
using SysMonitor.UI;
using Forms = System.Windows.Forms;
using GdiBitmap = System.Drawing.Bitmap;
using GdiGraphics = System.Drawing.Graphics;
using GdiSize = System.Drawing.Size;
using MediaBrush = System.Windows.Media.Brush;
using MediaColors = System.Windows.Media.Colors;
using MediaPen = System.Windows.Media.Pen;
using WpfRect = System.Windows.Rect;
using WpfSize = System.Windows.Size;

namespace SysMonitor.Tests;

public sealed class TaskbarThemeBugfixTests
{
    [Fact]
    public void HistorySparkline_CreateFrozenPen_ClonesUnfrozenBrushWithoutFreezingInput()
    {
        RunSta(() =>
        {
            var brush = new SolidColorBrush(MediaColors.DeepSkyBlue);
            Assert.False(brush.IsFrozen);

            MediaPen? pen = HistorySparkline.CreateFrozenPen(brush, 1.6);

            Assert.NotNull(pen);
            Assert.True(pen!.IsFrozen);
            Assert.False(brush.IsFrozen);
            Assert.Null(HistorySparkline.CreateFrozenPen(null, 1.6));
        });
    }

    [Fact]
    public void HistorySparkline_Render_WithUnfrozenStrokeBrush_DoesNotThrowOrFreezeBrush()
    {
        RunSta(() =>
        {
            var stroke = new SolidColorBrush(MediaColors.LimeGreen);
            var sparkline = new HistorySparkline
            {
                Stroke = stroke,
                Series = ImmutableArray.Create(
                    new MetricHistoryPoint(1, 1, 1_000_000, 25, 10),
                    new MetricHistoryPoint(1, 2, 1_000_000 + Stopwatch.Frequency, 75, 40)),
            };

            sparkline.Measure(new WpfSize(120, 40));
            sparkline.Arrange(new WpfRect(0, 0, 120, 40));
            sparkline.UpdateLayout();

            var bitmap = new RenderTargetBitmap(120, 40, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(sparkline);

            Assert.False(stroke.IsFrozen);
        });
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 6)]
    [InlineData(8, 2)]
    [InlineData(10, 4)]
    [InlineData(12, 8)]
    [InlineData(160, 28)]
    public void ModernToolStripRenderer_DrawMenuItemBackground_HandlesDegenerateAndSmallSizes(
        int width,
        int height)
    {
        RunSta(() =>
        {
            using var bitmap = new GdiBitmap(200, 40);
            using GdiGraphics graphics = GdiGraphics.FromImage(bitmap);
            var renderer = new ModernToolStripRenderer();
            using var strip = new Forms.ContextMenuStrip { AutoSize = false };
            using var item = new Forms.ToolStripMenuItem
            {
                AutoSize = false,
                Enabled = true
            };
            strip.Items.Add(item);
            item.Size = new GdiSize(width, height);
            item.Select();
            Assert.True(item.Selected);

            renderer.DrawMenuItemBackground(new Forms.ToolStripItemRenderEventArgs(graphics, item));
        });
    }

    [Fact]
    public void TrayIconService_ApplyMenuRenderer_UpdatesRootAndSubmenuDropDowns()
    {
        RunSta(() =>
        {
            using var menu = new Forms.ContextMenuStrip();
            var leaf = new Forms.ToolStripMenuItem("Compact");
            var child = new Forms.ToolStripMenuItem("Preset");
            child.DropDownItems.Add(leaf);
            var parent = new Forms.ToolStripMenuItem("Overlay");
            parent.DropDownItems.Add(child);
            menu.Items.Add(parent);

            TrayIconService.ConfigureMenuItems(menu.Items);
            menu.Renderer = new Forms.ToolStripProfessionalRenderer();
            parent.DropDown.Renderer = new Forms.ToolStripProfessionalRenderer();
            child.DropDown.Renderer = new Forms.ToolStripProfessionalRenderer();

            TrayIconService.ApplyMenuRenderer(menu);

            Assert.Same(menu.Renderer, parent.DropDown.Renderer);
            Assert.Same(menu.Renderer, child.DropDown.Renderer);
            if (Forms.SystemInformation.HighContrast)
            {
                Assert.IsType<Forms.ToolStripSystemRenderer>(menu.Renderer);
            }
            else
            {
                Assert.IsType<ModernToolStripRenderer>(menu.Renderer);
            }
        });
    }

    [Theory]
    [InlineData(unchecked((int)0xA1FF0000), false, true)]  // Unix symlink
    [InlineData(0x11B60000, false, true)]                   // Unix FIFO
    [InlineData(0x21B60000, false, true)]                   // Unix char device
    [InlineData(0x61B60000, false, true)]                   // Unix block device
    [InlineData(unchecked((int)0xC1ED0000), false, true)]  // Unix socket
    [InlineData(0x41ED0000, false, true)]                   // Unix dir marked as file
    [InlineData(unchecked((int)0x81A40000), true, true)]   // Unix regular file marked as dir
    [InlineData((int)FileAttributes.ReparsePoint, false, true)]
    [InlineData((int)FileAttributes.Device, false, true)]
    [InlineData(unchecked((int)0x81A40000), false, false)] // Unix regular file
    [InlineData(0x41ED0000, true, false)]                   // Unix directory
    [InlineData((int)FileAttributes.Archive, false, false)] // DOS archive file
    public void ThemePackageService_HasUnsafeArchiveAttributes_ClassifiesCorrectly(
        int externalAttributes,
        bool directory,
        bool expectedUnsafe)
    {
        Assert.Equal(
            expectedUnsafe,
            ThemePackageService.HasUnsafeArchiveAttributes(externalAttributes, directory));
    }

    [Fact]
    public async Task ThemePackageService_ImportAsync_RejectsSymlinkArchiveEntry()
    {
        using var temp = new TempDirectoryScope();
        string packagePath = Path.Combine(temp.RootPath, "symlink.smonitor-theme");
        CreateThemeZip(
            packagePath,
            "symlink-theme",
            "1.0.0",
            ("README.md", Encoding.UTF8.GetBytes("license/readme target"), unchecked((int)0xA1FF0000)));

        var service = new ThemePackageService(temp.ThemesPath, new Version(1, 3, 0));
        ThemeImportResult result = await service.ImportAsync(packagePath);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.InvalidPath, result.ErrorCode);
    }

    [Theory]
    [InlineData(" 1.0.0")]
    [InlineData("1.0.0 ")]
    [InlineData("1.0.0\n")]
    [InlineData("1.0.0\t")]
    public async Task ThemePackageService_ImportAsync_RejectsWhitespaceOrControlCharsInVersion(
        string malformedVersion)
    {
        using var temp = new TempDirectoryScope();
        string packagePath = Path.Combine(temp.RootPath, "bad-version.smonitor-theme");
        CreateThemeZip(packagePath, "bad-version", malformedVersion);

        var service = new ThemePackageService(temp.ThemesPath, new Version(1, 3, 0));
        ThemeImportResult result = await service.ImportAsync(packagePath);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.InvalidManifest, result.ErrorCode);
    }

    [Fact]
    public void ThemeResourceApplier_CreateBandBackground_SupportsHashInPathAndReleasesFileLock()
    {
        RunSta(() =>
        {
            using var temp = new TempDirectoryScope();
            string dirWithHash = Path.Combine(temp.RootPath, "Theme #1");
            Directory.CreateDirectory(dirWithHash);
            string imagePath = Path.Combine(dirWithHash, "band-background.png");

            var source = BitmapSource.Create(8, 2, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 2 * 4], 8 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (FileStream fs = File.Create(imagePath))
            {
                encoder.Save(fs);
            }

            ResolvedTheme theme = CreateResolvedTheme("hash-theme", "#FF112233", imagePath);
            MediaBrush brush = ThemeResourceApplier.CreateBandBackground(theme);

            ImageBrush imageBrush = Assert.IsType<ImageBrush>(brush);
            Assert.True(imageBrush.IsFrozen);

            // Decoding via BitmapCacheOption.OnLoad + closed FileStream must not keep an OS lock.
            File.Delete(imagePath);
            Assert.False(File.Exists(imagePath));
        });
    }

    [Fact]
    public void TaskbarRegionMonitor_TryConvertBounds_RejectsNonFiniteAndOverflowRects()
    {
        Assert.False(TaskbarRegionMonitor.TryConvertBounds(WpfRect.Empty, out _, out _, out _, out _));
        Assert.False(TaskbarRegionMonitor.TryConvertBounds(new WpfRect(double.NaN, 0, 10, 10), out _, out _, out _, out _));
        Assert.False(TaskbarRegionMonitor.TryConvertBounds(new WpfRect(0, double.PositiveInfinity, 10, 10), out _, out _, out _, out _));
        Assert.False(TaskbarRegionMonitor.TryConvertBounds(new WpfRect(0, 0, 0, 10), out _, out _, out _, out _));
        Assert.False(TaskbarRegionMonitor.TryConvertBounds(new WpfRect(-1e20, 0, 10, 10), out _, out _, out _, out _));

        Assert.True(TaskbarRegionMonitor.TryConvertBounds(
            new WpfRect(10.2, 20.9, 30.1, 40.2),
            out int left,
            out int top,
            out int right,
            out int bottom));
        Assert.Equal(10, left);
        Assert.Equal(20, top);
        Assert.Equal(41, right);
        Assert.Equal(62, bottom);
    }

    [Fact]
    public void BandWindow_VisibilityNonFiniteTelemetryAndThemeRefresh_BehaveSafely()
    {
        RunSta(() =>
        {
            var window = new BandWindow(generation: 1);
            try
            {
                var gpuGroup = Assert.IsType<Grid>(window.FindName("GpuGroup"));
                var gpuTemp = Assert.IsType<TextBlock>(window.FindName("GpuTemperatureText"));
                var cpuTemp = Assert.IsType<TextBlock>(window.FindName("CpuTemperatureText"));
                var cpuValue = Assert.IsType<TextBlock>(window.FindName("CpuValueText"));
                var memCap = Assert.IsType<TextBlock>(window.FindName("MemoryCapacityText"));
                var memValue = Assert.IsType<TextBlock>(window.FindName("MemoryValueText"));

                window.ApplyAppearance(new BandAppearanceSettings(
                    "Segoe UI",
                    13,
                    MetricVisibility: new BandMetricVisibility(
                        Cpu: true,
                        Memory: true,
                        Gpu: true,
                        CpuTemperature: true,
                        CpuPower: true,
                        MemoryUsage: true,
                        MemoryUsedCapacity: true,
                        GpuTemperature: false,
                        GpuPower: false)));

                Assert.Equal(Visibility.Collapsed, gpuTemp.Visibility);
                Assert.Equal(string.Empty, gpuTemp.Text);

                var singleGpuSnapshot = new MonitorSnapshot(
                    Sequence: 1,
                    SampledAt: DateTimeOffset.UtcNow,
                    CpuUsagePercent: 42,
                    CpuTemperatureCelsius: double.NaN,
                    LogicalProcessorCount: 8,
                    MemoryUsagePercent: 50,
                    MemoryUsedBytes: -1024,
                    MemoryTotalBytes: 16L * 1024 * 1024 * 1024,
                    Gpu: new GpuSnapshot(0, "Test GPU", 30, 55, null, null, DateTimeOffset.UtcNow),
                    DownloadBytesPerSecond: 0,
                    UploadBytesPerSecond: 0,
                    SystemDriveName: "C:\\",
                    SystemDriveUsagePercent: 20)
                {
                    CpuPowerWatts = double.PositiveInfinity
                };

                window.UpdateSnapshot(singleGpuSnapshot);
                Assert.Equal("--° --W", cpuTemp.Text);
                Assert.Equal("0.0G", memCap.Text);
                Assert.Equal("50%", memValue.Text);
                Assert.Equal(Visibility.Collapsed, gpuGroup.Visibility);

                MediaBrush beforeThemeBrush = cpuValue.Foreground;
                ResolvedTheme theme = CreateResolvedTheme("accent-test", "#FFFF5500", null);
                window.ApplyTheme(theme);

                // ApplyTheme updates foreground immediately and must not advance GPU capability stabilization.
                Assert.NotSame(beforeThemeBrush, cpuValue.Foreground);
                Assert.Equal(Visibility.Collapsed, gpuGroup.Visibility);
            }
            finally
            {
                window.RequestClose();
            }
        });
    }

    private static ResolvedTheme CreateResolvedTheme(
        string id,
        string bandTextColor,
        string? bandBackgroundPath) =>
        new(
            new ThemeIdentity(id, "Test Theme", "Tests", "1.0.0", "1.0.0"),
            new ThemeDefinition(
                new ThemePalette("#101014", "#18181D", "#F4F4F6", "#A0A0A8", "#707078", "#2E2E36", "#24242B", "#4BAEF4"),
                new ThemeMetricPalette("#4BAEF4", "#34C759", "#AF52DE", "#FFB340", "#FF6961"),
                new ThemeShape(8, 0.25),
                new ThemeBandStyle("#18181D", 6, bandTextColor, "#66FFFFFF", null),
                null),
            Guid.NewGuid().ToString("N"),
            IsBuiltIn: false,
            PreviewPath: null,
            BandBackgroundPath: bandBackgroundPath,
            TrayIconPath: null);

    private static void CreateThemeZip(
        string packagePath,
        string id,
        string version,
        params (string Name, byte[] Content, int ExternalAttributes)[] extraEntries)
    {
        string manifestJson = $$"""
            {
              "schemaVersion": 1,
              "id": "{{id}}",
              "name": "Bugfix Test Theme",
              "author": "Tests",
              "version": {{System.Text.Json.JsonSerializer.Serialize(version)}},
              "minSysMonitorVersion": "1.0.0"
            }
            """;

        const string themeJson = """
            {
              "colors": {
                "appBackground": "#101014",
                "surface": "#18181D",
                "text": "#F4F4F6",
                "secondary": "#A0A0A8",
                "tertiary": "#707078",
                "separator": "#2E2E36",
                "control": "#24242B",
                "accent": "#4BAEF4"
              },
              "metrics": {
                "cpu": "#4BAEF4",
                "memory": "#34C759",
                "gpu": "#AF52DE",
                "warning": "#FFB340",
                "critical": "#FF6961"
              },
              "shape": {
                "groupCornerRadius": 8,
                "shadowOpacity": 0.25
              },
              "band": {
                "backgroundColor": "#18181D",
                "cornerRadius": 6
              }
            }
            """;

        using FileStream stream = File.Create(packagePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteZipEntry(archive, "manifest.json", Encoding.UTF8.GetBytes(manifestJson));
        WriteZipEntry(archive, "theme.json", Encoding.UTF8.GetBytes(themeJson));
        foreach ((string name, byte[] content, int externalAttributes) in extraEntries)
        {
            WriteZipEntry(archive, name, content, externalAttributes);
        }
    }

    private static void WriteZipEntry(
        ZipArchive archive,
        string name,
        byte[] content,
        int externalAttributes = 0)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        if (externalAttributes != 0)
        {
            entry.ExternalAttributes = externalAttributes;
        }

        using Stream output = entry.Open();
        output.Write(content);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The STA test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class TempDirectoryScope : IDisposable
    {
        internal TempDirectoryScope()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"SysMonitor.BugfixTests.{Guid.NewGuid():N}");
            ThemesPath = Path.Combine(RootPath, "Themes");
            Directory.CreateDirectory(RootPath);
        }

        internal string RootPath { get; }
        internal string ThemesPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
