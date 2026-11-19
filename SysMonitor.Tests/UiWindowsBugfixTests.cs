using System.Globalization;
using System.Resources;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using SysMonitor.Models;
using SysMonitor.Services;
using SysMonitor.UI;

namespace SysMonitor.Tests;

[Collection(LocalizationCollection.Name)]
public sealed class UiWindowsBugfixTests
{
    [Fact]
    public void LocalizationResources_ContainHudSettingsSaveFailedKeys()
    {
        var manager = new ResourceManager(
            "SysMonitor.Resources.Strings",
            typeof(LocalizationService).Assembly);

        ResourceSet english = Assert.IsAssignableFrom<ResourceSet>(
            manager.GetResourceSet(CultureInfo.GetCultureInfo("en-US"), true, true));
        ResourceSet chinese = Assert.IsAssignableFrom<ResourceSet>(
            manager.GetResourceSet(CultureInfo.GetCultureInfo("zh-CN"), true, false));

        Assert.False(string.IsNullOrWhiteSpace(english.GetString("HudSettingsSaveFailedTitle")));
        Assert.False(string.IsNullOrWhiteSpace(english.GetString("HudSettingsSaveFailedMessage")));
        Assert.False(string.IsNullOrWhiteSpace(chinese.GetString("HudSettingsSaveFailedTitle")));
        Assert.False(string.IsNullOrWhiteSpace(chinese.GetString("HudSettingsSaveFailedMessage")));
    }

    [Fact]
    public void DetailWindow_PowerFormatting_UsesActiveCultureInsteadOfThreadCulture()
    {
        LocalizationService localization = LocalizationService.Current;
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            localization.ApplyCulture("en-US");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            string cpu = DetailWindow.BuildCpuDetails(8, 55, 45.6);
            string gpu = DetailWindow.BuildGpuDetails(new GpuSnapshot(
                0,
                "Test GPU",
                50,
                62,
                2L * 1024 * 1024 * 1024,
                4L * 1024 * 1024 * 1024,
                DateTimeOffset.UtcNow)
            {
                PowerWatts = 120.5,
            });

            Assert.Contains("45.6 W", cpu);
            Assert.Contains("120.5 W", gpu);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            localization.ApplyCulture("system");
        }
    }

    [Fact]
    public void GameOverlayWindow_BuildHorizontalMetricValue_UsesActiveCulture()
    {
        LocalizationService localization = LocalizationService.Current;
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            localization.ApplyCulture("en-US");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            string text = GameOverlayWindow.BuildHorizontalMetricValue("42%", "71°C", 65.4);

            Assert.Equal("42%  71°C  65.4 W", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            localization.ApplyCulture("system");
        }
    }

    [Fact]
    public void GameOverlayWindow_UpdateMetrics_IgnoresNonFiniteNetworkRates()
    {
        RunSta(() =>
        {
            var window = new GameOverlayWindow();
            try
            {
                MonitorSnapshot snapshot = MonitorSnapshot.Empty with
                {
                    DownloadBytesPerSecond = double.NaN,
                    UploadBytesPerSecond = double.PositiveInfinity,
                };
                var frame = new GameOverlayFrameSnapshot(
                    null,
                    GameOverlayFrameStatus.Unavailable,
                    DateTimeOffset.UtcNow);

                window.UpdateMetrics(snapshot, frame);

                var networkValue = Assert.IsType<TextBlock>(window.FindName("NetworkValue"));
                Assert.Equal("\u2193 0  \u2191 0", networkValue.Text);
            }
            finally
            {
                window.Close();
            }
        });
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The UI bugfix test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
