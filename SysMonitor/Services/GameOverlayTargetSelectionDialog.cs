using System.Drawing;
using Forms = System.Windows.Forms;

namespace SysMonitor.Services;

internal static class GameOverlayTargetSelectionDialog
{
    public static GameOverlayTargetOption? Show()
    {
        IReadOnlyList<GameOverlayTargetOption> targets = GameOverlayTargetCatalog.Enumerate();
        if (targets.Count == 0)
        {
            Forms.MessageBox.Show(
                "没有找到可绑定的窗口程序。请先打开游戏或应用，再从托盘选择。",
                "SysMonitor",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Information);
            return null;
        }

        using var dialog = new Forms.Form
        {
            Text = "选择帧率监控目标",
            StartPosition = Forms.FormStartPosition.CenterScreen,
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(560, 150),
            TopMost = true
        };
        var label = new Forms.Label
        {
            AutoSize = true,
            Location = new Point(16, 18),
            Text = "选择要显示帧率的程序（不要求被识别为游戏）："
        };
        var combo = new Forms.ComboBox
        {
            DropDownStyle = Forms.ComboBoxStyle.DropDownList,
            Location = new Point(16, 48),
            Width = 528,
            DisplayMember = nameof(TargetItem.Display)
        };
        combo.Items.AddRange(targets.Select(static target => new TargetItem(target)).ToArray());
        combo.SelectedIndex = 0;
        var ok = new Forms.Button
        {
            Text = "确定",
            DialogResult = Forms.DialogResult.OK,
            Location = new Point(388, 96),
            Width = 74
        };
        var cancel = new Forms.Button
        {
            Text = "取消",
            DialogResult = Forms.DialogResult.Cancel,
            Location = new Point(470, 96),
            Width = 74
        };
        dialog.Controls.AddRange([label, combo, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog() == Forms.DialogResult.OK &&
            combo.SelectedItem is TargetItem item
                ? item.Target
                : null;
    }

    private sealed record TargetItem(GameOverlayTargetOption Target)
    {
        public string Display => GameOverlayTargetCatalog.BuildDisplayName(Target);
        public override string ToString() => Display;
    }
}
