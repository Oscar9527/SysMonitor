using DrawingColor = System.Drawing.Color;
using DrawingPen = System.Drawing.Pen;
using DrawingPoint = System.Drawing.Point;
using System.Drawing;
using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;

namespace SysMonitor.UI;

/// <summary>
/// Lightweight macOS-inspired renderer for the WinForms tray menu. It deliberately
/// uses flat fills and a one-pixel separator so keyboard navigation and the native
/// ToolStrip menu behavior remain intact.
/// </summary>
internal sealed class MacToolStripRenderer : Forms.ToolStripProfessionalRenderer
{
    private readonly bool _dark;

    public MacToolStripRenderer()
        : this(IsDarkTheme())
    {
    }

    private MacToolStripRenderer(bool dark)
        : base(new Forms.ProfessionalColorTable())
    {
        _dark = dark;
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(BackgroundColor);
        e.Graphics.FillRectangle(brush, e.ToolStrip.ClientRectangle);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        Rectangle bounds = e.ToolStrip.ClientRectangle;
        bounds.Width--;
        bounds.Height--;
        using var pen = new DrawingPen(BorderColor);
        e.Graphics.DrawRectangle(pen, bounds);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        Rectangle bounds = new(DrawingPoint.Empty, e.Item.Size);
        DrawingColor fill = e.Item is Forms.ToolStripMenuItem { Selected: true, Enabled: true }
            ? HoverColor
            : BackgroundColor;
        using var brush = new SolidBrush(fill);
        e.Graphics.FillRectangle(brush, bounds);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? ForegroundColor : SecondaryColor;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        Rectangle bounds = e.Item.Bounds;
        int y = bounds.Top + (bounds.Height / 2);
        using var pen = new DrawingPen(BorderColor);
        e.Graphics.DrawLine(pen, bounds.Left + 8, y, bounds.Right - 8, y);
    }

    protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(BackgroundColor);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    private DrawingColor BackgroundColor => _dark
        ? DrawingColor.FromArgb(255, 32, 33, 37)
        : DrawingColor.FromArgb(255, 255, 255, 255);

    private DrawingColor HoverColor => _dark
        ? DrawingColor.FromArgb(255, 55, 56, 63)
        : DrawingColor.FromArgb(255, 245, 246, 248);

    private DrawingColor ForegroundColor => _dark
        ? DrawingColor.FromArgb(255, 245, 245, 247)
        : DrawingColor.FromArgb(255, 29, 29, 31);

    private DrawingColor SecondaryColor => _dark
        ? DrawingColor.FromArgb(255, 184, 187, 194)
        : DrawingColor.FromArgb(255, 98, 102, 110);

    private DrawingColor BorderColor => _dark
        ? DrawingColor.FromArgb(255, 69, 71, 79)
        : DrawingColor.FromArgb(255, 217, 220, 226);

    private static bool IsDarkTheme()
    {
        if (SystemParameters.HighContrast)
        {
            return false;
        }

        try
        {
            if (System.Windows.Application.Current?.TryFindResource("AppBackgroundBrush") is SolidColorBrush brush)
            {
                MediaColor color = brush.Color;
                return (color.R * 299 + color.G * 587 + color.B * 114) < 128000;
            }
        }
        catch
        {
            // Use the light palette when WPF resources are unavailable during startup.
        }

        return false;
    }
}
