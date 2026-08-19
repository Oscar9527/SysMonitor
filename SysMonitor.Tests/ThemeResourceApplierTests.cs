using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class ThemeResourceApplierTests
{
    [Fact]
    public void BuiltInThemesApplyIdempotentlyAndRestoreDefaultResources()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application();
                var catalog = new ThemeCatalogService(
                    Path.Combine(Path.GetTempPath(), $"SysMonitor.ThemeApply.{Guid.NewGuid():N}"),
                    new Version(1, 3, 0));
                var applier = new ThemeResourceApplier();

                Assert.True(applier.Apply(catalog.ResolveOrDefault(ThemeCatalogService.DefaultThemeId)));
                Assert.False(applier.Apply(catalog.ResolveOrDefault(ThemeCatalogService.DefaultThemeId)));
                Assert.Equal(
                    Color.FromRgb(0xF5, 0xF6, 0xF8),
                    Assert.IsType<SolidColorBrush>(application.Resources["AppBackgroundBrush"]).Color);

                Assert.True(applier.Apply(catalog.ResolveOrDefault(ThemeCatalogService.MidnightThemeId)));
                Assert.Equal(
                    Color.FromRgb(0x17, 0x18, 0x1B),
                    Assert.IsType<SolidColorBrush>(application.Resources["AppBackgroundBrush"]).Color);

                Assert.True(applier.Apply(catalog.ResolveOrDefault(ThemeCatalogService.DefaultThemeId)));
                Assert.Equal(
                    Color.FromRgb(0xF0, 0xF1, 0xF4),
                    Assert.IsType<SolidColorBrush>(application.Resources["MetricTrackBrush"]).Color);
                Assert.Equal(
                    new CornerRadius(10),
                    Assert.IsType<CornerRadius>(application.Resources["AppGroupCornerRadius"]));
                application.Shutdown();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The WPF theme test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
