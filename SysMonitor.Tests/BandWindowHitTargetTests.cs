using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysMonitor.UI;

namespace SysMonitor.Tests;

public sealed class BandWindowHitTargetTests
{
    [Fact]
    public void OnlyLeftButtonDownIsAToggleMessage()
    {
        Assert.True(BandWindow.IsToggleMessage(0x0201));
        Assert.False(BandWindow.IsToggleMessage(0x0202));
    }

    [Fact]
    public void HitTargetUsesThemeIndependentAlphaOneBackground()
    {
        RunSta(() =>
        {
            var window = new BandWindow(generation: 1);
            var hitTarget = Assert.IsType<Grid>(window.FindName("BandHitTarget"));
            var bandRoot = Assert.IsType<Border>(window.FindName("BandRoot"));
            var hitBrush = Assert.IsType<SolidColorBrush>(hitTarget.Background);

            Assert.Equal(Color.FromArgb(1, 0, 0, 0), hitBrush.Color);
            Assert.Equal(
                BaseValueSource.Local,
                DependencyPropertyHelper.GetValueSource(
                    hitTarget,
                    Panel.BackgroundProperty).BaseValueSource);

            var themedBrush = new SolidColorBrush(Colors.Magenta);
            window.Resources["BandBackgroundBrush"] = themedBrush;

            Assert.Same(themedBrush, bandRoot.Background);
            Assert.Equal(Color.FromArgb(1, 0, 0, 0), hitBrush.Color);
            window.RequestClose();
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The Band WPF test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
