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
    private readonly Icon _defaultIcon;
    private Icon? _themedIcon;
    private bool _syncingState;
    private bool _panelVisible;
    private bool _disposed;

    public TrayIconService()
    {
        _defaultIcon = CreateIcon();

        _panelItem = new Forms.ToolStripMenuItem();
        _panelItem.Click += OnPanelItemClick;

        _appearanceItem = new Forms.ToolStripMenuItem();
        _appearanceItem.Click += OnAppearanceItemClick;

        _pinItem = new Forms.ToolStripMenuItem { CheckOnClick = true };
        _pinItem.CheckedChanged += OnPinCheckedChanged;

        _startupItem = new Forms.ToolStripMenuItem { CheckOnClick = true };
        _startupItem.CheckedChanged += OnStartupCheckedChanged;

        _exitItem = new Forms.ToolStripMenuItem();
        _exitItem.Click += OnExitItemClick;

        _contextMenu = new Forms.ContextMenuStrip();
        _contextMenu.Items.AddRange(
        [
            _panelItem,
            _appearanceItem,
            new Forms.ToolStripSeparator(),
            _pinItem,
            _startupItem,
            new Forms.ToolStripSeparator(),
            _exitItem
        ]);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _defaultIcon,
            Text = "SysMonitor",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };
        _notifyIcon.MouseUp += OnNotifyIconMouseUp;
        LocalizationService.Current.CultureChanged += OnCultureChanged;
        ApplyLocalizedText();
    }

    public event EventHandler? ToggleDetailsRequested;
    public event EventHandler? AppearanceSettingsRequested;
    public event Action<bool>? PinToggled;
    public event Action<bool>? StartupToggled;
    public event EventHandler? ExitRequested;

    public void SetPanelVisible(bool visible)
    {
        ThrowIfDisposed();
        _panelVisible = visible;
        SetPanelItemText();
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

    public bool ApplyThemeIcon(string? iconPath)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            UseDefaultIcon();
            return true;
        }

        Icon? replacement = null;
        try
        {
            using var stream = new FileStream(
                iconPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var loaded = new Icon(stream);
            replacement = (Icon)loaded.Clone();
            Icon? oldThemed = _themedIcon;
            _notifyIcon.Icon = replacement;
            _themedIcon = replacement;
            replacement = null;
            oldThemed?.Dispose();
            return true;
        }
        catch
        {
            replacement?.Dispose();
            UseDefaultIcon();
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LocalizationService.Current.CultureChanged -= OnCultureChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.MouseUp -= OnNotifyIconMouseUp;
        _panelItem.Click -= OnPanelItemClick;
        _appearanceItem.Click -= OnAppearanceItemClick;
        _pinItem.CheckedChanged -= OnPinCheckedChanged;
        _startupItem.CheckedChanged -= OnStartupCheckedChanged;
        _exitItem.Click -= OnExitItemClick;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _themedIcon?.Dispose();
        _themedIcon = null;
        _defaultIcon.Dispose();
    }

    internal void ApplyLocalizedText()
    {
        if (_disposed)
        {
            return;
        }

        LocalizationService localization = LocalizationService.Current;
        SetPanelItemText();
        _appearanceItem.Text = localization.GetString("TrayAppearance");
        _pinItem.Text = localization.GetString("TrayPin");
        _startupItem.Text = localization.GetString("TrayStartup");
        _exitItem.Text = localization.GetString("TrayExit");
    }

    private void SetPanelItemText() =>
        _panelItem.Text = LocalizationService.Current.GetString(
            _panelVisible ? "TrayHidePanel" : "TrayShowPanel");

    private void OnCultureChanged(object? sender, EventArgs e) => ApplyLocalizedText();

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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void UseDefaultIcon()
    {
        Icon? oldThemed = _themedIcon;
        _notifyIcon.Icon = _defaultIcon;
        _themedIcon = null;
        oldThemed?.Dispose();
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
        [
            new(6, 17), new(10, 17), new(12.5f, 10), new(16, 23),
            new(19, 14), new(21.5f, 17), new(26, 17)
        ];
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
