using DrawingColor = System.Drawing.Color;
using DrawingPen = System.Drawing.Pen;
using DrawingPoint = System.Drawing.Point;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        Rectangle bounds = new(DrawingPoint.Empty, e.Item.Size);
        int y = bounds.Height / 2;
        using var pen = new DrawingPen(BorderColor);
        e.Graphics.DrawLine(pen, bounds.Left + 8, y, bounds.Right - 8, y);
    }

    protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled != false ? ForegroundColor : SecondaryColor;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        Rectangle area = e.ImageRectangle;
        if (area.Width <= 0 || area.Height <= 0)
        {
            area = new Rectangle(8, 0, 18, e.Item.Height);
        }

        int centerY = area.Top + (area.Height / 2);
        DrawingPoint first = new(area.Left + 3, centerY);
        DrawingPoint second = new(area.Left + 7, centerY + 4);
        DrawingPoint third = new(area.Left + 15, centerY - 5);
        using var pen = new DrawingPen(e.Item.Enabled ? ForegroundColor : SecondaryColor, 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, new DrawingPoint[] { first, second, third });
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
