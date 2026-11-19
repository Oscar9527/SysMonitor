using System.Globalization;
using System.IO;
using SysMonitor.Models;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class AppCoreBugfixTests
{
    [Fact]
    public void ResolveWindowsDirectory_UsesParentOfSystemDirectory()
    {
        Assert.Equal(
            @"C:\Windows",
            EnvironmentBootstrap.ResolveWindowsDirectory(@"C:\Windows\System32"));
        Assert.Equal(
            @"D:\WinNT",
            EnvironmentBootstrap.ResolveWindowsDirectory(@"D:\WinNT\System32\"));
    }

    [Fact]
    public void ResolveWindowsDirectory_FallsBackWhenSystemDirectoryHasNoParent()
    {
        Assert.Equal(
            @"D:\CustomWindows",
            EnvironmentBootstrap.ResolveWindowsDirectory(@"C:\", @"D:\CustomWindows\"));
        Assert.Equal(
            @"C:\Windows",
            EnvironmentBootstrap.ResolveWindowsDirectory(null, null));
    }

    [Fact]
    public void MetricHistoryBuffer_ThrowsArgumentOutOfRangeAtInt64DoubleBoundary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MetricHistoryBuffer(long.MaxValue, TimeSpan.FromSeconds(1), 1));
    }

    [Fact]
    public void MetricHistoryBuffer_AllowsLargestRepresentableDoubleBelowInt64Boundary()
    {
        var buffer = new MetricHistoryBuffer(9223372036854774784L, TimeSpan.FromSeconds(1), 1);

        Assert.True(buffer.TryAdd(new MetricHistoryPoint(1, 1, 100, 10, 20)));
        Assert.Single(buffer.Snapshot());
    }

    [Fact]
    public void NormalizeOverlayAppearance_ScRgbColorIsCultureInvariant()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            GameOverlayAppearance normalized = SettingsService.NormalizeOverlayAppearance(
                new GameOverlayAppearance(OutlineColor: "sc#1, 0.5, 0, 0"));

            Assert.Equal("sc#1, 0.5, 0, 0", normalized.OutlineColor);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void NormalizeOverlayAppearance_WhitespaceColorFallsBackToDefault()
    {
        GameOverlayAppearance normalized = SettingsService.NormalizeOverlayAppearance(
            new GameOverlayAppearance(OutlineColor: "   ", GpuColor: string.Empty));

        Assert.Equal("#FF000000", normalized.OutlineColor);
        Assert.Equal("#FFFF8C00", normalized.GpuColor);
    }

    [Fact]
    public void TryPatch_RefreshesExternalRevisionWhileSharedReaderWriterHandleIsOpen()
    {
        using var directory = new TempSettingsDirectory();
        var first = new SettingsService(directory.Path);
        var second = new SettingsService(directory.Path);

        Assert.True(first.TryPatch(settings => settings.PanelTopmost = false, out SettingsSnapshot initial));
        Assert.Equal(initial.Revision, second.Revision);
        Assert.True(first.TryPatch(initial.Revision, settings => settings.PanelTopmost = true, out SettingsSnapshot external));

        using (new FileStream(
                   first.SettingsPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            bool patched = second.TryPatch(
                initial.Revision,
                settings => settings.BandFontSize = 18,
                out SettingsSnapshot conflict);

            Assert.False(patched);
            Assert.Equal(external.Revision, conflict.Revision);
            Assert.True(conflict.Settings.PanelTopmost);
        }
    }

    [Fact]
    public void ChildProcessJob_ValidatesNullAndIdempotentDispose()
    {
        var job = new ChildProcessJob();
        Assert.Throws<ArgumentNullException>(() => job.Assign(null!));

        job.Dispose();
        job.Dispose();

        Assert.Throws<ObjectDisposedException>(() => job.Assign(null!));
    }

    private sealed class TempSettingsDirectory : IDisposable
    {
        public TempSettingsDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SysMonitor.AppCoreTests.{Guid.NewGuid():N}");
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
