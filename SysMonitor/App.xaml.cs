using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SysMonitor.Models;
using SysMonitor.Services;
using SysMonitor.UI;

namespace SysMonitor;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\SysMonitor.SingleInstance";
    private const string ShowPanelEventName = @"Local\SysMonitor.ShowPanel";
    private const string ExitForUpdateEventName = @"Local\SysMonitor.ExitForUpdate";

    static App()
    {
        try
        {
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(
                    System.Windows.Markup.XmlLanguage.GetLanguage(
                        System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag)));
        }
        catch
        {
            // Ignore if metadata is already overridden in test runners
        }
    }

    private readonly SettingsService _settingsService = new();
    private readonly StartupService _startupService = new();
    private readonly LocalizationService _localizationService = LocalizationService.Current;
    private readonly ThemeCatalogService _themeCatalog = new();
    private readonly ThemeResourceApplier _themeResourceApplier = new();
    private readonly MetricHistoryBuffer _metricHistory = new();
    private readonly RtssLegacyCompatibilityService _rtssLegacyCompatibilityService =
        RtssLegacyCompatibilityService.CreateDefault();
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showPanelEvent;
    private EventWaitHandle? _exitForUpdateEvent;
    private RegisteredWaitHandle? _showPanelRegistration;
    private RegisteredWaitHandle? _exitForUpdateRegistration;
    private MonitorService? _monitorService;
    private GlobalHotkeyService? _gameOverlayHotkey;
    private GameOverlayFrameProviderAdapter? _gameOverlayFrameProvider;
    private GameOverlayController? _gameOverlayController;
    private GameOverlayWindow? _gameOverlayWindow;
    private GameOverlayAppearanceWindow? _gameOverlayAppearanceWindow;
    private GameOverlaySettingsWindow? _gameOverlaySettingsWindow;
    private GameOverlayPreviewState? _gameOverlayPreviewBaseline;
    private bool _gameOverlayPreviewSessionActive;
    private TrayIconService? _trayIcon;
    private BandWindow? _bandWindow;
    private DetailWindow? _detailWindow;
    private AppearanceSettingsWindow? _appearanceSettingsWindow;
    private DispatcherTimer? _bandRecreateTimer;
    private UiRefreshScheduler? _snapshotRefreshScheduler;
    private MonitorSnapshot? _pendingSnapshot;
    private AppSettings _settings = new();
    private ResolvedTheme? _currentTheme;
    private ResolvedTheme? _appliedTheme;
    private bool _isExiting;
    private bool _sessionGameSafeMode;
    private GameOverlayTargetOption? _explicitGameOverlayTarget;
    private long _bandGeneration;
    private long _detailPresentationGeneration;
    private nint _bandHandle;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

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

        bool isFirstInstance;
        try
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out isFirstInstance);
        }
        catch (AbandonedMutexException)
        {
            isFirstInstance = true;
        }

        if (!isFirstInstance)
        {
            SignalExistingInstance();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            Environment.Exit(0);
            return;
        }

        CreateControlEvents();
        BandDiagnostics.LogProcessSession();
        _ = _startupService.RefreshExistingRegistration();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _settings = _settingsService.Load();
        _localizationService.ApplyCulture(_settings.UiCulture);

        try
        {
            await _themeCatalog.InitializeAsync();
        }
        catch (Exception exception)
        {
            LogException("Theme catalog initialization failed; using built-in themes", exception);
        }

        bool isSystemTheme = string.Equals(
            _settings.ActiveThemeId,
            ThemeCatalogService.SystemThemeId,
            StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_settings.ActiveThemeId, "auto", StringComparison.OrdinalIgnoreCase);

        bool activeThemeResolved = _themeCatalog.TryResolve(
            _settings.ActiveThemeId,
            out ResolvedTheme startupTheme);
        if (!activeThemeResolved && !isSystemTheme)
        {
            _settings.ActiveThemeId = startupTheme.Identity.Id;
            _ = TryPatchSettings(settings => settings.ActiveThemeId = startupTheme.Identity.Id);
        }

        if (!ApplyThemeRuntime(startupTheme))
        {
            ResolvedTheme fallbackTheme = _themeCatalog.ResolveOrDefault(ThemeCatalogService.DefaultThemeId);

            _settings.ActiveThemeId = fallbackTheme.Identity.Id;
            _ = TryPatchSettings(settings => settings.ActiveThemeId = fallbackTheme.Identity.Id);
            if (!ApplyThemeRuntime(fallbackTheme))
            {
                LogException(
                    "The built-in default theme could not be applied",
                    new InvalidOperationException("Theme resource initialization failed."));
                Shutdown();
                return;
            }

            startupTheme = fallbackTheme;
        }

        _appliedTheme = startupTheme;

        try
        {
            BandDiagnostics.Log("creating tray icon service");
            _trayIcon = new TrayIconService();
            _ = _trayIcon.ApplyThemeIcon(_currentTheme?.TrayIconPath);
            BandDiagnostics.Log("tray icon service created");
            BandDiagnostics.Log("creating monitor service");
            _sessionGameSafeMode = _settings.GameSafeMode;
            MonitorOptions monitorOptions = MonitorOptions.FromGameSafeMode(_sessionGameSafeMode) with
            {
                SamplingInterval = ResolveGameOverlaySamplingInterval(_settings.GameOverlaySampling)
            };
            _monitorService = new MonitorService(monitorOptions);
            BandDiagnostics.Log("monitor service created");
            _gameOverlayHotkey = new GlobalHotkeyService();
            _gameOverlayHotkey.Pressed += OnGameOverlayHotkeyPressed;
            if (!_gameOverlayHotkey.IsRegistered &&
                !string.IsNullOrWhiteSpace(_gameOverlayHotkey.RegistrationDiagnostic))
            {
                BandDiagnostics.Log(_gameOverlayHotkey.RegistrationDiagnostic);
            }

            WireTrayEvents();
            CreateBandWindow();
            EnsureBandRecreateTimer().Start();
            _trayIcon.SetPinned(_settings.PanelTopmost);
            _trayIcon.SetStartupEnabled(_startupService.IsEnabled());
            _trayIcon.SetPanelVisible(false);
            _trayIcon.SetGameSafeMode(_settings.GameSafeMode);
            _trayIcon.SetGameOverlayState(false, available: true);
            _trayIcon.SetGameOverlayPosition(_settings.GameOverlayHorizontalPositionPercent);
            _trayIcon.SetGameOverlayPreset(_settings.GameOverlayPreset);
            _trayIcon.SetGameOverlayMetrics(_settings.GameOverlayMetrics?.ToEffective() ?? new GameOverlayMetricVisibility());

            _monitorService.SnapshotUpdated += OnSnapshotUpdated;
            _snapshotRefreshScheduler = new UiRefreshScheduler(
                action =>
                {
                    _ = Dispatcher.InvokeAsync(action, DispatcherPriority.Background);
                },
                () => !_isExiting && !Dispatcher.HasShutdownStarted,
                ApplyPendingSnapshot);
            await _monitorService.StartAsync();
            RegisterControlEventCallbacks();

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
        _trayIcon.ToggleGameOverlayRequested += OnToggleGameOverlayRequested;
        _trayIcon.SelectGameOverlayTargetRequested += OnSelectGameOverlayTargetRequested;
        _trayIcon.GameOverlayPositionChanged += OnGameOverlayPositionChanged;
        _trayIcon.GameOverlayPresetChanged += OnGameOverlayPresetChanged;
        _trayIcon.GameOverlayMetricsChanged += OnGameOverlayMetricsChanged;
        _trayIcon.GameOverlayAppearanceRequested += OnGameOverlayAppearanceRequested;
        _trayIcon.GameOverlayConfigurationRequested += OnGameOverlayConfigurationRequested;
        _trayIcon.AppearanceSettingsRequested += OnAppearanceSettingsRequested;
        _trayIcon.GameSafeModeChangeRequested += OnGameSafeModeChangeRequested;
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
        if (_currentTheme is { } theme)
        {
            band.ApplyTheme(theme);
        }
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
        try
        {
            band.StopPositionTracking();
            band.RequestClose();
        }
        catch (Exception exception)
        {
            LogException("Band detach cleanup failed", exception);
        }
    }

    private void StopBandRecreateTimer()
    {
        _bandRecreateTimer?.Stop();
    }

    private void OnSnapshotUpdated(object? sender, MonitorSnapshot snapshot)
    {
        if (_isExiting || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        // Keep history complete on the producer thread, but render only the latest
        // snapshot. A blocked dispatcher can therefore retain at most one snapshot
        // instead of queueing an unbounded series of stale closures.
        _ = _metricHistory.TryAdd(new MetricHistoryPoint(
            snapshot.ProducerId,
            snapshot.Sequence,
            snapshot.MonotonicTimestamp,
            snapshot.CpuUsagePercent,
            snapshot.Gpu?.UsagePercent));
        Volatile.Write(ref _pendingSnapshot, snapshot);
        _snapshotRefreshScheduler?.Request();
    }

    private void ApplyPendingSnapshot()
    {
        Dispatcher.VerifyAccess();
        MonitorSnapshot? snapshot = Interlocked.Exchange(ref _pendingSnapshot, null);
        if (snapshot is null)
        {
            return;
        }

        _bandWindow?.UpdateSnapshot(snapshot);
        _detailWindow?.UpdateSnapshot(snapshot);
        if (_detailWindow is
            {
                IsVisible: true,
                WindowState: not WindowState.Minimized
            } visibleDetail)
        {
            visibleDetail.UpdateHistory(_metricHistory.Snapshot());
        }
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

        long presentationGeneration = Interlocked.Increment(
            ref _detailPresentationGeneration);

        try
        {
            DetailWindowShowPolicy showPolicy =
                DetailWindow.SelectShowPolicy(fromBand: clickBand is not null);
            DetailWindow detail = EnsureDetailWindow();
            if (detail is { IsVisible: true, WindowState: not WindowState.Minimized })
            {
                BandDiagnostics.Log("detail toggle action=hide");
                SavePanelPosition();
                detail.Hide();
                detail.Opacity = 1;
                _trayIcon?.SetPanelVisible(false);
                return;
            }

            if (detail.IsVisible)
            {
                detail.Hide();
            }

            detail.WindowState = WindowState.Normal;
            detail.ShowActivated = showPolicy.Activate;
            detail.Opacity = showPolicy.RevealAfterLayout ? 0 : 1;
            detail.UpdateSnapshot(_monitorService?.Latest ?? MonitorSnapshot.Empty);
            detail.UpdateHistory(_metricHistory.Snapshot());

            if (clickBand is not null || (_settings.PanelLeft == null && _settings.PanelTop == null))
            {
                PositionDetailAboveBand(detail, clickBand);
            }

            if (!detail.IsVisible)
            {
                BandDiagnostics.Log($"detail toggle action=show activate={showPolicy.Activate}");
                detail.Show();
            }

            if (showPolicy.RevealAfterLayout)
            {
                ScheduleDetailPresentation(
                    detail,
                    clickBand,
                    showPolicy,
                    presentationGeneration);
            }
            else if (showPolicy.Activate && !detail.IsActive)
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

    private void ScheduleDetailPresentation(
        DetailWindow detail,
        BandWindow? clickBand,
        DetailWindowShowPolicy showPolicy,
        long presentationGeneration)
    {
        try
        {
            _ = detail.Dispatcher.InvokeAsync(
                () =>
                {
                    if (presentationGeneration !=
                            Volatile.Read(ref _detailPresentationGeneration) ||
                        !ReferenceEquals(_detailWindow, detail))
                    {
                        return;
                    }

                    if (!detail.IsVisible || detail.WindowState == WindowState.Minimized)
                    {
                        detail.Opacity = 1;
                        return;
                    }

                    try
                    {
                        detail.UpdateLayout();
                        BandWindow? currentBand = clickBand is not null &&
                            IsCurrentBand(clickBand)
                                ? clickBand
                                : _bandWindow;
                        PositionDetailAboveBand(detail, currentBand);
                        detail.UpdateLayout();
                        detail.InvalidateVisual();
                        detail.Opacity = 1;

                        if (showPolicy.RaiseWithoutActivation)
                        {
                            detail.RaiseToTopWithoutActivation();
                        }

                        BandDiagnostics.Log(
                            $"detail presentation finalized width={detail.ActualWidth:0.##} " +
                            $"height={detail.ActualHeight:0.##} left={detail.Left:0.##} " +
                            $"top={detail.Top:0.##}");
                    }
                    catch (Exception exception)
                    {
                        detail.Opacity = 1;
                        LogException("Detail presentation finalization failed", exception);
                    }
                },
                DispatcherPriority.ContextIdle);
        }
        catch (InvalidOperationException)
        {
            detail.Opacity = 1;
        }
        catch (TaskCanceledException)
        {
            detail.Opacity = 1;
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
            _settings.BandHorizontalOffsetDip,
            _settings.BandMetricVisibility?.ToEffective() ?? BandMetricVisibility.All);

    private void OnAppearanceSettingsRequested(object? sender, EventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        AppearanceSettingsWindow window = EnsureAppearanceSettingsWindow();
        window.LoadAppearance(CurrentBandAppearance);
        window.LoadThemes(_themeCatalog.Catalog.Items, _settings.ActiveThemeId);
        window.LoadUiCulture(_settings.UiCulture);
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
        window.AppearanceThemeApplied += OnAppearanceThemeApplied;
        window.AppearancePreviewChanged += OnAppearancePreviewChanged;
        window.ThemePreviewRequested += OnThemePreviewRequested;
        window.ThemeImported += OnThemeImported;
        window.ThemeImportRequested = ImportThemeAsync;
        window.UiCultureChanged += OnUiCultureChanged;
        window.LoadAppearance(CurrentBandAppearance);
        window.LoadThemes(_themeCatalog.Catalog.Items, _settings.ActiveThemeId);
        window.LoadUiCulture(_settings.UiCulture);
        _appearanceSettingsWindow = window;
        return window;
    }

    private void OnAppearanceThemeApplied(object? sender, AppearanceThemeApplyEventArgs e)
    {
        BandAppearanceSettings previousAppearance = CurrentBandAppearance;
        string previousThemeId = _settings.ActiveThemeId;
        ResolvedTheme previousTheme = _appliedTheme ??
            _themeCatalog.ResolveOrDefault(previousThemeId);
        BandAppearanceSettings appearance = e.Appearance;
        if (!_themeCatalog.TryResolve(e.ThemeId, out ResolvedTheme selectedTheme))
        {
            e.ErrorMessage = _localizationService.GetString("ThemeUnavailable");
            return;
        }

        if (!ApplyThemeRuntime(selectedTheme))
        {
            _ = ApplyThemeRuntime(previousTheme);
            e.ErrorMessage = _localizationService.GetString("ThemeUnavailable");
            return;
        }

        _settings.BandFontFamily = appearance.FontFamily;
        _settings.BandFontSize = appearance.FontSize;
        _settings.BandItemSpacingDip = appearance.ItemSpacingDip;
        _settings.BandHorizontalPositionPercent = appearance.HorizontalPositionPercent;
        _settings.BandMetricVisibility =
            BandMetricVisibilitySettings.FromEffective(appearance.EffectiveMetricVisibility);

        string targetThemeId = string.Equals(e.ThemeId, ThemeCatalogService.SystemThemeId, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(e.ThemeId, "auto", StringComparison.OrdinalIgnoreCase)
            ? ThemeCatalogService.SystemThemeId
            : selectedTheme.Identity.Id;

        _settings.ActiveThemeId = targetThemeId;
        if (!TryPatchSettings(settings =>
            {
                settings.BandFontFamily = appearance.FontFamily;
                settings.BandFontSize = appearance.FontSize;
                settings.BandItemSpacingDip = appearance.ItemSpacingDip;
                settings.BandHorizontalPositionPercent = appearance.HorizontalPositionPercent;
                settings.BandMetricVisibility = BandMetricVisibilitySettings.FromEffective(
                    appearance.EffectiveMetricVisibility);
                settings.ActiveThemeId = targetThemeId;
            }))
        {
            RestoreAppearanceSettings(previousAppearance);
            _settings.ActiveThemeId = previousThemeId;
            _bandWindow?.ApplyAppearance(previousAppearance);
            _ = ApplyThemeRuntime(previousTheme);
            e.ErrorMessage = _localizationService.GetString("AppearanceSaveFailed");
            return;
        }

        _bandWindow?.ApplyAppearance(appearance);
        _appliedTheme = selectedTheme;
        e.Accepted = true;
    }

    private void OnAppearancePreviewChanged(
        object? sender,
        BandAppearanceSettings appearance) =>
        _bandWindow?.ApplyAppearance(appearance);

    private void OnThemePreviewRequested(string themeId)
    {
        if (_themeCatalog.TryResolve(themeId, out ResolvedTheme theme))
        {
            _ = ApplyThemeRuntime(theme);
        }
    }

    private Task<ThemeImportResult> ImportThemeAsync(
        string packagePath,
        CancellationToken cancellationToken) =>
        _themeCatalog.ImportAsync(packagePath, cancellationToken);

    private void OnThemeImported(ThemeImportResult result)
    {
        if (!result.Success || result.Theme is null || _appearanceSettingsWindow is null)
        {
            return;
        }

        _appearanceSettingsWindow.LoadThemes(
            _themeCatalog.Catalog.Items,
            result.Theme.Identity.Id,
            markApplied: false);
        _ = ApplyThemeRuntime(result.Theme);
    }

    private bool ApplyThemeRuntime(ResolvedTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (!Dispatcher.CheckAccess())
        {
            return false;
        }

        try
        {
            _themeResourceApplier.Apply(theme);
            _currentTheme = theme;
            _bandWindow?.ApplyTheme(theme);
            _detailWindow?.ApplyTheme(theme);
            _ = _trayIcon?.ApplyThemeIcon(theme.TrayIconPath);
            return true;
        }
        catch (Exception exception)
        {
            LogException($"Applying theme '{theme.Identity.Id}' failed", exception);
            return false;
        }
    }

    private void RestoreAppearanceSettings(BandAppearanceSettings appearance)
    {
        _settings.BandFontFamily = appearance.FontFamily;
        _settings.BandFontSize = appearance.FontSize;
        _settings.BandItemSpacingDip = appearance.ItemSpacingDip;
        _settings.BandHorizontalPositionPercent = appearance.HorizontalPositionPercent;
        _settings.BandHorizontalOffsetDip = appearance.LegacyHorizontalOffsetDip;
        _settings.BandMetricVisibility =
            BandMetricVisibilitySettings.FromEffective(appearance.EffectiveMetricVisibility);
    }

    private void OnUiCultureChanged(string culturePreference)
    {
        string normalized = LocalizationService.NormalizeCulturePreference(culturePreference);
        _settings.UiCulture = normalized;
        PatchSettings(settings => settings.UiCulture = normalized);
        _localizationService.ApplyCulture(normalized);
    }

    private void OnBandHorizontalPositionResolved(object? sender, double positionPercent)
    {
        if (sender is not BandWindow band || !ReferenceEquals(_bandWindow, band) ||
            _settings.BandHorizontalPositionPercent is not null)
        {
            return;
        }

        _settings.BandHorizontalPositionPercent = Math.Clamp(positionPercent, 0, 100);
        PatchSettings(settings => settings.BandHorizontalPositionPercent =
            Math.Clamp(positionPercent, 0, 100));
    }

    private DetailWindow EnsureDetailWindow()
    {
        if (_detailWindow is not null)
        {
            return _detailWindow;
        }

        var detail = new DetailWindow();
        if (_currentTheme is { } theme)
        {
            detail.ApplyTheme(theme);
        }
        detail.SetPinned(_settings.PanelTopmost);
        detail.PinChanged += OnDetailPinChanged;
        detail.HideRequested += OnDetailHideRequested;
        detail.LocationChanged += OnDetailLocationChanged;

        detail.WindowStartupLocation = WindowStartupLocation.Manual;
        if (_settings.PanelLeft is double left && _settings.PanelTop is double top)
        {
            Rect workArea = SystemParameters.WorkArea;
            detail.Left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - detail.Width));
            detail.Top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - detail.Height));
        }
        else
        {
            PositionDetailAboveBand(detail, _bandWindow);
        }

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
        PatchSettings(settings => settings.PanelTopmost = _detailWindow.IsPinned);
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
        PatchSettings(settings => settings.PanelTopmost = pinned);
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

    private async void OnGameOverlayHotkeyPressed(object? sender, EventArgs e)
    {
        if (_isExiting || !EnsureGameOverlayInitialized())
        {
            return;
        }

        await ToggleGameOverlayAsync(fromHotkey: true);
    }

    private async void OnToggleGameOverlayRequested(object? sender, EventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        if (!EnsureGameOverlayInitialized())
        {
            return;
        }

        await ToggleGameOverlayAsync(fromHotkey: false);
    }

    private async Task ToggleGameOverlayAsync(bool fromHotkey)
    {
        GameOverlayController? controller = _gameOverlayController;
        if (controller is null)
        {
            return;
        }

        try
        {
            if (fromHotkey)
            {
                await controller.ToggleFromHotkeyAsync();
            }
            else
            {
                await controller.ToggleFromTrayAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LogException("Game overlay toggle failed", exception);
        }
        finally
        {
            OnGameOverlayStateChanged(controller, EventArgs.Empty);
        }
    }

    private bool EnsureGameOverlayInitialized()
    {
        if (_gameOverlayController is not null &&
            _gameOverlayFrameProvider is not null &&
            _gameOverlayWindow is not null)
        {
            return true;
        }

        if (_isExiting || _monitorService is null || Dispatcher.HasShutdownStarted)
        {
            return false;
        }

        if (!Dispatcher.CheckAccess())
        {
            try
            {
                return Dispatcher.Invoke(EnsureGameOverlayInitialized);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
        }

        GameOverlayFrameProviderAdapter? frameProvider = null;
        GameOverlayWindow? window = null;
        GameOverlayController? controller = null;
        try
        {
            frameProvider = new GameOverlayFrameProviderAdapter(
                GameOverlayFrameRateProviderFactory.Create());
            window = new GameOverlayWindow();
            window.SetHorizontalPositionPercent(
                _settings.GameOverlayHorizontalPositionPercent);
            window.SetMonitorPositions(_settings.GameOverlayMonitorPositions);
            window.SetLayout(
                _settings.GameOverlayPreset,
                _settings.GameOverlayMetrics?.ToEffective() ?? new GameOverlayMetricVisibility());
            window.SetLayoutMode(_settings.GameOverlayLayoutMode);
            window.SetAppearance(
                _settings.GameOverlayAppearance?.ToEffective() ?? new GameOverlayAppearance());
            controller = new GameOverlayController(
                frameProvider,
                _monitorService,
                new ForegroundTargetTracker(new Win32ForegroundWindowSource()),
                window,
                ResolveGameOverlaySamplingInterval(_settings.GameOverlaySampling));
            controller.StateChanged += OnGameOverlayStateChanged;

            _gameOverlayFrameProvider = frameProvider;
            _gameOverlayWindow = window;
            _gameOverlayController = controller;
            BandDiagnostics.Log("game overlay runtime initialized on first use");
            return true;
        }
        catch (Exception exception)
        {
            if (controller is not null)
            {
                controller.StateChanged -= OnGameOverlayStateChanged;
                try
                {
                    controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                }
            }

            if (frameProvider is not null)
            {
                try
                {
                    frameProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                }
            }

            try
            {
                window?.Close();
            }
            catch
            {
            }

            LogException("Game overlay lazy initialization failed", exception);
            return false;
        }
    }

    private void OnGameOverlayStateChanged(object? sender, EventArgs e)
    {
        bool visible = _gameOverlayController?.DesiredVisible == true;
        _trayIcon?.SetGameOverlayState(visible, available: true);
        UpdateDetailedHudTelemetry();

        if (visible && _gameOverlayController?.CurrentTarget is { ExecutablePath: { Length: > 0 } exePath } target)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (_rtssLegacyCompatibilityService.TryAutoEnableForExecutable(exePath, out _))
                    {
                        BandDiagnostics.Log($"auto-configured legacy RTSS compatibility for {Path.GetFileName(exePath)}");
                        RtssLegacyCompatibilityService.TryEnsureRtssRunning(out _);
                    }
                }
                catch
                {
                }
            });
        }
    }

    private void CreateControlEvents()
    {
        try
        {
            _showPanelEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                ShowPanelEventName);
            _exitForUpdateEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                ExitForUpdateEventName);
            BandDiagnostics.Log("launcher control events created");
        }
        catch (Exception exception)
        {
            DisposeControlEvents();
            LogException("Launcher control event creation failed", exception);
        }
    }

    private void RegisterControlEventCallbacks()
    {
        if (_showPanelEvent is null || _exitForUpdateEvent is null)
        {
            return;
        }

        try
        {
            _showPanelRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showPanelEvent,
                static (state, timedOut) =>
                {
                    var app = (App)state!;
                    BandDiagnostics.Log($"launcher show event received timedOut={timedOut}");
                    _ = app.Dispatcher.InvokeAsync(
                        app.ShowDetailsFromExternalActivation,
                        DispatcherPriority.Normal);
                },
                this,
                Timeout.Infinite,
                executeOnlyOnce: false);
            _exitForUpdateRegistration = ThreadPool.RegisterWaitForSingleObject(
                _exitForUpdateEvent,
                static (state, timedOut) =>
                {
                    var app = (App)state!;
                    BandDiagnostics.Log($"launcher exit event received timedOut={timedOut}");
                    _ = app.Dispatcher.InvokeAsync(
                        () => _ = app.ExitAsync(),
                        DispatcherPriority.Send);
                },
                this,
                Timeout.Infinite,
                executeOnlyOnce: false);
            BandDiagnostics.Log("launcher control events ready");
        }
        catch (Exception exception)
        {
            DisposeControlEvents();
            LogException("Launcher control event registration failed", exception);
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using EventWaitHandle showEvent = EventWaitHandle.OpenExisting(ShowPanelEventName);
            showEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void DisposeControlEvents()
    {
        _showPanelRegistration?.Unregister(null);
        _showPanelRegistration = null;
        _exitForUpdateRegistration?.Unregister(null);
        _exitForUpdateRegistration = null;
        _showPanelEvent?.Dispose();
        _showPanelEvent = null;
        _exitForUpdateEvent?.Dispose();
        _exitForUpdateEvent = null;
    }

    private void ShowDetailsFromExternalActivation()
    {
        if (_isExiting)
        {
            return;
        }

        if (_detailWindow is { IsVisible: true, WindowState: not WindowState.Minimized } detail)
        {
            if (!detail.IsActive)
            {
                detail.Activate();
            }

            return;
        }

        OnToggleDetailsRequested(null, EventArgs.Empty);
    }

    private async void OnSelectGameOverlayTargetRequested(object? sender, EventArgs e)
    {
        if (_isExiting || !EnsureGameOverlayInitialized())
        {
            return;
        }

        GameOverlayController controller = _gameOverlayController!;

        GameOverlayTargetOption? option = GameOverlayTargetSelectionDialog.Show();
        ForegroundTarget? target = GameOverlayTargetCatalog.ToForegroundTarget(option);
        if (target is null)
        {
            return;
        }

        try
        {
            _explicitGameOverlayTarget = option;
            await controller.ShowForTargetAsync(target);
        }
        catch (Exception exception)
        {
            LogException("Manual game overlay target failed", exception);
        }
        finally
        {
            OnGameOverlayStateChanged(controller, EventArgs.Empty);
        }
    }

    private void OnGameOverlayPositionChanged(double positionPercent)
    {
        double normalized = double.IsFinite(positionPercent)
            ? Math.Clamp(positionPercent, 0, 100)
            : 50d;
        _settings.GameOverlayHorizontalPositionPercent = normalized;
        _settings.GameOverlayMonitorPositions?.Clear();
        _gameOverlayWindow?.SetMonitorPositions([]);
        _gameOverlayWindow?.SetHorizontalPositionPercent(normalized);
        PatchSettings(settings =>
        {
            settings.GameOverlayHorizontalPositionPercent = normalized;
            settings.GameOverlayMonitorPositions = [];
        });
        _trayIcon?.SetGameOverlayPosition(normalized);
    }

    private void OnGameOverlayPresetChanged(string preset)
    {
        _settings.GameOverlayPreset = preset;
        PatchSettings(settings => settings.GameOverlayPreset = preset);
        _gameOverlayWindow?.SetLayout(
            _settings.GameOverlayPreset,
            _settings.GameOverlayMetrics?.ToEffective() ?? new GameOverlayMetricVisibility());
        UpdateDetailedHudTelemetry();
    }

    private void UpdateDetailedHudTelemetry() =>
        _monitorService?.SetDetailedTelemetryEnabled(
            _gameOverlayController?.DesiredVisible == true &&
            string.Equals(_settings.GameOverlayPreset, "detailed", StringComparison.OrdinalIgnoreCase));

    private void OnGameOverlayMetricsChanged(GameOverlayMetricVisibility metrics)
    {
        _settings.GameOverlayMetrics = GameOverlayMetricVisibilitySettings.FromEffective(metrics);
        PatchSettings(settings => settings.GameOverlayMetrics =
            GameOverlayMetricVisibilitySettings.FromEffective(metrics));
        _gameOverlayWindow?.SetLayout(_settings.GameOverlayPreset, metrics);
    }

    private void OnGameOverlayAppearanceRequested(object? sender, EventArgs e)
    {
        if (_isExiting || !EnsureGameOverlayInitialized())
        {
            return;
        }

        GameOverlayAppearanceWindow window = EnsureGameOverlayAppearanceWindow();
        window.LoadAppearance(_settings.GameOverlayAppearance?.ToEffective() ?? new GameOverlayAppearance());
        window.Show();
        window.Activate();
    }

    private void OnGameOverlayConfigurationRequested(object? sender, EventArgs e)
    {
        if (_isExiting || !EnsureGameOverlayInitialized()) return;
        GameOverlaySettingsWindow window = EnsureGameOverlaySettingsWindow();
        if (window.IsVisible)
        {
            window.Activate();
            return;
        }

        BeginGameOverlayPreviewSession(window);
        LoadGameOverlayConfiguration(window);
        window.Show();
        window.Activate();
    }

    private GameOverlaySettingsWindow EnsureGameOverlaySettingsWindow()
    {
        if (_gameOverlaySettingsWindow is not null) return _gameOverlaySettingsWindow;
        var window = new GameOverlaySettingsWindow();
        window.ApplyRequested = OnGameOverlayConfigurationApplyRequested;
        window.PreviewRequested = OnGameOverlayPreviewRequested;
        window.PreviewSessionFinished = OnGameOverlayPreviewSessionFinished;
        window.AppearanceRequested = () => OnGameOverlayAppearanceRequested(this, EventArgs.Empty);
        _gameOverlaySettingsWindow = window;
        return window;
    }

    private void BeginGameOverlayPreviewSession(GameOverlaySettingsWindow window)
    {
        if (_gameOverlayPreviewSessionActive)
        {
            RestoreGameOverlayPreviewBaseline();
        }

        _gameOverlayPreviewBaseline = _gameOverlayWindow?.CapturePreviewState();
        _gameOverlayPreviewSessionActive = _gameOverlayPreviewBaseline is not null;
        window.BeginPreviewSession();
    }

    private void OnGameOverlayPreviewRequested(GameOverlayPreviewRequest request)
    {
        if (!_gameOverlayPreviewSessionActive ||
            _gameOverlayPreviewBaseline is not GameOverlayPreviewState baseline ||
            _gameOverlayWindow is null)
        {
            return;
        }

        // Layout is monitor-independent and remains safe to preview even when a
        // display was hot-plugged. Only the physical position mutation is gated
        // by the captured monitor snapshot below.
        _gameOverlayWindow.SetLayoutMode(request.LayoutMode);
        if (request.Monitor is null)
        {
            _gameOverlaySettingsWindow?.SetPreviewStatus(
                _localizationService.GetString("HudPreviewActive"));
            return;
        }

        if (!_gameOverlayWindow.TryGetCurrentCoordinateContext(out OverlaySettingsCoordinateContext currentContext) ||
            !_gameOverlayWindow.TryGetCurrentMonitorIdentity(out OverlayMonitorIdentity currentIdentity) ||
            !GameOverlayWindow.CoordinateContextMatches(request.Monitor, currentContext))
        {
            _gameOverlaySettingsWindow?.SetPreviewStatus(
                _localizationService.GetString("HudPositionStaleMessage"));
            _gameOverlaySettingsWindow?.ReloadCoordinateContext(
                _gameOverlayWindow.TryGetCurrentCoordinateContext(out OverlaySettingsCoordinateContext refreshed)
                    ? refreshed
                    : null);
            return;
        }

        IReadOnlyList<GameOverlayMonitorPositionSettings> previewPositions =
            GameOverlayWindow.BuildPreviewMonitorPositions(
                baseline.MonitorPositions,
                currentIdentity,
                request.ExactEnabled,
                request.X,
                request.Y);
        _gameOverlayWindow.SetMonitorPositions(previewPositions);
        _gameOverlaySettingsWindow?.SetPreviewStatus(
            _localizationService.GetString("HudPreviewActive"));
    }

    private void OnGameOverlayPreviewSessionFinished(bool committed)
    {
        if (!_gameOverlayPreviewSessionActive)
        {
            return;
        }

        GameOverlayPreviewState? baseline = _gameOverlayPreviewBaseline;
        _gameOverlayPreviewBaseline = null;
        _gameOverlayPreviewSessionActive = false;
        if (!committed && baseline is not null)
        {
            _gameOverlayWindow?.RestorePreviewState(baseline);
        }
    }

    private void RestoreGameOverlayPreviewBaseline()
    {
        if (_gameOverlayPreviewBaseline is GameOverlayPreviewState baseline)
        {
            _gameOverlayWindow?.RestorePreviewState(baseline);
        }

        _gameOverlayPreviewBaseline = null;
        _gameOverlayPreviewSessionActive = false;
    }

    private void ResetGameOverlayPreviewAfterFailedApply(bool reloadConfiguration)
    {
        RestoreGameOverlayPreviewBaseline();
        if (_gameOverlaySettingsWindow is null || _gameOverlayWindow is null)
        {
            return;
        }

        _gameOverlayPreviewBaseline = _gameOverlayWindow.CapturePreviewState();
        _gameOverlayPreviewSessionActive = true;
        if (reloadConfiguration)
        {
            LoadGameOverlayConfiguration(_gameOverlaySettingsWindow);
        }

        _gameOverlaySettingsWindow.ResetAfterFailedApply();
    }

    private void LoadGameOverlayConfiguration(GameOverlaySettingsWindow window)
    {
        var targets = new List<LegacyFpsTargetView>();
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<RtssManagedTargetSummary> managedTargets =
            _rtssLegacyCompatibilityService.EnumerateManagedTargets();
        string? preferredPath = null;

        if (_explicitGameOverlayTarget is { ExecutablePath: { Length: > 0 } explicitPath })
        {
            RtssManagedTargetSummary? managedStatus = managedTargets.FirstOrDefault(candidate =>
                string.Equals(candidate.ExecutablePath, explicitPath, StringComparison.OrdinalIgnoreCase));
            RtssCompatibilityResult? queriedStatus = managedStatus is null
                ? _rtssLegacyCompatibilityService.Query(explicitPath)
                : null;
            targets.Add(new LegacyFpsTargetView(
                BuildLegacyTargetDisplayName(_explicitGameOverlayTarget.ProcessName, explicitPath),
                explicitPath,
                managedStatus?.Enabled ?? queriedStatus!.Enabled,
                managedStatus?.Managed ?? queriedStatus!.Managed,
                managedStatus?.CanEnable ?? queriedStatus!.CanEnable,
                managedStatus?.CanDisable ?? queriedStatus!.CanDisable,
                managedStatus?.Code ?? queriedStatus!.Code));
            knownPaths.Add(explicitPath);
            preferredPath = explicitPath;
        }

        foreach (RtssManagedTargetSummary managed in managedTargets)
        {
            string? path = managed.ExecutablePath;
            if (!string.IsNullOrWhiteSpace(path) && !knownPaths.Add(path))
            {
                continue;
            }

            targets.Add(new LegacyFpsTargetView(
                BuildLegacyTargetDisplayName(managed.ExecutableName, path),
                path,
                managed.Enabled,
                managed.Managed,
                managed.CanEnable,
                managed.CanDisable,
                managed.Code));
        }

        window.LoadConfiguration(
            _settings.GameOverlayMetrics?.ToEffective() ?? new GameOverlayMetricVisibility(),
            _settings.GameOverlaySampling,
            targets,
            preferredPath,
            _settings.GameOverlayLayoutMode,
            _gameOverlayWindow is not null &&
                _gameOverlayWindow.TryGetCurrentCoordinateContext(out OverlaySettingsCoordinateContext coordinateContext)
                    ? coordinateContext
                    : null,
            _settings.GameOverlayHorizontalPositionPercent);
    }

    private static string BuildLegacyTargetDisplayName(string? executableName, string? executablePath)
    {
        string name = !string.IsNullOrWhiteSpace(executableName)
            ? executableName
            : (!string.IsNullOrWhiteSpace(executablePath) ? Path.GetFileName(executablePath) : "Unknown target");
        return string.IsNullOrWhiteSpace(executablePath) ? name : $"{name} — {executablePath}";
    }

    private bool OnGameOverlayConfigurationApplyRequested(GameOverlayConfigurationRequest request)
    {
        OverlayMonitorIdentity? positionIdentity = null;
        if (request.PositionChange != GameOverlayPositionChange.None)
        {
            var requestedContext = new OverlaySettingsCoordinateContext(
                request.PositionMonitorId ?? string.Empty,
                string.Empty,
                request.PositionLeft,
                request.PositionTop,
                request.PositionRight,
                request.PositionBottom,
                request.PositionX ?? 0,
                request.PositionY ?? 0,
                ExactEnabled: request.PositionChange == GameOverlayPositionChange.Set);
            if (_gameOverlayWindow is null ||
                !_gameOverlayWindow.TryGetCurrentCoordinateContext(out OverlaySettingsCoordinateContext currentContext) ||
                !GameOverlayWindow.CoordinateContextMatches(requestedContext, currentContext) ||
                !_gameOverlayWindow.TryGetCurrentMonitorIdentity(out OverlayMonitorIdentity currentIdentity))
            {
                System.Windows.MessageBox.Show(
                    _gameOverlaySettingsWindow,
                    _localizationService.GetString("HudPositionStaleMessage"),
                    _localizationService.GetString("HudPositionStaleTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                ResetGameOverlayPreviewAfterFailedApply(reloadConfiguration: true);
                return false;
            }

            positionIdentity = currentIdentity;
        }

        if (request.LegacyChanged && !string.IsNullOrWhiteSpace(request.LegacyExecutablePath))
        {
            if (_sessionGameSafeMode)
            {
                ResetGameOverlayPreviewAfterFailedApply(reloadConfiguration: false);
                return false;
            }
        }

        if (positionIdentity is OverlayMonitorIdentity originallySelected &&
            (_gameOverlayWindow is null ||
             !_gameOverlayWindow.TryGetCurrentMonitorIdentity(out OverlayMonitorIdentity nowSelected) ||
             !SameOverlayMonitorSnapshot(originallySelected, nowSelected)))
        {
            System.Windows.MessageBox.Show(
                _gameOverlaySettingsWindow,
                _localizationService.GetString("HudPositionStaleMessage"),
                _localizationService.GetString("HudPositionStaleTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ResetGameOverlayPreviewAfterFailedApply(reloadConfiguration: true);
            return false;
        }

        string previousLayoutMode = _settings.GameOverlayLayoutMode;
        List<GameOverlayMonitorPositionSettings> previousMonitorPositions =
            SettingsService.NormalizeOverlayMonitorPositions(_settings.GameOverlayMonitorPositions);
        GameOverlayMetricVisibilitySettings? previousMetrics = _settings.GameOverlayMetrics;
        string previousSampling = _settings.GameOverlaySampling;
        double previousHorizontalPositionPercent = _settings.GameOverlayHorizontalPositionPercent;

        GameOverlayMetricVisibility metrics = request.Metrics;
        string sampling = request.Sampling;
        _settings.GameOverlayLayoutMode = string.Equals(
            request.LayoutMode,
            "horizontal",
            StringComparison.OrdinalIgnoreCase)
                ? "horizontal"
                : "vertical";

        if (request.HorizontalPositionPercent is double percent)
        {
            double normalized = Math.Clamp(percent, 0, 100);
            _settings.GameOverlayHorizontalPositionPercent = normalized;
            _trayIcon?.SetGameOverlayPosition(normalized);
        }

        if (positionIdentity is OverlayMonitorIdentity identity)
        {
            RemoveOverlayMonitorPosition(identity);
            if (request.PositionChange == GameOverlayPositionChange.Reset)
            {
                _settings.GameOverlayMonitorPositions?.Clear();
                _gameOverlayWindow?.SetMonitorPositions([]);
                _gameOverlayWindow?.SetHorizontalPositionPercent(_settings.GameOverlayHorizontalPositionPercent);
            }
            else if (request.PositionChange == GameOverlayPositionChange.Set &&
                request.PositionX is int positionX &&
                request.PositionY is int positionY)
            {
                if (_gameOverlayWindow is not null &&
                    _gameOverlayWindow.TryClampCurrentExactPosition(positionX, positionY, out int clampedX, out int clampedY))
                {
                    positionX = clampedX;
                    positionY = clampedY;
                }
                (_settings.GameOverlayMonitorPositions ??= []).Add(
                    new GameOverlayMonitorPositionSettings
                    {
                        StableMonitorId = identity.StableMonitorId,
                        GdiDeviceName = identity.GdiDeviceName,
                        IsFallbackIdentity = identity.IsFallback,
                        Left = identity.Bounds.Left,
                        Top = identity.Bounds.Top,
                        Right = identity.Bounds.Right,
                        Bottom = identity.Bounds.Bottom,
                        X = positionX,
                        Y = positionY
                    });
            }
        }
        _settings.GameOverlayMetrics = GameOverlayMetricVisibilitySettings.FromEffective(metrics);
        _settings.GameOverlaySampling = sampling;
        if (!TryPatchSettings(settings =>
            {
                settings.GameOverlayLayoutMode = _settings.GameOverlayLayoutMode;
                settings.GameOverlayHorizontalPositionPercent = _settings.GameOverlayHorizontalPositionPercent;
                settings.GameOverlayMonitorPositions = SettingsService.NormalizeOverlayMonitorPositions(
                    _settings.GameOverlayMonitorPositions);
                settings.GameOverlayMetrics = _settings.GameOverlayMetrics;
                settings.GameOverlaySampling = _settings.GameOverlaySampling;
            }))
        {
            _settings.GameOverlayLayoutMode = previousLayoutMode;
            _settings.GameOverlayMonitorPositions = previousMonitorPositions;
            _settings.GameOverlayMetrics = previousMetrics;
            _settings.GameOverlaySampling = previousSampling;
            _settings.GameOverlayHorizontalPositionPercent = previousHorizontalPositionPercent;
            ResetGameOverlayPreviewAfterFailedApply(reloadConfiguration: true);
            System.Windows.MessageBox.Show(
                _gameOverlaySettingsWindow,
                _localizationService.GetString("HudSettingsSaveFailedMessage"),
                _localizationService.GetString("HudSettingsSaveFailedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        // Mutate the external RTSS profile only after every stale-monitor check
        // and the atomic settings write have succeeded. This prevents a later
        // settings failure from leaving an unrelated profile change behind.
        if (request.LegacyChanged && !string.IsNullOrWhiteSpace(request.LegacyExecutablePath))
        {
            RtssCompatibilityResult result = _rtssLegacyCompatibilityService.SetEnabled(
                request.LegacyExecutablePath,
                request.LegacyEnabled);
            if (!result.Success)
            {
                bool settingsRolledBack = TryPatchSettings(settings =>
                {
                    settings.GameOverlayLayoutMode = previousLayoutMode;
                    settings.GameOverlayHorizontalPositionPercent = previousHorizontalPositionPercent;
                    settings.GameOverlayMonitorPositions = previousMonitorPositions;
                    settings.GameOverlayMetrics = previousMetrics;
                    settings.GameOverlaySampling = previousSampling;
                });
                if (!settingsRolledBack)
                {
                    _settings.GameOverlayLayoutMode = previousLayoutMode;
                    _settings.GameOverlayHorizontalPositionPercent = previousHorizontalPositionPercent;
                    _settings.GameOverlayMonitorPositions = previousMonitorPositions;
                    _settings.GameOverlayMetrics = previousMetrics;
                    _settings.GameOverlaySampling = previousSampling;
                }

                BandDiagnostics.Log(
                    $"RTSS compatibility change failed code={result.Code} " +
                    $"settingsRollback={settingsRolledBack} diagnostic={result.Diagnostic}");
                System.Windows.MessageBox.Show(
                    _gameOverlaySettingsWindow,
                    _localizationService.Format("HudLegacyApplyFailedMessage", result.Diagnostic),
                    _localizationService.GetString("HudLegacyApplyFailedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                ResetGameOverlayPreviewAfterFailedApply(reloadConfiguration: true);
                return false;
            }

            if (result.RestartRequired)
            {
                string key = request.LegacyEnabled
                    ? "HudLegacyRestartEnabledMessage"
                    : "HudLegacyRestartDisabledMessage";
                System.Windows.MessageBox.Show(
                    _gameOverlaySettingsWindow,
                    _localizationService.Format(
                        key,
                        result.ExecutableName ?? Path.GetFileName(request.LegacyExecutablePath)),
                    _localizationService.GetString("HudLegacyRestartTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        _gameOverlayWindow?.SetMonitorPositions(_settings.GameOverlayMonitorPositions);
        _gameOverlayWindow?.SetLayoutMode(_settings.GameOverlayLayoutMode);
        _gameOverlayWindow?.SetLayout(_settings.GameOverlayPreset, metrics);
        TimeSpan samplingInterval = ResolveGameOverlaySamplingInterval(sampling);
        _monitorService?.SetSamplingInterval(samplingInterval);
        _gameOverlayController?.SetSamplingInterval(samplingInterval);
        _trayIcon?.SetGameOverlayMetrics(metrics);
        return true;
    }

    private static TimeSpan ResolveGameOverlaySamplingInterval(string? sampling) =>
        sampling?.Trim().ToLowerInvariant() switch
        {
            "low" => TimeSpan.FromSeconds(2),
            "high" => TimeSpan.FromMilliseconds(500),
            _ => TimeSpan.FromSeconds(1)
        };

    private void RemoveOverlayMonitorPosition(OverlayMonitorIdentity identity)
    {
        List<GameOverlayMonitorPositionSettings> positions =
            _settings.GameOverlayMonitorPositions ??= [];
        positions.RemoveAll(position => identity.IsFallback
            ? position.IsFallbackIdentity &&
              string.Equals(position.GdiDeviceName, identity.GdiDeviceName, StringComparison.OrdinalIgnoreCase) &&
              position.Left == identity.Bounds.Left && position.Top == identity.Bounds.Top &&
              position.Right == identity.Bounds.Right && position.Bottom == identity.Bounds.Bottom
            : !position.IsFallbackIdentity &&
              string.Equals(position.StableMonitorId, identity.StableMonitorId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameOverlayMonitorSnapshot(
        OverlayMonitorIdentity left,
        OverlayMonitorIdentity right) =>
        string.Equals(left.StableMonitorId, right.StableMonitorId, StringComparison.Ordinal) &&
        left.Bounds == right.Bounds;

    private GameOverlayAppearanceWindow EnsureGameOverlayAppearanceWindow()
    {
        if (_gameOverlayAppearanceWindow is not null)
        {
            return _gameOverlayAppearanceWindow;
        }

        var window = new GameOverlayAppearanceWindow();
        window.PreviewChanged += OnGameOverlayAppearancePreviewChanged;
        window.Applied += OnGameOverlayAppearanceApplied;
        _gameOverlayAppearanceWindow = window;
        return window;
    }

    private void OnGameOverlayAppearancePreviewChanged(GameOverlayAppearance appearance) =>
        _gameOverlayWindow?.SetAppearance(appearance);

    private void OnGameOverlayAppearanceApplied(GameOverlayAppearance appearance)
    {
        _settings.GameOverlayAppearance = GameOverlayAppearanceSettings.FromEffective(appearance);
        PatchSettings(settings => settings.GameOverlayAppearance =
            GameOverlayAppearanceSettings.FromEffective(appearance));
        _gameOverlayWindow?.SetAppearance(appearance);
    }

    private void OnGameSafeModeChangeRequested(bool enabled)
    {
        if (!enabled)
        {
            MessageBoxResult confirmation = System.Windows.MessageBox.Show(
                _localizationService.GetString("TrayGameSafeModeDisableMessage"),
                _localizationService.GetString("TrayGameSafeModeDisableTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                _trayIcon?.SetGameSafeMode(true);
                return;
            }
        }

        _settings.GameSafeMode = enabled;
        if (!TryPatchSettings(settings => settings.GameSafeMode = enabled))
        {
            _settings.GameSafeMode = !enabled;
        }

        _trayIcon?.SetGameSafeMode(_settings.GameSafeMode);
        _trayIcon?.ShowGameSafeModeRestartRequired();
    }

    private async void OnExitRequested(object? sender, EventArgs e) => await ExitAsync();

    private void SavePanelPosition()
    {
        if (_detailWindow is { WindowState: WindowState.Normal } &&
            double.IsFinite(_detailWindow.Left) && double.IsFinite(_detailWindow.Top))
        {
            _settings.PanelLeft = _detailWindow.Left;
            _settings.PanelTop = _detailWindow.Top;
        }

        _settings.PanelTopmost = _detailWindow?.IsPinned ?? _settings.PanelTopmost;
        PatchSettings(settings =>
        {
            settings.PanelLeft = _settings.PanelLeft;
            settings.PanelTop = _settings.PanelTop;
            settings.PanelTopmost = _settings.PanelTopmost;
        });
    }

    private bool TryPatchSettings(Action<AppSettings> patch)
    {
        if (!_settingsService.TryPatch(patch, out SettingsSnapshot snapshot))
        {
            return false;
        }

        _settings = snapshot.Settings;
        return true;
    }

    private void PatchSettings(Action<AppSettings> patch)
    {
        if (!TryPatchSettings(patch))
        {
            _settings = _settingsService.GetConfirmedSnapshot();
            BandDiagnostics.LogRateLimited(
                "settings-save-failed",
                "settings patch could not be persisted; restored confirmed snapshot",
                TimeSpan.FromSeconds(30));
        }
    }

    private async Task ExitAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;

        try
        {
            DisposeControlEvents();
            StopBandRecreateTimer();
            SavePanelPosition();

            if (_monitorService is not null)
            {
                _monitorService.SnapshotUpdated -= OnSnapshotUpdated;
                _snapshotRefreshScheduler?.Dispose();
                _snapshotRefreshScheduler = null;
                Interlocked.Exchange(ref _pendingSnapshot, null);
                await _monitorService.DisposeAsync();
            }

            if (_gameOverlayHotkey is not null)
            {
                _gameOverlayHotkey.Pressed -= OnGameOverlayHotkeyPressed;
                _gameOverlayHotkey.Dispose();
                _gameOverlayHotkey = null;
            }

            if (_gameOverlayController is not null)
            {
                _gameOverlayController.StateChanged -= OnGameOverlayStateChanged;
                try
                {
                    await _gameOverlayController.DisposeAsync();
                }
                catch (Exception exception)
                {
                    LogException("Game overlay shutdown failed", exception);
                }
                finally
                {
                    _gameOverlayController = null;
                }
            }

            if (_gameOverlayFrameProvider is not null)
            {
                try
                {
                    await _gameOverlayFrameProvider.DisposeAsync();
                }
                catch (Exception exception)
                {
                    LogException("Frame-rate provider shutdown failed", exception);
                }
                finally
                {
                    _gameOverlayFrameProvider = null;
                }
            }

            if (_gameOverlayWindow is not null)
            {
                try
                {
                    _gameOverlayWindow.Close();
                }
                catch (Exception exception)
                {
                    LogException("Game overlay window shutdown failed", exception);
                }
                finally
                {
                    _gameOverlayWindow = null;
                }
            }

            if (_gameOverlayAppearanceWindow is not null)
            {
                try
                {
                    _gameOverlayAppearanceWindow.PreviewChanged -= OnGameOverlayAppearancePreviewChanged;
                    _gameOverlayAppearanceWindow.Applied -= OnGameOverlayAppearanceApplied;
                    _gameOverlayAppearanceWindow.Close();
                }
                catch (Exception exception)
                {
                    LogException("Game overlay appearance window shutdown failed", exception);
                }
                finally
                {
                    _gameOverlayAppearanceWindow = null;
                }
            }

            if (_gameOverlaySettingsWindow is not null)
            {
                try
                {
                    RestoreGameOverlayPreviewBaseline();
                    _gameOverlaySettingsWindow.ApplyRequested = null;
                    _gameOverlaySettingsWindow.PreviewRequested = null;
                    _gameOverlaySettingsWindow.PreviewSessionFinished = null;
                    _gameOverlaySettingsWindow.CloseForExit();
                }
                catch (Exception exception)
                {
                    LogException("Game overlay settings window close failed", exception);
                }
                finally
                {
                    _gameOverlaySettingsWindow = null;
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
                    _appearanceSettingsWindow.AppearanceThemeApplied -= OnAppearanceThemeApplied;
                    _appearanceSettingsWindow.AppearancePreviewChanged -= OnAppearancePreviewChanged;
                    _appearanceSettingsWindow.ThemePreviewRequested -= OnThemePreviewRequested;
                    _appearanceSettingsWindow.ThemeImported -= OnThemeImported;
                    _appearanceSettingsWindow.ThemeImportRequested = null;
                    _appearanceSettingsWindow.UiCultureChanged -= OnUiCultureChanged;
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
                    _trayIcon.ToggleGameOverlayRequested -= OnToggleGameOverlayRequested;
                    _trayIcon.SelectGameOverlayTargetRequested -= OnSelectGameOverlayTargetRequested;
                    _trayIcon.GameOverlayPositionChanged -= OnGameOverlayPositionChanged;
                    _trayIcon.GameOverlayPresetChanged -= OnGameOverlayPresetChanged;
                    _trayIcon.GameOverlayMetricsChanged -= OnGameOverlayMetricsChanged;
                    _trayIcon.GameOverlayAppearanceRequested -= OnGameOverlayAppearanceRequested;
                    _trayIcon.GameOverlayConfigurationRequested -= OnGameOverlayConfigurationRequested;
                    _trayIcon.AppearanceSettingsRequested -= OnAppearanceSettingsRequested;
                    _trayIcon.GameSafeModeChangeRequested -= OnGameSafeModeChangeRequested;
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

            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch
            {
            }

            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
        catch (Exception ex)
        {
            LogException("Error during ExitAsync", ex);
        }
        finally
        {
            Shutdown();
            Environment.Exit(0);
        }
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

    private void PositionDetailAboveBand(DetailWindow detail, BandWindow? clickBand)
    {
        detail.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double detailWidth = detail.ActualWidth > 0 ? detail.ActualWidth : (double.IsFinite(detail.Width) && detail.Width > 0 ? detail.Width : 480);
        double detailHeight = detail.ActualHeight > 0 ? detail.ActualHeight : (detail.DesiredSize.Height > 0 ? detail.DesiredSize.Height : (double.IsFinite(detail.Height) && detail.Height > 0 ? detail.Height : 620));

        Rect workArea = SystemParameters.WorkArea;
        double targetLeft;
        double targetTop = Math.Max(workArea.Top + 10, workArea.Bottom - detailHeight - 12);

        nint bandHandle = clickBand?.NativeHandle ?? _bandHandle;
        if (bandHandle != nint.Zero && TaskbarPositioner.IsWindowHandleAlive(bandHandle) &&
            GetWindowRect(bandHandle, out NativeRect bandRect) && bandRect.Right > bandRect.Left)
        {
            double dpiScale = DetailWindow.ResolvePlacementDpiScale(
                GetDpiForWindow(bandHandle));

            nint hMonitor = MonitorFromWindow(bandHandle, 2);
            if (hMonitor != nint.Zero)
            {
                var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    workArea = new Rect(
                        mi.rcWork.Left / dpiScale,
                        mi.rcWork.Top / dpiScale,
                        (mi.rcWork.Right - mi.rcWork.Left) / dpiScale,
                        (mi.rcWork.Bottom - mi.rcWork.Top) / dpiScale);
                }
            }

            double bandCenterDip = ((bandRect.Left + bandRect.Right) / 2.0) / dpiScale;
            targetLeft = bandCenterDip - (detailWidth / 2.0);
            if (targetLeft + detailWidth > workArea.Right - 14)
            {
                targetLeft = workArea.Right - detailWidth - 14;
            }

            if (targetLeft < workArea.Left + 14)
            {
                targetLeft = workArea.Left + 14;
            }

            double bandTopDip = bandRect.Top / dpiScale;
            if (bandTopDip > workArea.Top + detailHeight + 20)
            {
                targetTop = bandTopDip - detailHeight - 8;
            }
            else
            {
                targetTop = (bandRect.Bottom / dpiScale) + 8;
            }
        }
        else
        {
            targetLeft = Math.Max(workArea.Left + 16, workArea.Right - detailWidth - 16);
            targetTop = Math.Max(workArea.Top + 16, workArea.Bottom - detailHeight - 12);
        }

        detail.Left = Math.Clamp(targetLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - detailWidth));
        detail.Top = Math.Clamp(targetTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - detailHeight));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);
}
