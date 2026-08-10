using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace SysMonitor.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _contextMenu;
    private readonly Forms.ToolStripMenuItem _panelItem;
    private readonly Forms.ToolStripMenuItem _appearanceItem;
    private readonly Forms.ToolStripMenuItem _pinItem;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly Icon _icon;
    private bool _syncingState;
    private bool _disposed;

    public TrayIconService()
    {
        _icon = CreateIcon();

        _panelItem = new Forms.ToolStripMenuItem("显示面板");
        _panelItem.Click += OnPanelItemClick;

        _appearanceItem = new Forms.ToolStripMenuItem("任务栏外观…");
        _appearanceItem.Click += OnAppearanceItemClick;

        _pinItem = new Forms.ToolStripMenuItem("窗口置顶")
        {
            CheckOnClick = true
        };
        _pinItem.CheckedChanged += OnPinCheckedChanged;

        _startupItem = new Forms.ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true
        };
        _startupItem.CheckedChanged += OnStartupCheckedChanged;

        _exitItem = new Forms.ToolStripMenuItem("退出");
        _exitItem.Click += OnExitItemClick;

        _contextMenu = new Forms.ContextMenuStrip();
        _contextMenu.Items.AddRange(
        new Forms.ToolStripItem[]
        {
            _panelItem,
            _appearanceItem,
            new Forms.ToolStripSeparator(),
            _pinItem,
            _startupItem,
            new Forms.ToolStripSeparator(),
            _exitItem
        });

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "SysMonitor",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };
        _notifyIcon.MouseUp += OnNotifyIconMouseUp;
    }

    public event EventHandler? ToggleDetailsRequested;

    public event EventHandler? AppearanceSettingsRequested;

    public event Action<bool>? PinToggled;

    public event Action<bool>? StartupToggled;

    public event EventHandler? ExitRequested;

    public void SetPanelVisible(bool visible)
    {
        ThrowIfDisposed();
        _panelItem.Text = visible ? "隐藏面板" : "显示面板";
    }

    public void SetPinned(bool pinned)
    {
        ThrowIfDisposed();
        SetCheckedWithoutNotification(_pinItem, pinned);
    }

    public void SetStartupEnabled(bool enabled)
    {
        ThrowIfDisposed();
        SetCheckedWithoutNotification(_startupItem, enabled);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.MouseUp -= OnNotifyIconMouseUp;
        _panelItem.Click -= OnPanelItemClick;
        _appearanceItem.Click -= OnAppearanceItemClick;
        _pinItem.CheckedChanged -= OnPinCheckedChanged;
        _startupItem.CheckedChanged -= OnStartupCheckedChanged;
        _exitItem.Click -= OnExitItemClick;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _icon.Dispose();
    }

    private void OnNotifyIconMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            ToggleDetailsRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPanelItemClick(object? sender, EventArgs e) =>
        ToggleDetailsRequested?.Invoke(this, EventArgs.Empty);

    private void OnAppearanceItemClick(object? sender, EventArgs e) =>
        AppearanceSettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnPinCheckedChanged(object? sender, EventArgs e)
    {
        if (!_syncingState)
        {
            PinToggled?.Invoke(_pinItem.Checked);
        }
    }

    private void OnStartupCheckedChanged(object? sender, EventArgs e)
    {
        if (!_syncingState)
        {
            StartupToggled?.Invoke(_startupItem.Checked);
        }
    }

    private void OnExitItemClick(object? sender, EventArgs e) =>
        ExitRequested?.Invoke(this, EventArgs.Empty);

    private void SetCheckedWithoutNotification(Forms.ToolStripMenuItem item, bool value)
    {
        if (item.Checked == value)
        {
            return;
        }

        _syncingState = true;
        try
        {
            item.Checked = value;
        }
        finally
        {
            _syncingState = false;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static Icon CreateIcon()
    {
        try
        {
            Uri resourceUri = new("pack://application:,,,/Assets/sysmonitor.ico", UriKind.Absolute);
            System.Windows.Resources.StreamResourceInfo? resource =
                System.Windows.Application.GetResourceStream(resourceUri);
            if (resource is not null)
            {
                using Stream stream = resource.Stream;
                using var icon = new Icon(stream);
                return (Icon)icon.Clone();
            }
        }
        catch
        {
            // Keep the tray icon available if the resource cannot be loaded.
        }

        return CreateFallbackIcon();
    }

    private static Icon CreateFallbackIcon()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using GraphicsPath backgroundPath = CreateRoundedRectangle(new RectangleF(2, 2, 28, 28), 7);
        using var backgroundBrush = new SolidBrush(Color.FromArgb(255, 22, 24, 29));
        using var borderPen = new Pen(Color.FromArgb(255, 52, 59, 70), 1.2f);
        graphics.FillPath(backgroundBrush, backgroundPath);
        graphics.DrawPath(borderPen, backgroundPath);

        PointF[] waveform =
        {
            new(6, 17),
            new(10, 17),
            new(12.5f, 10),
            new(16, 23),
            new(19, 14),
            new(21.5f, 17),
            new(26, 17)
        };
        using var waveformPen = new Pen(Color.FromArgb(255, 56, 217, 197), 2.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawLines(waveformPen, waveform);

        nint iconHandle = bitmap.GetHicon();
        try
        {
            using Icon temporaryIcon = Icon.FromHandle(iconHandle);
            return (Icon)temporaryIcon.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF rectangle, float radius)
    {
        float diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);
}
