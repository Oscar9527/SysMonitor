using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SysMonitor.Models;
using SysMonitor.UI;
using Forms = System.Windows.Forms;

namespace SysMonitor.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _contextMenu;
    private readonly Forms.ToolStripMenuItem _panelItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayTargetItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayPositionItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayPresetItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayMetricsItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayAppearanceItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayConfigurationItem;
    private readonly Forms.ToolStripMenuItem _gameOverlaySettingsItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayFpsMetricItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayCpuMetricItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayGpuMetricItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayMemoryMetricItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayNetworkMetricItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayCompactItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayRivatunerItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayDetailedItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayLeftItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayCenterItem;
    private readonly Forms.ToolStripMenuItem _gameOverlayRightItem;
    private readonly Forms.ToolStripMenuItem _appearanceItem;
    private readonly Forms.ToolStripMenuItem _gameSafeModeItem;
    private readonly Forms.ToolStripMenuItem _pinItem;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly Icon _defaultIcon;
    private Icon? _themedIcon;
    private bool _syncingState;
    private bool _panelVisible;
    private bool _gameOverlayVisible;
    private bool _gameOverlayAvailable = true;
    private bool _disposed;

    public TrayIconService()
    {
        _defaultIcon = CreateIcon();

        _panelItem = new Forms.ToolStripMenuItem();
        _panelItem.Click += OnPanelItemClick;

        _gameOverlayItem = new Forms.ToolStripMenuItem();
        _gameOverlayItem.Click += OnGameOverlayItemClick;

        _gameOverlayTargetItem = new Forms.ToolStripMenuItem();
        _gameOverlayTargetItem.Click += OnGameOverlayTargetItemClick;

        _gameOverlayPositionItem = new Forms.ToolStripMenuItem();
        _gameOverlayLeftItem = CreateOverlayPositionItem(0);
        _gameOverlayCenterItem = CreateOverlayPositionItem(50);
        _gameOverlayRightItem = CreateOverlayPositionItem(100);
        _gameOverlayPositionItem.DropDownItems.AddRange(
        [
            _gameOverlayLeftItem,
            _gameOverlayCenterItem,
            _gameOverlayRightItem
        ]);

        _gameOverlayPresetItem = new Forms.ToolStripMenuItem();
        _gameOverlayCompactItem = CreateOverlayPresetItem("compact");
        _gameOverlayRivatunerItem = CreateOverlayPresetItem("rivatuner");
        _gameOverlayDetailedItem = CreateOverlayPresetItem("detailed");
        _gameOverlayPresetItem.DropDownItems.AddRange(
        [ _gameOverlayCompactItem, _gameOverlayRivatunerItem, _gameOverlayDetailedItem ]);

        _gameOverlayMetricsItem = new Forms.ToolStripMenuItem();
        _gameOverlayFpsMetricItem = CreateOverlayMetricItem("fps", true);
        _gameOverlayCpuMetricItem = CreateOverlayMetricItem("cpu", true);
        _gameOverlayGpuMetricItem = CreateOverlayMetricItem("gpu", true);
        _gameOverlayMemoryMetricItem = CreateOverlayMetricItem("memory", true);
        _gameOverlayNetworkMetricItem = CreateOverlayMetricItem("network", false);
        _gameOverlayMetricsItem.DropDownItems.AddRange(
        [ _gameOverlayFpsMetricItem, _gameOverlayCpuMetricItem, _gameOverlayGpuMetricItem, _gameOverlayMemoryMetricItem, _gameOverlayNetworkMetricItem ]);

        _gameOverlayAppearanceItem = new Forms.ToolStripMenuItem();
        _gameOverlayAppearanceItem.Click += OnGameOverlayAppearanceItemClick;
        _gameOverlayConfigurationItem = new Forms.ToolStripMenuItem { Text = "项目与采样…" };
        _gameOverlayConfigurationItem.Click += OnGameOverlayConfigurationItemClick;

        _gameOverlaySettingsItem = new Forms.ToolStripMenuItem();
        _gameOverlaySettingsItem.DropDownItems.AddRange(
        [
            _gameOverlayTargetItem,
            _gameOverlayPositionItem,
            _gameOverlayPresetItem,
            _gameOverlayMetricsItem,
            _gameOverlayAppearanceItem
        ]);

        _appearanceItem = new Forms.ToolStripMenuItem();
        _appearanceItem.Click += OnAppearanceItemClick;

        _gameSafeModeItem = new Forms.ToolStripMenuItem { CheckOnClick = true, Checked = true };
        _gameSafeModeItem.CheckedChanged += OnGameSafeModeCheckedChanged;

        _pinItem = new Forms.ToolStripMenuItem { CheckOnClick = true };
        _pinItem.CheckedChanged += OnPinCheckedChanged;

        _startupItem = new Forms.ToolStripMenuItem { CheckOnClick = true };
        _startupItem.CheckedChanged += OnStartupCheckedChanged;

        _exitItem = new Forms.ToolStripMenuItem();
        _exitItem.Click += OnExitItemClick;

        _contextMenu = new Forms.ContextMenuStrip
        {
            AutoSize = true,
            MinimumSize = new Size(220, 0),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            Padding = new Forms.Padding(4, 6, 4, 6),
            ShowImageMargin = false,
            Renderer = Forms.SystemInformation.HighContrast
                ? new Forms.ToolStripSystemRenderer()
                : new MacToolStripRenderer()
        };
        _contextMenu.Items.AddRange(
        [
            _panelItem,
            _gameOverlayItem,
            _gameOverlayConfigurationItem,
            _gameOverlaySettingsItem,
            _appearanceItem,
            new Forms.ToolStripSeparator(),
            _gameSafeModeItem,
            _pinItem,
            _startupItem,
            new Forms.ToolStripSeparator(),
            _exitItem
        ]);
        ConfigureMenuItems(_contextMenu.Items);
        _contextMenu.Opening += OnContextMenuOpening;

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
    public event EventHandler? ToggleGameOverlayRequested;
    public event EventHandler? SelectGameOverlayTargetRequested;
    public event Action<double>? GameOverlayPositionChanged;
    public event Action<string>? GameOverlayPresetChanged;
    public event Action<GameOverlayMetricVisibility>? GameOverlayMetricsChanged;
    public event EventHandler? GameOverlayAppearanceRequested;
    public event EventHandler? GameOverlayConfigurationRequested;
    public event EventHandler? AppearanceSettingsRequested;
    public event Action<bool>? GameSafeModeChangeRequested;
    public event Action<bool>? PinToggled;
    public event Action<bool>? StartupToggled;
    public event EventHandler? ExitRequested;

    public bool GameOverlayVisible => _gameOverlayVisible;

    public bool GameOverlayAvailable => _gameOverlayAvailable;

    public bool GameSafeModeEnabled => _gameSafeModeItem.Checked;

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

    public void SetGameOverlayState(bool visible, bool available)
    {
        ThrowIfDisposed();
        _gameOverlayVisible = visible;
        _gameOverlayAvailable = available;
        SetGameOverlayItemText();
    }

    public void SetGameOverlayPosition(double positionPercent)
    {
        double normalized = double.IsFinite(positionPercent)
            ? Math.Clamp(positionPercent, 0, 100)
            : 50d;
        foreach (Forms.ToolStripMenuItem item in new[]
                 { _gameOverlayLeftItem, _gameOverlayCenterItem, _gameOverlayRightItem })
        {
            item.Checked = Math.Abs((double)item.Tag! - normalized) < 0.1;
        }
    }

    public void SetGameOverlayPreset(string? preset)
    {
        string normalized = preset?.ToLowerInvariant() ?? "rivatuner";
        foreach (Forms.ToolStripMenuItem item in new[] { _gameOverlayCompactItem, _gameOverlayRivatunerItem, _gameOverlayDetailedItem })
        {
            item.Checked = string.Equals(item.Tag as string, normalized, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetGameOverlayMetrics(GameOverlayMetricVisibility metrics)
    {
        SetCheckedWithoutNotification(_gameOverlayFpsMetricItem, metrics.FrameRate);
        SetCheckedWithoutNotification(_gameOverlayCpuMetricItem, metrics.Cpu);
        SetCheckedWithoutNotification(_gameOverlayGpuMetricItem, metrics.Gpu);
        SetCheckedWithoutNotification(_gameOverlayMemoryMetricItem, metrics.Memory);
        SetCheckedWithoutNotification(_gameOverlayNetworkMetricItem, metrics.Network);
    }

    public void SetGameSafeMode(bool enabled)
    {
        ThrowIfDisposed();
        SetCheckedWithoutNotification(_gameSafeModeItem, enabled);
    }

    public void ShowGameSafeModeRestartRequired()
    {
        ThrowIfDisposed();
        LocalizationService localization = LocalizationService.Current;
        _notifyIcon.ShowBalloonTip(
            5000,
            localization.GetString("TrayGameSafeModeRestartTitle"),
            localization.GetString("TrayGameSafeModeRestartMessage"),
            Forms.ToolTipIcon.Info);
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
        _gameOverlayItem.Click -= OnGameOverlayItemClick;
        _gameOverlayTargetItem.Click -= OnGameOverlayTargetItemClick;
        _gameOverlayLeftItem.Click -= OnGameOverlayPositionItemClick;
        _gameOverlayCenterItem.Click -= OnGameOverlayPositionItemClick;
        _gameOverlayRightItem.Click -= OnGameOverlayPositionItemClick;
        _gameOverlayCompactItem.Click -= OnGameOverlayPresetItemClick;
        _gameOverlayRivatunerItem.Click -= OnGameOverlayPresetItemClick;
        _gameOverlayDetailedItem.Click -= OnGameOverlayPresetItemClick;
        _gameOverlayFpsMetricItem.CheckedChanged -= OnGameOverlayMetricItemCheckedChanged;
        _gameOverlayCpuMetricItem.CheckedChanged -= OnGameOverlayMetricItemCheckedChanged;
        _gameOverlayGpuMetricItem.CheckedChanged -= OnGameOverlayMetricItemCheckedChanged;
        _gameOverlayMemoryMetricItem.CheckedChanged -= OnGameOverlayMetricItemCheckedChanged;
        _gameOverlayNetworkMetricItem.CheckedChanged -= OnGameOverlayMetricItemCheckedChanged;
        _gameOverlayAppearanceItem.Click -= OnGameOverlayAppearanceItemClick;
        _gameOverlayConfigurationItem.Click -= OnGameOverlayConfigurationItemClick;
        _appearanceItem.Click -= OnAppearanceItemClick;
        _gameSafeModeItem.CheckedChanged -= OnGameSafeModeCheckedChanged;
        _pinItem.CheckedChanged -= OnPinCheckedChanged;
        _startupItem.CheckedChanged -= OnStartupCheckedChanged;
        _exitItem.Click -= OnExitItemClick;
        _contextMenu.Opening -= OnContextMenuOpening;
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
        SetGameOverlayItemText();
        _gameOverlayTargetItem.Text = localization.GetString("TrayGameOverlayTarget");
        _gameOverlayPositionItem.Text = localization.GetString("TrayGameOverlayPosition");
        _gameOverlayPresetItem.Text = localization.GetString("TrayGameOverlayPreset");
        _gameOverlayMetricsItem.Text = localization.GetString("TrayGameOverlayMetrics");
        _gameOverlayAppearanceItem.Text = localization.GetString("TrayGameOverlayAppearance");
        _gameOverlayConfigurationItem.Text = localization.GetString("TrayGameOverlayQuickSettings");
        _gameOverlaySettingsItem.Text = localization.GetString("TrayGameOverlayAdvanced");
        _gameOverlayFpsMetricItem.Text = localization.GetString("TrayGameOverlayMetricFps");
        _gameOverlayCpuMetricItem.Text = localization.GetString("TrayGameOverlayMetricCpu");
        _gameOverlayGpuMetricItem.Text = localization.GetString("TrayGameOverlayMetricGpu");
        _gameOverlayMemoryMetricItem.Text = localization.GetString("TrayGameOverlayMetricMemory");
        _gameOverlayNetworkMetricItem.Text = localization.GetString("TrayGameOverlayMetricNetwork");
        _gameOverlayCompactItem.Text = localization.GetString("TrayGameOverlayPresetCompact");
        _gameOverlayRivatunerItem.Text = localization.GetString("TrayGameOverlayPresetRivatuner");
        _gameOverlayDetailedItem.Text = localization.GetString("TrayGameOverlayPresetDetailed");
        _gameOverlayLeftItem.Text = localization.GetString("TrayGameOverlayPositionLeft");
        _gameOverlayCenterItem.Text = localization.GetString("TrayGameOverlayPositionCenter");
        _gameOverlayRightItem.Text = localization.GetString("TrayGameOverlayPositionRight");
        _appearanceItem.Text = localization.GetString("TrayAppearance");
        _gameSafeModeItem.Text = localization.GetString("TrayGameSafeMode");
        _gameSafeModeItem.ToolTipText = localization.GetString("TrayGameSafeModeHelp");
        _pinItem.Text = localization.GetString("TrayPin");
        _startupItem.Text = localization.GetString("TrayStartup");
        _exitItem.Text = localization.GetString("TrayExit");
    }

    private void SetPanelItemText() =>
        _panelItem.Text = LocalizationService.Current.GetString(
            _panelVisible ? "TrayHidePanel" : "TrayShowPanel");

    private void SetGameOverlayItemText()
    {
        _gameOverlayItem.Enabled = _gameOverlayAvailable;
        _gameOverlayPositionItem.Enabled = _gameOverlayAvailable;
        _gameOverlayPresetItem.Enabled = _gameOverlayAvailable;
        _gameOverlayMetricsItem.Enabled = _gameOverlayAvailable;
        _gameOverlayAppearanceItem.Enabled = _gameOverlayAvailable;
        _gameOverlayConfigurationItem.Enabled = _gameOverlayAvailable;
        _gameOverlaySettingsItem.Enabled = _gameOverlayAvailable;
        _gameOverlayTargetItem.Enabled = _gameOverlayAvailable;
        _gameOverlayItem.Text = LocalizationService.Current.GetString(
            GetGameOverlayResourceKey(_gameOverlayVisible, _gameOverlayAvailable));
    }

    internal static string GetGameOverlayResourceKey(bool visible, bool available) =>
        !available
            ? "TrayGameOverlayUnavailableCompatibility"
            : visible
                ? "TrayHideGameOverlay"
                : "TrayShowGameOverlay";

    private void OnCultureChanged(object? sender, EventArgs e) => ApplyLocalizedText();

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _contextMenu.Renderer = Forms.SystemInformation.HighContrast
            ? new Forms.ToolStripSystemRenderer()
            : new MacToolStripRenderer();
    }

    private void OnNotifyIconMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Right)
        {
            // Respect a high-contrast switch made while the process is running.
            _contextMenu.Renderer = Forms.SystemInformation.HighContrast
                ? new Forms.ToolStripSystemRenderer()
                : new MacToolStripRenderer();
        }

        if (e.Button == Forms.MouseButtons.Left)
        {
            ToggleDetailsRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPanelItemClick(object? sender, EventArgs e) =>
        ToggleDetailsRequested?.Invoke(this, EventArgs.Empty);

    private void OnGameOverlayItemClick(object? sender, EventArgs e) =>
        ToggleGameOverlayRequested?.Invoke(this, EventArgs.Empty);

    private void OnGameOverlayTargetItemClick(object? sender, EventArgs e) =>
        SelectGameOverlayTargetRequested?.Invoke(this, EventArgs.Empty);

    private void OnGameOverlayPositionItemClick(object? sender, EventArgs e)
    {
        if (sender is Forms.ToolStripMenuItem item && item.Tag is double position)
        {
            SetGameOverlayPosition(position);
            GameOverlayPositionChanged?.Invoke(position);
        }
    }

    private void OnGameOverlayPresetItemClick(object? sender, EventArgs e)
    {
        if (sender is Forms.ToolStripMenuItem { Tag: string preset })
        {
            SetGameOverlayPreset(preset);
            GameOverlayPresetChanged?.Invoke(preset);
        }
    }

    private void OnGameOverlayMetricItemCheckedChanged(object? sender, EventArgs e)
    {
        if (!_syncingState)
        {
            GameOverlayMetricsChanged?.Invoke(new GameOverlayMetricVisibility(
                _gameOverlayFpsMetricItem.Checked,
                _gameOverlayCpuMetricItem.Checked,
                _gameOverlayGpuMetricItem.Checked,
                _gameOverlayMemoryMetricItem.Checked,
                _gameOverlayNetworkMetricItem.Checked));
        }
    }

    private void OnGameOverlayAppearanceItemClick(object? sender, EventArgs e) =>
        GameOverlayAppearanceRequested?.Invoke(this, EventArgs.Empty);

    private void OnGameOverlayConfigurationItemClick(object? sender, EventArgs e) =>
        GameOverlayConfigurationRequested?.Invoke(this, EventArgs.Empty);

    private void OnAppearanceItemClick(object? sender, EventArgs e) =>
        AppearanceSettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnGameSafeModeCheckedChanged(object? sender, EventArgs e)
    {
        if (!_syncingState)
        {
            GameSafeModeChangeRequested?.Invoke(_gameSafeModeItem.Checked);
        }
    }

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

    private static void ConfigureMenuItems(Forms.ToolStripItemCollection items)
    {
        foreach (Forms.ToolStripItem item in items)
        {
            if (item is Forms.ToolStripSeparator separator)
            {
                separator.AutoSize = false;
                separator.Height = 8;
                separator.Margin = new Forms.Padding(8, 3, 8, 3);
                continue;
            }

            if (item is not Forms.ToolStripMenuItem menuItem)
            {
                continue;
            }

            menuItem.AutoSize = false;
            menuItem.Height = 32;
            menuItem.Padding = new Forms.Padding(10, 0, 10, 0);
            ConfigureMenuItems(menuItem.DropDownItems);
        }
    }

    private Forms.ToolStripMenuItem CreateOverlayPositionItem(double position)
    {
        var item = new Forms.ToolStripMenuItem { Tag = position, CheckOnClick = false };
        item.Click += OnGameOverlayPositionItemClick;
        return item;
    }

    private Forms.ToolStripMenuItem CreateOverlayPresetItem(string preset)
    {
        var item = new Forms.ToolStripMenuItem { Tag = preset, CheckOnClick = false };
        item.Click += OnGameOverlayPresetItemClick;
        return item;
    }

    private Forms.ToolStripMenuItem CreateOverlayMetricItem(string metric, bool enabled)
    {
        var item = new Forms.ToolStripMenuItem { Tag = metric, CheckOnClick = true, Checked = enabled };
        item.CheckedChanged += OnGameOverlayMetricItemCheckedChanged;
        return item;
    }

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
