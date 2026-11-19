using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SysMonitor.Models;
using SysMonitor.Services;
using SysMonitor.UI;
using Forms = System.Windows.Forms;
using MediaBrushes = System.Windows.Media.Brushes;

namespace SysMonitor.Tests;

public sealed class CrossSliceBugfixTests
{
    [Fact]
    public void TrayIconService_MetricToggle_PreservesCustomOrderAndSubMetrics()
    {
        RunSta(() =>
        {
            using var service = new TrayIconService();
            var initial = new GameOverlayMetricVisibility(
                FrameRate: true,
                Cpu: true,
                Gpu: true,
                Memory: true,
                Network: false,
                CpuPower: true,
                GpuPower: false,
                CpuFrequency: false,
                GpuClock: false,
                GpuMemory: false,
                MemoryFrequency: false,
                CpuTemperature: false,
                GpuTemperature: false,
                GpuMemoryClock: true)
            {
                Order = ["cpu", "fps", "gpu", "memory", "network"],
                CpuItemOrder = ["temp", "usage", "freq", "power"],
                GpuItemOrder = ["temp", "usage", "vram", "power"],
                MemoryItemOrder = ["usage", "freq"],
                NetworkItemOrder = ["down", "up"],
            };

            service.SetGameOverlayMetrics(initial);

            GameOverlayMetricVisibility? updated = null;
            service.GameOverlayMetricsChanged += next => updated = next;

            Forms.ToolStripMenuItem? networkItem = FindMenuItemByTag(service.ContextMenuForTests.Items, "network");
            Assert.NotNull(networkItem);
            networkItem!.Checked = true;

            Assert.NotNull(updated);
            Assert.True(updated!.Network);
            Assert.False(updated.CpuFrequency);
            Assert.False(updated.GpuClock);
            Assert.False(updated.GpuMemory);
            Assert.False(updated.MemoryFrequency);
            Assert.False(updated.CpuTemperature);
            Assert.False(updated.GpuTemperature);
            Assert.True(updated.GpuMemoryClock);
            Assert.Equal(["cpu", "fps", "gpu", "memory", "network"], updated.Order);
            Assert.Equal(["temp", "usage", "freq", "power"], updated.CpuItemOrder);
            Assert.Equal(["temp", "usage", "vram", "power"], updated.GpuItemOrder);
            Assert.Equal(["usage", "freq"], updated.MemoryItemOrder);
            Assert.Equal(["down", "up"], updated.NetworkItemOrder);
        });
    }

    [Fact]
    public void DetailWindow_SetPinned_UsesThemePinBrushes()
    {
        RunSta(() =>
        {
            var pinButton = new Button();
            var pinIcon = new TextBlock();
            var pinnedBg = new SolidColorBrush(Color.FromRgb(12, 34, 56));
            var pinnedFg = new SolidColorBrush(Color.FromRgb(78, 90, 123));
            var unpinnedFg = new SolidColorBrush(Color.FromRgb(200, 100, 50));
            pinnedBg.Freeze();
            pinnedFg.Freeze();
            unpinnedFg.Freeze();

            DetailWindow.ApplyPinBrushes(pinButton, pinIcon, true, pinnedBg, pinnedFg, unpinnedFg);

            Assert.Same(pinnedBg, pinButton.Background);
            Assert.Same(pinnedFg, pinIcon.Foreground);

            DetailWindow.ApplyPinBrushes(pinButton, pinIcon, false, pinnedBg, pinnedFg, unpinnedFg);

            Assert.Same(MediaBrushes.Transparent, pinButton.Background);
            Assert.Same(unpinnedFg, pinIcon.Foreground);
        });
    }

    [Theory]
    [InlineData("Segoe UI", "#FFFFA94D", "#FFFFD166", "#FF95D5B2", "#FFFF8E72", "#FFE4B1FF")]
    [InlineData("Segoe UI", "#FF7E57C2", "#FF1976D2", "#FF1B9A5A", "#FFD97706", "#FF0097A7")]
    [InlineData("Segoe UI Variable Text", "#FFFF8C00", "#FF00E5FF", "#FF00E676", "#FFFFD600", "#FFE040FB")]
    public void SettingsService_NormalizeOverlayAppearance_PreservesSkinsAndFont(
        string fontFamily,
        string gpuColor,
        string cpuColor,
        string fpsColor,
        string memoryColor,
        string networkColor)
    {
        var input = new GameOverlayAppearance(
            FontFamily: fontFamily,
            FontSize: 16d,
            LabelColor: gpuColor,
            ValueColor: gpuColor,
            GpuColor: gpuColor,
            CpuColor: cpuColor,
            FpsColor: fpsColor,
            MemoryColor: memoryColor,
            NetworkColor: networkColor);

        GameOverlayAppearance normalized = SettingsService.NormalizeOverlayAppearance(input);

        Assert.Equal(fontFamily, normalized.FontFamily);
        Assert.Equal(gpuColor, normalized.GpuColor);
        Assert.Equal(cpuColor, normalized.CpuColor);
        Assert.Equal(fpsColor, normalized.FpsColor);
        Assert.Equal(memoryColor, normalized.MemoryColor);
        Assert.Equal(networkColor, normalized.NetworkColor);
    }

    [Fact]
    public void SettingsService_NormalizeOverlayAppearance_DefaultsBlankFontToConsolas()
    {
        var input = new GameOverlayAppearance(FontFamily: "   ");

        GameOverlayAppearance normalized = SettingsService.NormalizeOverlayAppearance(input);

        Assert.Equal("Consolas", normalized.FontFamily);
    }

