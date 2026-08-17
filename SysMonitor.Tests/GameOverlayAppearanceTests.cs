using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SysMonitor.Models;
using SysMonitor.UI;

namespace SysMonitor.Tests;

public sealed class GameOverlayAppearanceTests
{
    [Fact]
    public void HudHasNoBlackSurfaceAndUsesConfiguredTextOutline()
    {
        RunSta(() =>
        {
            var window = new GameOverlayWindow();
            window.SetAppearance(new GameOverlayAppearance(
                OutlineColor: "#FF102030",
                OutlineThickness: 2d,
                ShadowOpacity: 0.7d,
                ShadowDepth: 1.5d));

            var surface = Assert.IsType<Border>(window.FindName("OverlaySurface"));
            var background = Assert.IsType<SolidColorBrush>(surface.Background);
            Assert.Equal(0, background.Color.A);
            Assert.Equal(new Thickness(0), surface.BorderThickness);

            var overlayGrid = Assert.IsType<Grid>(window.FindName("OverlayGrid"));
            var effect = Assert.IsType<DropShadowEffect>(overlayGrid.Effect);
            Assert.Equal(Color.FromRgb(0x10, 0x20, 0x30), effect.Color);
            Assert.Equal(5d, effect.BlurRadius);
            Assert.Equal(0.7d, effect.Opacity);
            Assert.Equal(1.5d, effect.ShadowDepth);
            window.Close();
        });
    }

    [Fact]
    public void HorizontalHudHonorsMemorySelectionAndDoesNotForceCpuOrGpu()
    {
        RunSta(() =>
        {
            var window = new GameOverlayWindow();
            window.SetLayoutMode("horizontal");
            window.SetLayout(
                "rivatuner",
                new GameOverlayMetricVisibility(
                    FrameRate: false,
                    Cpu: false,
                    Gpu: false,
                    Memory: true,
                    Network: false));

            Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(window.FindName("MemoryValue")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(window.FindName("CpuValue")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(window.FindName("GpuValue")).Visibility);
            window.Close();
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The HUD appearance test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
