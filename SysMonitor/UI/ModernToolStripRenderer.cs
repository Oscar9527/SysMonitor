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
/// Modern Windows Fluent-inspired renderer for the WinForms tray menu. Provides
/// flat fills, subtle rounded selection highlights, anti-aliased icons,
/// and clean separators matching modern Windows desktop standards.
/// </summary>
internal sealed class ModernToolStripRenderer : Forms.ToolStripProfessionalRenderer
{
    private readonly bool _dark;

    public ModernToolStripRenderer()
        : this(IsDarkTheme())
    {
    }

    private ModernToolStripRenderer(bool dark)
        : base(new Forms.ProfessionalColorTable())
    {
        _dark = dark;
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        Rectangle rect = e.ToolStrip.ClientRectangle;
        using var brush = new SolidBrush(BackgroundColor);
        e.Graphics.FillRectangle(brush, rect);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        Rectangle bounds = e.ToolStrip.ClientRectangle;
        bounds.Width = Math.Max(0, bounds.Width - 1);
        bounds.Height = Math.Max(0, bounds.Height - 1);
        using var pen = new DrawingPen(BorderColor, 1f);
        e.Graphics.DrawRectangle(pen, bounds);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not Forms.ToolStripMenuItem { Selected: true, Enabled: true })
        {
            return;
        }

        Rectangle bounds = new(DrawingPoint.Empty, e.Item.Size);
        int insetX = 4;
        int insetY = 1;
        RectangleF highlightRect = new(insetX, insetY, bounds.Width - (insetX * 2), bounds.Height - (insetY * 2));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedRectangle(highlightRect, 4);
        using var brush = new SolidBrush(HoverColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is Forms.ToolStripMenuItem { Selected: true, Enabled: true })
        {
            e.TextColor = DrawingColor.White;
        }
        else
        {
            e.TextColor = e.Item.Enabled ? ForegroundColor : DisabledColor;
        }
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        Rectangle bounds = new(DrawingPoint.Empty, e.Item.Size);
        int y = bounds.Height / 2;
        using var pen = new DrawingPen(SeparatorColor, 1f);
        e.Graphics.DrawLine(pen, bounds.Left + 10, y, bounds.Right - 10, y);
    }

    protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
    {
        if (e.Item is Forms.ToolStripMenuItem { Selected: true, Enabled: true })
        {
            e.ArrowColor = DrawingColor.White;
        }
        else
        {
            e.ArrowColor = e.Item?.Enabled != false ? SecondaryColor : DisabledColor;
        }
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        Rectangle area = e.ImageRectangle;
        if (area.Width <= 0 || area.Height <= 0)
        {
            area = new Rectangle(8, 0, 18, e.Item.Height);
        }

        bool isSelected = e.Item is Forms.ToolStripMenuItem { Selected: true, Enabled: true };
        DrawingColor checkColor = isSelected
            ? DrawingColor.White
            : (e.Item.Enabled ? AccentColor : DisabledColor);

        int centerY = area.Top + (area.Height / 2);
        DrawingPoint first = new(area.Left + 4, centerY);
        DrawingPoint second = new(area.Left + 8, centerY + 4);
        DrawingPoint third = new(area.Left + 15, centerY - 4);
        using var pen = new DrawingPen(checkColor, 1.8f)
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

    // ── Windows Fluent Theme Palette ──────────────────────────────────────

    private DrawingColor BackgroundColor => _dark
        ? DrawingColor.FromArgb(255, 32, 32, 36)     // Windows Dark Surface
        : DrawingColor.FromArgb(255, 255, 255, 255); // Windows Light Surface

    private DrawingColor HoverColor => _dark
        ? DrawingColor.FromArgb(255, 0, 120, 215)   // Fluent Accent Dark
        : DrawingColor.FromArgb(255, 0, 103, 192);   // Fluent Accent Light

    private DrawingColor AccentColor => _dark
        ? DrawingColor.FromArgb(255, 0, 120, 215)
        : DrawingColor.FromArgb(255, 0, 103, 192);

    private DrawingColor ForegroundColor => _dark
        ? DrawingColor.FromArgb(255, 243, 243, 243)
        : DrawingColor.FromArgb(255, 26, 26, 26);

    private DrawingColor SecondaryColor => _dark
        ? DrawingColor.FromArgb(255, 160, 160, 165)
        : DrawingColor.FromArgb(255, 110, 110, 115);

    private DrawingColor DisabledColor => _dark
        ? DrawingColor.FromArgb(255, 85, 85, 90)
        : DrawingColor.FromArgb(255, 180, 180, 185);

    private DrawingColor BorderColor => _dark
        ? DrawingColor.FromArgb(255, 55, 55, 60)
        : DrawingColor.FromArgb(255, 218, 220, 224);

    private DrawingColor SeparatorColor => _dark
        ? DrawingColor.FromArgb(255, 50, 50, 55)
        : DrawingColor.FromArgb(255, 230, 232, 236);

    // ── Helpers ──────────────────────────────────────────────────────────

    private static GraphicsPath CreateRoundedRectangle(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

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
            // Fallback during startup
        }

        return false;
    }
}