    [Fact]
    public void AppearanceSettingsWindow_LoadPreview_LoadsImageWhenPathContainsHash()
    {
        RunSta(() =>
        {
            string tempRoot = Path.Combine(
                Path.GetTempPath(),
                $"sysmon_preview_{Guid.NewGuid():N}_#hash");
            Directory.CreateDirectory(tempRoot);
            try
            {
                string imagePath = Path.Combine(tempRoot, "preview#1.png");
                var writable = BitmapSource.Create(
                    2,
                    2,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    new byte[16],
                    8);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(writable));
                using (var stream = File.Create(imagePath))
                {
                    encoder.Save(stream);
                }

                BitmapSource? loaded = AppearanceSettingsWindow.LoadPreview(imagePath);

                Assert.NotNull(loaded);
                Assert.True(loaded!.IsFrozen);
                Assert.True(loaded.PixelWidth > 0);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        });
    }

    [Fact]
    public void ProcessExecutablePathResolver_TryReadProcessIdentity_ReadsCurrentProcessAndRejectsInvalidPid()
    {
        using Process current = Process.GetCurrentProcess();
        DateTimeOffset expectedStart = new(current.StartTime.ToUniversalTime());

        Assert.True(ProcessExecutablePathResolver.TryReadProcessIdentity(
            current.Id,
            out DateTimeOffset startedAt,
            out bool hasExited));
        Assert.False(hasExited);
        Assert.NotEqual(default, startedAt);
        Assert.Equal(TimeSpan.Zero, startedAt.Offset);
        Assert.Equal(expectedStart, startedAt);

        Assert.False(ProcessExecutablePathResolver.TryReadProcessIdentity(0, out DateTimeOffset zeroStart, out bool zeroExited));
        Assert.Equal(default, zeroStart);
        Assert.True(zeroExited);

        Assert.False(ProcessExecutablePathResolver.TryReadProcessIdentity(-1, out DateTimeOffset negStart, out bool negExited));
        Assert.Equal(default, negStart);
        Assert.True(negExited);
    }

    [Fact]
    public void Win32ForegroundWindowSource_TargetIdentity_MatchesResolvedProcessTimestamp()
    {
        using Process current = Process.GetCurrentProcess();
        Assert.True(ProcessExecutablePathResolver.TryReadProcessIdentity(
            current.Id,
            out DateTimeOffset startedAt,
            out bool hasExited));
        Assert.False(hasExited);

        var first = new ForegroundTarget(
            WindowHandle: new nint(0x1234),
            ProcessId: current.Id,
            ProcessStartedAt: startedAt,
            QualifiedAt: DateTimeOffset.UtcNow);
        var second = new ForegroundTarget(
            WindowHandle: new nint(0x1234),
            ProcessId: current.Id,
            ProcessStartedAt: new DateTimeOffset(current.StartTime.ToUniversalTime()),
            QualifiedAt: DateTimeOffset.UtcNow);

        Assert.True(first.SameIdentity(second));
    }

    [Fact]
    public void HudPreviewScheduler_And_TaskbarMotionTracker_DisposeCleanlyAfterDispatcherShutdown()
    {
        RunSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            using var scheduler = new HudPreviewScheduler(dispatcher, () => { });
            using var tracker = new TaskbarMotionTracker(
                dispatcher,
                () => { },
                () => { },
                () => { },
                () => { });

            dispatcher.InvokeShutdown();

            scheduler.Request();
            scheduler.Dispose();
            tracker.Dispose();
        });
    }

    [Fact]
    public void EnvironmentBootstrap_IntegrityLevelDetection_IdentifiesLowVsMediumCorrectly()
    {
        Assert.True(EnvironmentBootstrap.IsLowIntegrityRid(0x0000));
        Assert.True(EnvironmentBootstrap.IsLowIntegrityRid(0x1000));
        Assert.False(EnvironmentBootstrap.IsLowIntegrityRid(0x2000));
        Assert.False(EnvironmentBootstrap.IsLowIntegrityRid(0x3000));

        Assert.True(EnvironmentBootstrap.TryGetCurrentProcessIntegrityRid(out uint rid));
        Assert.True(rid >= 0x2000u);
        Assert.False(EnvironmentBootstrap.IsRunningAtLowIntegrity());
    }

    [Fact]
    public void BandWindow_ApplyAppearance_SetsTightBlockLineHeightAndHoverBrush()
    {
        RunSta(() =>
        {
            var band = new BandWindow(generation: 1);
            try
            {
                band.ApplyAppearance(new BandAppearanceSettings("Segoe UI Variable Text", 13, 1, 0, 98));
                var cpuValue = Assert.IsType<TextBlock>(band.FindName("CpuValueText"));
                var cpuTemp = Assert.IsType<TextBlock>(band.FindName("CpuTemperatureText"));

                Assert.Equal(System.Windows.LineStackingStrategy.BlockLineHeight, cpuValue.LineStackingStrategy);
                Assert.Equal(System.Windows.LineStackingStrategy.BlockLineHeight, cpuTemp.LineStackingStrategy);
                Assert.True(cpuValue.LineHeight + cpuTemp.LineHeight <= 30d);
                Assert.NotNull(band.Resources["BandHoverBrush"]);
            }
            finally
            {
                band.RequestClose();
            }
        });
    }

    private static Forms.ToolStripMenuItem? FindMenuItemByTag(Forms.ToolStripItemCollection items, string tag)
    {
        foreach (Forms.ToolStripItem item in items)
        {
            if (item is Forms.ToolStripMenuItem menuItem)
            {
                if (string.Equals(menuItem.Tag as string, tag, StringComparison.Ordinal))
                {
                    return menuItem;
                }

                Forms.ToolStripMenuItem? nested = FindMenuItemByTag(menuItem.DropDownItems, tag);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
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
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The cross-slice bugfix test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
