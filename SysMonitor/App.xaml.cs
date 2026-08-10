using System.Threading;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SysMonitor.Models;
using SysMonitor.Services;
using SysMonitor.UI;

namespace SysMonitor;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\SysMonitor.SingleInstance";

    private readonly SettingsService _settingsService = new();
    private readonly StartupService _startupService = new();
    private Mutex? _singleInstanceMutex;
    private MonitorService? _monitorService;
    private TrayIconService? _trayIcon;
    private BandWindow? _bandWindow;
    private DetailWindow? _detailWindow;
    private AppearanceSettingsWindow? _appearanceSettingsWindow;
    private DispatcherTimer? _bandRecreateTimer;
    private AppSettings _settings = new();
    private bool _isExiting;
    private long _bandGeneration;
    private nint _bandHandle;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // This branch must precede the single-instance mutex and every tray/band
        // initialization step. It is a short-lived, elevated sensor process only.
        if (CpuTemperatureHelperHost.TryGetPipeName(e.Args, out string helperPipeName))
        {
            try
            {
                await CpuTemperatureHelperHost.RunAsync(helperPipeName);
            }
            catch
            {
                // The parent may exit, UAC policy may block the driver, or the pipe
                // may disappear. None of those conditions should show helper UI.
            }
            finally
            {
                Shutdown();
            }

            return;
        }

        string? launcherArgument = e.Args.FirstOrDefault(argument =>
            argument.StartsWith("--launcher-path=", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(launcherArgument))
        {
            string launcherPath = launcherArgument["--launcher-path=".Length..].Trim('"');
            if (File.Exists(launcherPath))
            {
                Environment.SetEnvironmentVariable("SYSMONITOR_LAUNCHER_PATH", launcherPath);
            }
        }

        _singleInstanceMutex = new Mutex(true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        BandDiagnostics.LogProcessSession();
        _ = _startupService.RefreshExistingRegistration();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _settings = _settingsService.Load();

        try
        {
            _ = System.Windows.Media.Fonts.SystemFontFamilies.Count;
            BandDiagnostics.Log("WPF system font cache prepared before band HWND creation");
            BandDiagnostics.Log("creating tray icon service");
            _trayIcon = new TrayIconService();
            BandDiagnostics.Log("tray icon service created");
            BandDiagnostics.Log("creating monitor service");
            _monitorService = new MonitorService();
            BandDiagnostics.Log("monitor service created");

            WireTrayEvents();
            CreateBandWindow();
            EnsureBandRecreateTimer().Start();
            _trayIcon.SetPinned(_settings.PanelTopmost);
            _trayIcon.SetStartupEnabled(_startupService.IsEnabled());
            _trayIcon.SetPanelVisible(false);

            _monitorService.SnapshotUpdated += OnSnapshotUpdated;
            await _monitorService.StartAsync();

            if (e.Args.Any(argument =>
                    string.Equals(argument, "--show-panel", StringComparison.OrdinalIgnoreCase)))
            {
                OnToggleDetailsRequested(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            LogException("Startup failed", exception);
            await ExitAsync();
        }
    }

    private void WireTrayEvents()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.ToggleDetailsRequested += OnToggleDetailsRequested;
        _trayIcon.AppearanceSettingsRequested += OnAppearanceSettingsRequested;
        _trayIcon.PinToggled += OnTrayPinToggled;
        _trayIcon.StartupToggled += OnTrayStartupToggled;
        _trayIcon.ExitRequested += OnExitRequested;
    }

    private void CreateBandWindow()
    {
        if (_isExiting || _bandWindow is not null)
        {
            return;
        }

        long generation = checked(_bandGeneration + 1);
        var band = new BandWindow(generation);
        BandDiagnostics.Log($"creating band window generation={generation}");
        band.ApplyAppearance(CurrentBandAppearance);
        band.UpdateSnapshot(_monitorService?.Latest ?? MonitorSnapshot.Empty);
        band.ToggleDetailsRequested += OnToggleDetailsRequested;
        band.NativeDestroyed += OnBandNativeDestroyed;
        band.HorizontalPositionResolved += OnBandHorizontalPositionResolved;
        _bandWindow = band;
        _bandGeneration = generation;
        _bandHandle = nint.Zero;

        if (_settings.BandVisible)
        {
            band.StartPositionTracking();
            _bandHandle = band.NativeHandle;
            BandDiagnostics.Log(
                $"app tracking band generation={generation} hwnd=0x{_bandHandle.ToInt64():X}");
        }
    }

    private void OnBandNativeDestroyed(object? sender, BandNativeDestroyedEventArgs e)
    {
        if (sender is not BandWindow band ||
            !ReferenceEquals(_bandWindow, band) ||
            e.Generation != _bandGeneration ||
            e.Handle != _bandHandle)
        {
            BandDiagnostics.Log(
                $"stale band destruction ignored eventGeneration={e.Generation} " +
                $"eventHwnd=0x{e.Handle.ToInt64():X} currentGeneration={_bandGeneration} " +
                $"currentHwnd=0x{_bandHandle.ToInt64():X}");
            return;
        }

        DetachBandWindow(band);
        _bandWindow = null;
        _bandHandle = nint.Zero;
        BandDiagnostics.Log(
            $"app observed proven band destruction generation={e.Generation} " +
            $"hwnd=0x{e.Handle.ToInt64():X} source={e.Source}");
        if (_isExiting)
        {
            return;
        }

        EnsureBandRecreateTimer().Start();
    }

    private DispatcherTimer EnsureBandRecreateTimer()
    {
        if (_bandRecreateTimer is not null)
        {
            return _bandRecreateTimer;
        }

        var timer = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        timer.Tick += OnBandRecreateTimerTick;
        _bandRecreateTimer = timer;
        return timer;
    }

    private void OnBandRecreateTimerTick(object? sender, EventArgs e)
    {
        if (_isExiting)
        {
            StopBandRecreateTimer();
            return;
        }

        if (!_settings.BandVisible)
        {
            return;
        }

        if (_bandWindow is { } existingBand)
        {
            nint expectedHandle = existingBand.NativeHandle;
            if (_bandHandle == nint.Zero && expectedHandle != nint.Zero)
            {
                _bandHandle = expectedHandle;
            }

            if (existingBand.IsNativeWindowAlive)
            {
                if (ReferenceEquals(_bandWindow, existingBand) &&
                    existingBand.Generation == _bandGeneration &&
                    expectedHandle == _bandHandle)
                {
                    existingBand.RequestHealthCheck();
                }
                else
                {
                    BandDiagnostics.LogRateLimited(
                        "app-band-live-identity-mismatch",
                        "live band identity mismatch; retaining HWND without replacement",
                        TimeSpan.FromSeconds(2));
                }

                return;
            }

            BandDiagnostics.Log(
                $"health check proved missing/stale band hwnd generation={existingBand.Generation} " +
                $"hwnd=0x{expectedHandle.ToInt64():X}");
            DetachBandWindow(existingBand);
            _bandWindow = null;
            _bandHandle = nint.Zero;
        }

        if (!TaskbarPositioner.IsTaskbarAvailable())
        {
            return;
        }

        BandDiagnostics.Log("health check recreating band window");
        CreateBandWindow();
    }

    private void DetachBandWindow(BandWindow band)
    {
        band.ToggleDetailsRequested -= OnToggleDetailsRequested;
        band.NativeDestroyed -= OnBandNativeDestroyed;
        band.HorizontalPositionResolved -= OnBandHorizontalPositionResolved;
    }

    private void StopBandRecreateTimer()
    {
        _bandRecreateTimer?.Stop();
    }

    private void OnSnapshotUpdated(object? sender, MonitorSnapshot snapshot)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _bandWindow?.UpdateSnapshot(snapshot);
                _detailWindow?.UpdateSnapshot(snapshot);
            }));
    }

    private void OnToggleDetailsRequested(object? sender, EventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        BandWindow? clickBand = sender as BandWindow;
        if (clickBand is not null && !IsCurrentBand(clickBand))
        {
            BandDiagnostics.Log("detail toggle from stale band ignored");
            return;
        }

        if (clickBand is not null)
        {
            LogBandInvariant("click-pre", clickBand);
        }

        try
        {
            bool shouldActivate = clickBand is null;
            DetailWindow detail = EnsureDetailWindow();
            if (detail is { IsVisible: true, WindowState: not WindowState.Minimized })
            {
                BandDiagnostics.Log("detail toggle action=hide");
                SavePanelPosition();
                detail.Hide();
                _trayIcon?.SetPanelVisible(false);
                return;
            }

            if (detail.IsVisible)
            {
                detail.Hide();
            }

            detail.WindowState = WindowState.Normal;
            detail.ShowActivated = shouldActivate;
            detail.UpdateSnapshot(_monitorService?.Latest ?? MonitorSnapshot.Empty);
            if (!detail.IsVisible)
            {
                BandDiagnostics.Log($"detail toggle action=show activate={shouldActivate}");
                detail.Show();
            }

            if (shouldActivate && !detail.IsActive)
            {
                detail.Activate();
            }

            _trayIcon?.SetPanelVisible(true);
        }
        finally
        {
            if (clickBand is not null)
            {
                LogBandInvariant("click-post", clickBand);
            }
        }
    }

    private bool IsCurrentBand(BandWindow band) =>
        ReferenceEquals(_bandWindow, band) &&
        band.Generation == _bandGeneration &&
        band.NativeHandle == _bandHandle &&
        TaskbarPositioner.IsWindowHandleAlive(_bandHandle);

    private void LogBandInvariant(string checkpoint, BandWindow expectedBand)
    {
        nint handle = expectedBand.NativeHandle;
        bool isCurrent = ReferenceEquals(_bandWindow, expectedBand) &&
            expectedBand.Generation == _bandGeneration &&
            handle == _bandHandle;
        bool isAlive = TaskbarPositioner.IsWindowHandleAlive(handle);
        string detailState = _detailWindow is null
            ? "not-created"
            : $"visible={_detailWindow.IsVisible},state={_detailWindow.WindowState}";
        BandDiagnostics.Log(
            $"band invariant checkpoint={checkpoint} current={isCurrent} alive={isAlive} " +
            $"generation={expectedBand.Generation} hwnd=0x{handle.ToInt64():X} detail={detailState}");
    }

    private BandAppearanceSettings CurrentBandAppearance =>
        new(
            _settings.BandFontFamily,
            _settings.BandFontSize,
            _settings.BandHorizontalPositionPercent,
            _settings.BandItemSpacingDip,
            _settings.BandHorizontalOffsetDip);

    private void OnAppearanceSettingsRequested(object? sender, EventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        AppearanceSettingsWindow window = EnsureAppearanceSettingsWindow();
        window.LoadAppearance(CurrentBandAppearance);
        window.Show();
        window.Activate();
    }

    private AppearanceSettingsWindow EnsureAppearanceSettingsWindow()
    {
        if (_appearanceSettingsWindow is not null)
        {
            return _appearanceSettingsWindow;
        }

        var window = new AppearanceSettingsWindow();
        window.AppearanceApplied += OnAppearanceApplied;
        window.AppearancePreviewChanged += OnAppearancePreviewChanged;
        window.LoadAppearance(CurrentBandAppearance);
        _appearanceSettingsWindow = window;
        return window;
    }

    private void OnAppearanceApplied(object? sender, BandAppearanceSettings appearance)
    {
        _settings.BandFontFamily = appearance.FontFamily;
        _settings.BandFontSize = appearance.FontSize;
        _settings.BandItemSpacingDip = appearance.ItemSpacingDip;
        _settings.BandHorizontalPositionPercent = appearance.HorizontalPositionPercent;
        _bandWindow?.ApplyAppearance(appearance);
        _settingsService.Save(_settings);
    }

    private void OnAppearancePreviewChanged(
        object? sender,
        BandAppearanceSettings appearance) =>
        _bandWindow?.ApplyAppearance(appearance);

    private void OnBandHorizontalPositionResolved(object? sender, double positionPercent)
    {
        if (sender is not BandWindow band || !ReferenceEquals(_bandWindow, band) ||
            _settings.BandHorizontalPositionPercent is not null)
        {
            return;
        }

        _settings.BandHorizontalPositionPercent = Math.Clamp(positionPercent, 0, 100);
        _settingsService.Save(_settings);
    }

    private DetailWindow EnsureDetailWindow()
    {
        if (_detailWindow is not null)
        {
            return _detailWindow;
        }

        var detail = new DetailWindow();
        detail.SetPinned(_settings.PanelTopmost);
        detail.PinChanged += OnDetailPinChanged;
        detail.HideRequested += OnDetailHideRequested;
        detail.LocationChanged += OnDetailLocationChanged;

        Rect workArea = SystemParameters.WorkArea;
        double left = _settings.PanelLeft ??
            Math.Max(workArea.Left + 16, workArea.Right - detail.Width - 16);
        double top = _settings.PanelTop ??
            Math.Max(workArea.Top + 16, workArea.Bottom - detail.Height - 56);

        detail.WindowStartupLocation = WindowStartupLocation.Manual;
        detail.Left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - detail.Width));
        detail.Top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - detail.Height));
        _detailWindow = detail;
        return detail;
    }

    private void OnDetailPinChanged(object? sender, EventArgs e)
    {
        if (_detailWindow is null)
        {
            return;
        }

        _settings.PanelTopmost = _detailWindow.IsPinned;
        _trayIcon?.SetPinned(_detailWindow.IsPinned);
        _settingsService.Save(_settings);
    }

    private void OnDetailHideRequested(object? sender, EventArgs e)
    {
        SavePanelPosition();
        _trayIcon?.SetPanelVisible(false);
    }

    private void OnDetailLocationChanged(object? sender, EventArgs e)
    {
        if (_detailWindow is { IsVisible: true, WindowState: WindowState.Normal })
        {
            _settings.PanelLeft = _detailWindow.Left;
            _settings.PanelTop = _detailWindow.Top;
        }
    }

    private void OnTrayPinToggled(bool pinned)
    {
        _settings.PanelTopmost = pinned;
        _detailWindow?.SetPinned(pinned);
        _trayIcon?.SetPinned(pinned);
        _settingsService.Save(_settings);
    }

    private void OnTrayStartupToggled(bool enabled)
    {
        try
        {
            _startupService.SetEnabled(enabled);
        }
        finally
        {
            _trayIcon?.SetStartupEnabled(_startupService.IsEnabled());
        }
    }

    private async void OnExitRequested(object? sender, EventArgs e) => await ExitAsync();

    private void SavePanelPosition()
    {
        if (_detailWindow is { WindowState: WindowState.Normal })
        {
            _settings.PanelLeft = _detailWindow.Left;
            _settings.PanelTop = _detailWindow.Top;
        }

        _settings.PanelTopmost = _detailWindow?.IsPinned ?? _settings.PanelTopmost;
        _settingsService.Save(_settings);
    }

    private async Task ExitAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        StopBandRecreateTimer();
        try
        {
            SavePanelPosition();
        }
        catch (Exception exception)
        {
            LogException("Saving settings during exit failed", exception);
        }

        if (_monitorService is not null)
        {
            _monitorService.SnapshotUpdated -= OnSnapshotUpdated;
            try
            {
                await _monitorService.DisposeAsync();
            }
            catch (Exception exception)
            {
                LogException("Monitor service shutdown failed", exception);
            }
            finally
            {
                _monitorService = null;
            }
        }

        if (_bandWindow is not null)
        {
            try
            {
                _bandWindow.ToggleDetailsRequested -= OnToggleDetailsRequested;
                _bandWindow.NativeDestroyed -= OnBandNativeDestroyed;
                _bandWindow.HorizontalPositionResolved -= OnBandHorizontalPositionResolved;
                _bandWindow.StopPositionTracking();
                _bandWindow.RequestClose();
            }
            catch (Exception exception)
            {
                LogException("Band window shutdown failed", exception);
            }
            finally
            {
                _bandWindow = null;
                _bandHandle = nint.Zero;
            }
        }

        if (_detailWindow is not null)
        {
            try
            {
                _detailWindow.PinChanged -= OnDetailPinChanged;
                _detailWindow.HideRequested -= OnDetailHideRequested;
                _detailWindow.LocationChanged -= OnDetailLocationChanged;
                _detailWindow.ForceClose();
            }
            catch (Exception exception)
            {
                LogException("Detail window shutdown failed", exception);
            }
            finally
            {
                _detailWindow = null;
            }
        }

        if (_appearanceSettingsWindow is not null)
        {
            try
            {
                _appearanceSettingsWindow.AppearanceApplied -= OnAppearanceApplied;
                _appearanceSettingsWindow.AppearancePreviewChanged -= OnAppearancePreviewChanged;
                _appearanceSettingsWindow.ForceClose();
            }
            catch (Exception exception)
            {
                LogException("Appearance window shutdown failed", exception);
            }
            finally
            {
                _appearanceSettingsWindow = null;
            }
        }

        if (_trayIcon is not null)
        {
            try
            {
                _trayIcon.ToggleDetailsRequested -= OnToggleDetailsRequested;
                _trayIcon.AppearanceSettingsRequested -= OnAppearanceSettingsRequested;
                _trayIcon.PinToggled -= OnTrayPinToggled;
                _trayIcon.StartupToggled -= OnTrayStartupToggled;
                _trayIcon.ExitRequested -= OnExitRequested;
                _trayIcon.Dispose();
            }
            catch (Exception exception)
            {
                LogException("Tray icon shutdown failed", exception);
            }
            finally
            {
                _trayIcon = null;
            }
        }

        if (_bandRecreateTimer is not null)
        {
            _bandRecreateTimer.Tick -= OnBandRecreateTimerTick;
            _bandRecreateTimer = null;
        }

        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        Shutdown();
    }

    private async void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("Unhandled UI exception", e.Exception);
        e.Handled = true;
        await ExitAsync();
    }

    private static void LogException(string context, Exception exception)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SysMonitor");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "sysmonitor.log"),
                $"{DateTimeOffset.Now:O} {context}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch
        {
        }
    }

}
