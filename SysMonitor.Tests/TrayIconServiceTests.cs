using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Threading;
using SysMonitor.Services;
using Forms = System.Windows.Forms;

namespace SysMonitor.Tests;

public sealed class TrayIconServiceTests
{
    [Theory]
    [InlineData(false, true, "TrayShowGameOverlay")]
    [InlineData(true, true, "TrayHideGameOverlay")]
    [InlineData(false, false, "TrayGameOverlayUnavailableCompatibility")]
    [InlineData(true, false, "TrayGameOverlayUnavailableCompatibility")]
    public void GameOverlayText_ReflectsVisibilityAndCompatibilityAvailability(
        bool visible,
        bool available,
        string expected)
    {
        Assert.Equal(expected, TrayIconService.GetGameOverlayResourceKey(visible, available));
    }

    [Theory]
    [InlineData(1.00f)]
    [InlineData(1.25f)]
    [InlineData(1.50f)]
    [InlineData(1.75f)]
    [InlineData(2.00f)]
    public void MenuLayout_FitsLocalizedTextChecksAndShortcutAtCommonDpiScales(float scale)
    {
        RunSta(() =>
        {
            using var menu = new Forms.ContextMenuStrip
            {
                Font = new Font("Segoe UI", 9f * scale, FontStyle.Regular, GraphicsUnit.Point),
                ShowImageMargin = false,
                ShowCheckMargin = true
            };
            var overlay = new Forms.ToolStripMenuItem("显示游戏叠加层")
            {
                ShortcutKeyDisplayString = "Ctrl+Shift+F10",
                Checked = true
            };
            var advanced = new Forms.ToolStripMenuItem("游戏叠加层高级设置");
            advanced.DropDownItems.AddRange(
            [
                new Forms.ToolStripMenuItem("Frame-rate assistant position"),
                new Forms.ToolStripMenuItem("监控项目与采样频率设置") { Checked = true }
            ]);
            menu.Items.AddRange([overlay, advanced]);

            TrayIconService.ConfigureMenuItems(menu.Items);
            var workArea = new Rectangle(0, 0, 1920, 1040);
            TrayIconService.PrepareDropDownLayout(menu, workArea);
            TrayIconService.PrepareDropDownLayout(advanced.DropDown, workArea);

            int rootColumnsWidth = Forms.TextRenderer.MeasureText(overlay.Text, menu.Font).Width +
                Forms.TextRenderer.MeasureText(overlay.ShortcutKeyDisplayString, menu.Font).Width + 64;
            int childColumnsWidth = advanced.DropDownItems.Cast<Forms.ToolStripItem>()
                .Max(item => Forms.TextRenderer.MeasureText(item.Text, menu.Font).Width) + 48;
            Assert.True(menu.MinimumSize.Width >= rootColumnsWidth);
            Assert.True(advanced.DropDown.MinimumSize.Width >= childColumnsWidth);
            Assert.True(menu.MaximumSize.Width <= workArea.Width - 16);
            Assert.True(menu.MaximumSize.Height <= workArea.Height - 16);
            Assert.True(advanced.DropDown.MaximumSize.Height <= workArea.Height - 16);
            Assert.True(overlay.AutoSize);
            Assert.Equal(6, overlay.Padding.Top);
        });
    }

    [Fact]
    public void MenuLayout_CapsHeightSoOverflowRemainsInsideWorkingArea()
    {
        RunSta(() =>
        {
            using var menu = new Forms.ContextMenuStrip();
            for (int index = 0; index < 40; index++)
            {
                menu.Items.Add(new Forms.ToolStripMenuItem($"项目 {index + 1}"));
            }

            TrayIconService.ConfigureMenuItems(menu.Items);
            TrayIconService.PrepareDropDownLayout(menu, new Rectangle(0, 0, 800, 320));

            Assert.Equal(304, menu.MaximumSize.Height);
            Assert.True(menu.GetPreferredSize(menu.MaximumSize).Height <= menu.MaximumSize.Height);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The tray menu test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
