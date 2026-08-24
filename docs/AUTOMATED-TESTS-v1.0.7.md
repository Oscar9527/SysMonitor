# SysMonitor v1.0.7 自动化测试完整清单

## 核对口径

实际执行命令：

```powershell
dotnet test SysMonitor.Tests\SysMonitor.Tests.csproj -c Release --no-restore
```

最近一次串行执行结果：失败 0、通过 368、跳过 0、总计 368。此前的 365 项基础上，针对本次启动器误识别新增了 3 个参数化回归用例。

用例发现命令：

```powershell
dotnet test SysMonitor.Tests\SysMonitor.Tests.csproj -c Release --no-restore --no-build --list-tests
```

以下清单逐字保留 VSTest 返回的用例显示标识。Theory 的每个参数组合都会展开成独立用例；极长参数可能由 VSTest 以 `···` 截断显示。

重要限制：这些主要是单元测试或受控集成测试，不等于 368 次真实游戏实测。

## 汇总

- 测试类：42
- 展开后的测试用例：368
- 原始标识符 SHA-256：`A21D76D31D754BD6C23FE86E4C1B2880088B8E7AE578D26D7EEC5C4E8C82DCC1`

| 测试类 | 用例数 | 测试范围 |
|---|---:|---|
| `AdaptiveFrameRateProviderTests` | 7 | RTSS 优先、PresentMon 回退、恢复与生命周期。 |
| `BandClickDebouncerTests` | 4 | 点击去抖与时间边界。 |
| `BandDiagnosticsTests` | 1 | 诊断限流、TTL 与容量淘汰。 |
| `BandLayoutTests` | 6 | 指标顺序、可见性、间距与宽度。 |
| `BandWindowHitTargetTests` | 2 | 鼠标命中区域与切换消息。 |
| `CpuFrequencyReaderTests` | 3 | CPU 频率读取、过滤与平均。 |
| `CpuTemperatureReaderTests` | 3 | 温度传感器异步生命周期。 |
| `DetailWindowShowPolicyTests` | 3 | 详情窗口激活与置顶策略。 |
| `DriveTelemetryTests` | 9 | 磁盘枚举、容量、故障宽限与快照。 |
| `ForegroundTargetTrackerTests` | 18 | 前台游戏筛选、黑名单与稳定确认，包括启动器和反作弊辅助进程。 |
| `FrameRateAggregatorTests` | 8 | 一秒 FPS 聚合、交换链选择与过期。 |
| `GameOverlayAppearanceTests` | 3 | HUD 背景、描边、颜色与指标选择。 |
| `GameOverlayControllerTests` | 6 | HUD 显隐、切换、定位与采样频率。 |
| `GameOverlayNativeTests` | 34 | 窗口样式、置顶、DPI/坐标、布局与 FPS 行。 |
| `GameOverlaySettingsLogicTests` | 4 | HUD 坐标、预览与重置。 |
| `GameOverlayWindowTrackerTests` | 6 | 窗口移动、最小化、销毁与事件合并。 |
| `GlobalHotkeyServiceTests` | 3 | 热键注册、冲突与注销。 |
| `GpuCapabilityStabilizerTests` | 3 | GPU 能力稳定判断。 |
| `GpuSensorSelectorTests` | 11 | 多厂商 GPU 传感器选择与异常值。 |
| `GpuTelemetryCoordinatorTests` | 9 | GPU 数据源合并、选择与生命周期。 |
| `HelperProcessDispatcherTests` | 9 | 辅助进程参数验证。 |
| `HistorySparklineTests` | 11 | 历史曲线几何、范围、线程与渲染。 |
| `LiveSharedMemoryProbeTests` | 1 | 只读探测本机共享内存生产者。 |
| `LocalizationTests` | 14 | 语言解析、资源键与动态切换。 |
| `MemoryFrequencyReaderTests` | 3 | 内存频率首选、回退与过滤。 |
| `MetricHistoryTests` | 9 | 历史缓冲区、窗口、顺序与快照。 |
| `MonitorServiceTests` | 1 | 启动失败清理与重试。 |
| `NvidiaSmiParsingTests` | 5 | nvidia-smi 解析与坏数据。 |
| `OverlayMonitorIdentityResolverTests` | 6 | 显示器标识、重复项与负坐标。 |
| `PresentMonCsvParserTests` | 9 | PresentMon CSV 字段和边界验证。 |
| `PresentMonProcessSupportTests` | 13 | PresentMon 参数、会话、提权与停止。 |
| `ProcessExecutablePathResolverTests` | 3 | 进程路径与非法 PID。 |
| `RtssLegacyCompatibilityServiceTests` | 14 | RTSS 配置、备份、冲突与恢复。 |
| `RtssSharedMemoryParserTests` | 7 | RTSS 版本、PID、FPS、过期与异常值。 |
| `SettingsServiceTests` | 35 | 设置迁移、保存、恢复、并发与布局。 |
| `TaskbarMotionTrackerTests` | 2 | 任务栏移动事件合并与释放。 |
| `TaskbarPlacementStabilizerTests` | 18 | 任务栏约束、边界、宽度与隐藏。 |
| `ThemeCatalogTests` | 13 | 主题目录导入、并发与安全。 |
| `ThemePackageTests` | 32 | 主题包路径、大小、图片、JSON 与版本安全。 |
| `ThemeResourceApplierTests` | 1 | 主题资源应用与恢复。 |
| `TrayIconServiceTests` | 12 | 托盘菜单、DPI、子菜单与 HUD 状态。 |
| `UiRefreshSchedulerTests` | 7 | 刷新间隔、合并、调整、失效与释放。 |

## 368 项逐条清单

### 1. AdaptiveFrameRateProviderTests（7 项）

RTSS 优先、PresentMon 回退、恢复与生命周期。

001. `SysMonitor.Tests.AdaptiveFrameRateProviderTests.ExistingRtssSampleDoesNotStartPresentMonFallback`
002. `SysMonitor.Tests.AdaptiveFrameRateProviderTests.ZeroFpsIsAValidRtssSample`
003. `SysMonitor.Tests.AdaptiveFrameRateProviderTests.FallsBackRecoversAndStartIsIdempotent`
004. `SysMonitor.Tests.AdaptiveFrameRateProviderTests.GameSafeOptionsKeepGpuCompatibilityOffButEnableIndependentCpuTemperature`
005. `SysMonitor.Tests.AdaptiveFrameRateProviderTests.CompatibilityOptionsEnableBothHardwareSensorReaders`
006. `SysMonitor.Tests.AdaptiveFrameRateProviderTests.FactoryCreatesAdaptiveProvider`
007. `SysMonitor.Tests.AdaptiveFrameRateProviderTests.StopBeforeFallbackDelayPreventsLatePresentMonStart`

### 2. BandClickDebouncerTests（4 项）

点击去抖与时间边界。

008. `SysMonitor.Tests.BandClickDebouncerTests.FirstTimestampIsAccepted`
009. `SysMonitor.Tests.BandClickDebouncerTests.ExactBoundaryIsAccepted`
010. `SysMonitor.Tests.BandClickDebouncerTests.TimestampRollbackStartsANewInterval`
011. `SysMonitor.Tests.BandClickDebouncerTests.RepeatedTimestampsDoNotExtendSuppressionFromLastAcceptedClick`

### 3. BandDiagnosticsTests（1 项）

诊断限流、TTL 与容量淘汰。

012. `SysMonitor.Tests.BandDiagnosticsTests.RateLimitedKeysUseTtlAndDeterministicBoundedEviction`

### 4. BandLayoutTests（6 项）

指标顺序、可见性、间距与宽度。

013. `SysMonitor.Tests.BandLayoutTests.GroupsRemainInCanonicalOrderAndSeparatorsAreAdjacentCount`
014. `SysMonitor.Tests.BandLayoutTests.CompactAlwaysOmitsDiskAndCanShowSingleMetric`
015. `SysMonitor.Tests.BandLayoutTests.GpuRequiresBothUserVisibilityAndStableCapability`
016. `SysMonitor.Tests.BandLayoutTests.WidthGrowsMonotonicallyWithVisibleGroupsAndSpacing`
017. `SysMonitor.Tests.BandLayoutTests.EquivalentInputsProduceEqualDescriptor`
018. `SysMonitor.Tests.BandLayoutTests.PowerMetricsIncreaseAllocatedWidth`

### 5. BandWindowHitTargetTests（2 项）

鼠标命中区域与切换消息。

019. `SysMonitor.Tests.BandWindowHitTargetTests.OnlyLeftButtonDownIsAToggleMessage`
020. `SysMonitor.Tests.BandWindowHitTargetTests.HitTargetUsesThemeIndependentAlphaOneBackground`

### 6. CpuFrequencyReaderTests（3 项）

CPU 频率读取、过滤与平均。

021. `SysMonitor.Tests.CpuFrequencyReaderTests.AveragesOnlyValidCurrentMhzValues`
022. `SysMonitor.Tests.CpuFrequencyReaderTests.MissingOrImplausibleValuesRemainUnknown`
023. `SysMonitor.Tests.CpuFrequencyReaderTests.NativeReadNeverFabricatesNonpositiveFrequency`

### 7. CpuTemperatureReaderTests（3 项）

温度传感器异步生命周期。

024. `SysMonitor.Tests.CpuTemperatureReaderTests.Start_WhenHardwareOpenBlocks_ReturnsImmediatelyAndDoesNotBlockRead`
025. `SysMonitor.Tests.CpuTemperatureReaderTests.Dispose_WhileHardwareOpenBlocks_DiscardsLateOpenResult`
026. `SysMonitor.Tests.CpuTemperatureReaderTests.StopThenStart_DoesNotOverlapHardwareSessionsOrAcceptStaleResult`

### 8. DetailWindowShowPolicyTests（3 项）

详情窗口激活与置顶策略。

027. `SysMonitor.Tests.DetailWindowShowPolicyTests.BandShowRaisesWithoutActivation`
028. `SysMonitor.Tests.DetailWindowShowPolicyTests.PinnedBandShowRetainsTopmostZOrderWithoutActivation`
029. `SysMonitor.Tests.DetailWindowShowPolicyTests.TrayShowKeepsActivationAndSkipsNativeRaise`

### 9. DriveTelemetryTests（9 项）

磁盘枚举、容量、故障宽限与快照。

030. `SysMonitor.Tests.DriveTelemetryTests.SuccessfulEnumeration_FiltersAndSortsSystemDriveFirst`
031. `SysMonitor.Tests.DriveTelemetryTests.SuccessfulEnumeration_ClampsFreeUsedAndPercent(free: -10, total: 100, expectedUsed: 100, expectedPercent: 100)`
032. `SysMonitor.Tests.DriveTelemetryTests.SuccessfulEnumeration_ClampsFreeUsedAndPercent(free: 200, total: 100, expectedUsed: 0, expectedPercent: 0)`
033. `SysMonitor.Tests.DriveTelemetryTests.SuccessfulEnumeration_ClampsFreeUsedAndPercent(free: 25, total: 100, expectedUsed: 75, expectedPercent: 75)`
034. `SysMonitor.Tests.DriveTelemetryTests.GlobalFailure_KeepsLastSuccessfulSnapshot`
035. `SysMonitor.Tests.DriveTelemetryTests.PropertyFailure_AllowsTwoCyclesThenRemovesDrive`
036. `SysMonitor.Tests.DriveTelemetryTests.PropertyFailure_RecoveryResetsGraceCounter`
037. `SysMonitor.Tests.DriveTelemetryTests.MissingOrNotReadyDrive_IsRemovedImmediately`
038. `SysMonitor.Tests.DriveTelemetryTests.MonitorSnapshot_DefaultFixedDrives_IsAlwaysSafeAndImmutable`

### 10. ForegroundTargetTrackerTests（18 项）

前台游戏筛选、黑名单与稳定确认，包括启动器和反作弊辅助进程。

039. `SysMonitor.Tests.ForegroundTargetTrackerTests.Policy_ExcludesKnownNonGameProcessesAndShellClasses(processName: "explorer", windowClass: "GameWindow")`
040. `SysMonitor.Tests.ForegroundTargetTrackerTests.Policy_ExcludesKnownNonGameProcessesAndShellClasses(processName: "dwm.exe", windowClass: "GameWindow")`
041. `SysMonitor.Tests.ForegroundTargetTrackerTests.Policy_ExcludesKnownNonGameProcessesAndShellClasses(processName: "ShellExperienceHost", windowClass: "GameWindow")`
042. `SysMonitor.Tests.ForegroundTargetTrackerTests.Policy_ExcludesKnownNonGameProcessesAndShellClasses(processName: "ChatGPT.exe", windowClass: "Chrome_WidgetWin_1")`
043. `SysMonitor.Tests.ForegroundTargetTrackerTests.Policy_ExcludesKnownNonGameProcessesAndShellClasses(processName: "codex", windowClass: "GameWindow")`
044. `SysMonitor.Tests.ForegroundTargetTrackerTests.Policy_ExcludesKnownNonGameProcessesAndShellClasses(processName: "codex-code-mode-host.exe", windowClass: "GameWindow")`
045. `SysMonitor.Tests.ForegroundTargetTrackerTests.Policy_ExcludesKnownNonGameProcessesAndShellClasses(processName: "DesktopMgr64.exe", windowClass: "GameWindow")`
046. `SysMonitor.Tests.ForegroundTargetTrackerTests.Policy_ExcludesKnownNonGameProcessesAndShellClasses(processName: "delta_force_launcher.exe", windowClass: "GameWindow")`
047. `SysMonitor.Tests.ForegroundTargetTrackerTests.Policy_ExcludesKnownNonGameProcessesAndShellClasses(processName: "ACE-Helper.exe", windowClass: "GameWindow")`
048. `SysMonitor.Tests.ForegroundTargetTrackerTests.Policy_ExcludesKnownNonGameProcessesAndShellClasses(processName: "game", windowClass: "Shell_TrayWnd")`
049. `SysMonitor.Tests.ForegroundTargetTrackerTests.Policy_ExcludesKnownNonGameProcessesAndShellClasses(processName: "game", windowClass: "Progman")`
050. `SysMonitor.Tests.ForegroundTargetTrackerTests.Policy_ExcludesKnownNonGameProcessesAndShellClasses(processName: "game", windowClass: "WorkerW")`
051. `SysMonitor.Tests.ForegroundTargetTrackerTests.Policy_ExcludesCurrentProcessAndInvalidOrExitedTargets`
052. `SysMonitor.Tests.ForegroundTargetTrackerTests.ManualTargetCanBeSetWithoutGameNameHeuristic`
053. `SysMonitor.Tests.ForegroundTargetTrackerTests.Stabilize_RequiresThreeMatchingSamplesAcrossFiveHundredMilliseconds`
054. `SysMonitor.Tests.ForegroundTargetTrackerTests.Stabilize_RejectsChangedForegroundIdentity`
055. `SysMonitor.Tests.ForegroundTargetTrackerTests.RecentTarget_RejectsStaleAndPidReuse`
056. `SysMonitor.Tests.ForegroundTargetTrackerTests.WaitingForTarget_IsCancellableWithoutSelectingBackgroundProcess`

### 11. FrameRateAggregatorTests（8 项）

一秒 FPS 聚合、交换链选择与过期。

057. `SysMonitor.Tests.FrameRateAggregatorTests.ComputesOneSecondPerSwapchainIntervals`
058. `SysMonitor.Tests.FrameRateAggregatorTests.RejectsNonMonotonicTimePerSwapchain`
059. `SysMonitor.Tests.FrameRateAggregatorTests.ChallengerNeedsTwoUpdatedWindowsAtLeastTwentyFivePercentFaster`
060. `SysMonitor.Tests.FrameRateAggregatorTests.StaleCurrentSwitchesAndGlobalReceiveStaleClearsFps`
061. `SysMonitor.Tests.FrameRateAggregatorTests.CurrentChainFpsIsRetainedUntilGlobalTwoSecondStaleBoundary`
062. `SysMonitor.Tests.FrameRateAggregatorTests.PrunesStaleChainsAndBoundsUniqueSwapchains`
063. `SysMonitor.Tests.FrameRateAggregatorTests.PreservesActiveSelectionWhileEvictingUnselectedChains`
064. `SysMonitor.Tests.FrameRateAggregatorTests.NeverEvictsJustAddedChainWhenTheCapIsReached`

### 12. GameOverlayAppearanceTests（3 项）

HUD 背景、描边、颜色与指标选择。

065. `SysMonitor.Tests.GameOverlayAppearanceTests.HudHasNoBlackSurfaceAndUsesConfiguredTextOutline`
066. `SysMonitor.Tests.GameOverlayAppearanceTests.HudUsesUnifiedLineThemeColorsLikeMsiAfterburner`
067. `SysMonitor.Tests.GameOverlayAppearanceTests.HorizontalHudHonorsMemorySelectionAndDoesNotForceCpuOrGpu`

### 13. GameOverlayControllerTests（6 项）

HUD 显隐、切换、定位与采样频率。

068. `SysMonitor.Tests.GameOverlayControllerTests.HideDuringStart_CancelsWaitStopsProviderAndNeverStaleReshows`
069. `SysMonitor.Tests.GameOverlayControllerTests.TrayToggleAlwaysStartsAvailableOverlay`
070. `SysMonitor.Tests.GameOverlayControllerTests.TargetInvalidated_KeepsOverlayVisibleWhenDesiredVisible`
071. `SysMonitor.Tests.GameOverlayControllerTests.SwitchingToNonGameForeground_HidesOverlay_AndSwitchingBackShowsOverlay`
072. `SysMonitor.Tests.GameOverlayControllerTests.SwitchingTargetsRepositionsBeforeSlowFrameProviderRestartCompletes`
073. `SysMonitor.Tests.GameOverlayControllerTests.VisibleMetricsFollowConfiguredSamplingInterval`

### 14. GameOverlayNativeTests（34 项）

窗口样式、置顶、DPI/坐标、布局与 FPS 行。

074. `SysMonitor.Tests.GameOverlayNativeTests.ApplyNoActivateStyles_AddsRequiredStylesAndRemovesAppWindow`
075. `SysMonitor.Tests.GameOverlayNativeTests.ZOrder_AlwaysMaintainsTopmostTierToPreventWindowSwitchLoss`
076. `SysMonitor.Tests.GameOverlayNativeTests.Placement_UsesTargetMonitorDpiAndSupportsNegativeCoordinates`
077. `SysMonitor.Tests.GameOverlayNativeTests.Placement_ClampsOversizedOverlayToWorkingArea`
078. `SysMonitor.Tests.GameOverlayNativeTests.Placement_UsesWindowClientAreaAsItsCoordinateSpace`
079. `SysMonitor.Tests.GameOverlayNativeTests.PlacementSupportsConfiguredTopHorizontalPosition(position: 0, expectedLeft: 104)`
080. `SysMonitor.Tests.GameOverlayNativeTests.PlacementSupportsConfiguredTopHorizontalPosition(position: 50, expectedLeft: 650)`
081. `SysMonitor.Tests.GameOverlayNativeTests.PlacementSupportsConfiguredTopHorizontalPosition(position: 100, expectedLeft: 1196)`
082. `SysMonitor.Tests.GameOverlayNativeTests.ExactPlacementUsesPhysicalCoordinatesAndScalesSizeOnce(dpi: 96, expectedWidth: 300, expectedHeight: 100)`
083. `SysMonitor.Tests.GameOverlayNativeTests.ExactPlacementUsesPhysicalCoordinatesAndScalesSizeOnce(dpi: 144, expectedWidth: 450, expectedHeight: 150)`
084. `SysMonitor.Tests.GameOverlayNativeTests.ExactPlacementUsesPhysicalCoordinatesAndScalesSizeOnce(dpi: 192, expectedWidth: 600, expectedHeight: 200)`
085. `SysMonitor.Tests.GameOverlayNativeTests.ExactPlacementClampsRightBottomAndOversizedHud`
086. `SysMonitor.Tests.GameOverlayNativeTests.ExactPositionMatchesStableMonitorAndRejectsDuplicates`
087. `SysMonitor.Tests.GameOverlayNativeTests.PreviewPositionClonesMapPreservesOtherMonitorsAndNeverMutatesBaseline`
088. `SysMonitor.Tests.GameOverlayNativeTests.CoordinateContextDetectsTargetMovingToAnotherMonitor`
089. `SysMonitor.Tests.GameOverlayNativeTests.HorizontalMetricContainsOnlyUsageAndTemperature(usage: "42%", temperature: "71°C", expected: "42%  71°C")`
090. `SysMonitor.Tests.GameOverlayNativeTests.HorizontalMetricContainsOnlyUsageAndTemperature(usage: "--", temperature: "", expected: "--  --")`
091. `SysMonitor.Tests.GameOverlayNativeTests.HudUsesShortLocalizedEtwDiagnostic`
092. `SysMonitor.Tests.GameOverlayNativeTests.HudUsesShortLocalizedEtwResourceDiagnostic`
093. `SysMonitor.Tests.GameOverlayNativeTests.HudDoesNotExposeEnglishUnavailableState(status: Unavailable, expected: "未启用")`
094. `SysMonitor.Tests.GameOverlayNativeTests.HudDoesNotExposeEnglishUnavailableState(status: WaitingForTarget, expected: "未选择目标")`
095. `SysMonitor.Tests.GameOverlayNativeTests.HudDoesNotExposeEnglishUnavailableState(status: Starting, expected: "正在采集")`
096. `SysMonitor.Tests.GameOverlayNativeTests.HudDoesNotExposeEnglishUnavailableState(status: NoFrames, expected: "")`
097. `SysMonitor.Tests.GameOverlayNativeTests.HudDoesNotExposeEnglishUnavailableState(status: Faulted, expected: "采集失败")`
098. `SysMonitor.Tests.GameOverlayNativeTests.HudSilentlyShowsPlaceholderWhenNoFramesAreAvailable`
099. `SysMonitor.Tests.GameOverlayNativeTests.HudHidesFrameRateRowWhenNoFramesAreAvailable`
100. `SysMonitor.Tests.GameOverlayNativeTests.HudHidesFrameRateRowWhenFrameProviderFaults`
101. `SysMonitor.Tests.GameOverlayNativeTests.HudShowsFrameRateRowOnlyForFiniteActiveValue`
102. `SysMonitor.Tests.GameOverlayNativeTests.RivatunerLayoutUsesRowsAndOnlySelectedMetrics`
103. `SysMonitor.Tests.GameOverlayNativeTests.DetailedLayoutIncludesConfiguredMemoryFrequency`
104. `SysMonitor.Tests.GameOverlayNativeTests.HorizontalLayoutIncludesEveryEnabledMetricIncludingMemory`
105. `SysMonitor.Tests.GameOverlayNativeTests.HorizontalLayoutRemovesOnlyUnavailableFrameRate`
106. `SysMonitor.Tests.GameOverlayNativeTests.MetricOrder_DefaultPlacesCpuGpuMemoryFpsInOrder`
107. `SysMonitor.Tests.GameOverlayNativeTests.MetricOrder_LegacyDefaultsAutoUpgradeToCpuGpuMemFps`

### 15. GameOverlaySettingsLogicTests（4 项）

HUD 坐标、预览与重置。

108. `SysMonitor.Tests.GameOverlaySettingsLogicTests.UnrelatedApplyDoesNotCreateExactPosition`
109. `SysMonitor.Tests.GameOverlaySettingsLogicTests.ExplicitCoordinatesProduceSetRequest`
110. `SysMonitor.Tests.GameOverlaySettingsLogicTests.ResetAndInvalidTextAreDistinct`
111. `SysMonitor.Tests.GameOverlaySettingsLogicTests.PreviewSessionFinalizationIsIdempotent`

### 16. GameOverlayWindowTrackerTests（6 项）

窗口移动、最小化、销毁与事件合并。

112. `SysMonitor.Tests.GameOverlayWindowTrackerTests.LocationChange_RequiresTopLevelTargetWindowObject`
113. `SysMonitor.Tests.GameOverlayWindowTrackerTests.MoveAndMinimizeEvents_RequireExactTargetAndIgnoreOverlay`
114. `SysMonitor.Tests.GameOverlayWindowTrackerTests.ForegroundEvents_AreRelevantForRevalidationButNoTargetIsNot`
115. `SysMonitor.Tests.GameOverlayWindowTrackerTests.Coalescing_PreservesAllWorkKindsForOneRenderPass`
116. `SysMonitor.Tests.GameOverlayWindowTrackerTests.EventClassification_DistinguishesTemporaryMinimizeFromPermanentDestroy`
117. `SysMonitor.Tests.GameOverlayWindowTrackerTests.Identity_MatchesHwndPidAndProcessGeneration`

### 17. GlobalHotkeyServiceTests（3 项）

热键注册、冲突与注销。

118. `SysMonitor.Tests.GlobalHotkeyServiceTests.Registration_UsesFixedChordAndNoRepeat`
119. `SysMonitor.Tests.GlobalHotkeyServiceTests.RegistrationConflict_ProvidesDiagnosticWithoutClaimingRegistration`
120. `SysMonitor.Tests.GlobalHotkeyServiceTests.Dispose_UnregistersExactlyOnce`

### 18. GpuCapabilityStabilizerTests（3 项）

GPU 能力稳定判断。

121. `SysMonitor.Tests.GpuCapabilityStabilizerTests.RequiresTwoConsecutivePresentSamplesAndTransitionsOnce`
122. `SysMonitor.Tests.GpuCapabilityStabilizerTests.RequiresFiveConsecutiveMissingSamplesAndTransitionsOnce`
123. `SysMonitor.Tests.GpuCapabilityStabilizerTests.OppositeSampleBreaksConsecutiveRun`

### 19. GpuSensorSelectorTests（11 项）

多厂商 GPU 传感器选择与异常值。

124. `SysMonitor.Tests.GpuSensorSelectorTests.NvidiaUsesOnlyExactGpuCoreLoadAndTemperature`
125. `SysMonitor.Tests.GpuSensorSelectorTests.AmdDoesNotFallBackToControllerOrVideoLoads`
126. `SysMonitor.Tests.GpuSensorSelectorTests.IntelUsesMaximumD3dEngineAndExcludesOtherLoads`
127. `SysMonitor.Tests.GpuSensorSelectorTests.MemoryUsesMiBAndCanDeriveUsedFromTotalMinusFree`
128. `SysMonitor.Tests.GpuSensorSelectorTests.DedicatedD3dIsUsedOnlyFallbackAndSharedIsExcluded`
129. `SysMonitor.Tests.GpuSensorSelectorTests.InvalidMiBValuesRemainUnknown(value: -1)`
130. `SysMonitor.Tests.GpuSensorSelectorTests.InvalidMiBValuesRemainUnknown(value: NaN)`
131. `SysMonitor.Tests.GpuSensorSelectorTests.InvalidMiBValuesRemainUnknown(value: ∞)`
132. `SysMonitor.Tests.GpuSensorSelectorTests.VeryLargeFiniteMiBValueSaturatesSafely`
133. `SysMonitor.Tests.GpuSensorSelectorTests.ZeroCoreTemperatureAndZeroMemoryTotalRemainUnknown`
134. `SysMonitor.Tests.GpuSensorSelectorTests.CompatibilityClocksUseOnlyExactCoreAndMemoryNames`

### 20. GpuTelemetryCoordinatorTests（9 项）

GPU 数据源合并、选择与生命周期。

135. `SysMonitor.Tests.GpuTelemetryCoordinatorTests.FreshNvidiaSmiCycleSuppressesAllLhmNvidiaAdapters`
136. `SysMonitor.Tests.GpuTelemetryCoordinatorTests.SameModelNameWithoutComparableIdentityNeverMerges`
137. `SysMonitor.Tests.GpuTelemetryCoordinatorTests.ExactPciIdentityCanFillOnlyMissingMetrics`
138. `SysMonitor.Tests.GpuTelemetryCoordinatorTests.StaleSmiAllowsLhmNvidiaAndOutOfOrderCycleIsRejected`
139. `SysMonitor.Tests.GpuTelemetryCoordinatorTests.ChallengerNeedsTwoConsecutiveDisplayTicks`
140. `SysMonitor.Tests.GpuTelemetryCoordinatorTests.NoUsageRetainsCurrentFreshAdapter`
141. `SysMonitor.Tests.GpuTelemetryCoordinatorTests.LifecycleIsAwaitedAndIdempotent`
142. `SysMonitor.Tests.GpuTelemetryCoordinatorTests.SelectedFrequencyMetricsArePlumbedToSnapshot`
143. `SysMonitor.Tests.GpuTelemetryCoordinatorTests.SafeModeConstructorUsesInertCompatibilityProvider`

### 21. HelperProcessDispatcherTests（9 项）

辅助进程参数验证。

144. `SysMonitor.Tests.HelperProcessDispatcherTests.Classify_AcceptsExactCpuTemperatureRequest`
145. `SysMonitor.Tests.HelperProcessDispatcherTests.Classify_RejectsMalformedCpuTemperatureRequest(arguments: [])`
146. `SysMonitor.Tests.HelperProcessDispatcherTests.Classify_RejectsMalformedCpuTemperatureRequest(arguments: ["--cpu-temperature-helper"])`
147. `SysMonitor.Tests.HelperProcessDispatcherTests.Classify_RejectsMalformedCpuTemperatureRequest(arguments: ["--cpu-temperature-helper", "bad-pipe"])`
148. `SysMonitor.Tests.HelperProcessDispatcherTests.Classify_RejectsMalformedCpuTemperatureRequest(arguments: ["--cpu-temperature-helper", "SysMonitor.CpuTemperature.000000000000000000000000"···, "extra"])`
149. `SysMonitor.Tests.HelperProcessDispatcherTests.Classify_AcceptsExactPresentMonRequest`
150. `SysMonitor.Tests.HelperProcessDispatcherTests.Classify_RejectsMalformedPresentMonRequest(arguments: ["--presentmon-helper"])`
151. `SysMonitor.Tests.HelperProcessDispatcherTests.Classify_RejectsMalformedPresentMonRequest(arguments: ["--presentmon-helper", "bad-pipe", "123", "bad-session"])`
152. `SysMonitor.Tests.HelperProcessDispatcherTests.Classify_RejectsMalformedPresentMonRequest(arguments: ["--presentmon-helper", "SysMonitor.PresentMon.0000000000000000000000000000"···, "0", "SysMonitor-00000000000000000000000000000000"])`

### 22. HistorySparklineTests（11 项）

历史曲线几何、范围、线程与渲染。

153. `SysMonitor.Tests.HistorySparklineTests.EmptyAndZeroSizedInputsProduceNoGeometry`
154. `SysMonitor.Tests.HistorySparklineTests.SinglePointUsesExactSixtySecondDomainAndFixedPercentScale`
155. `SysMonitor.Tests.HistorySparklineTests.NullAndLargeGapCreateIndependentSegments`
156. `SysMonitor.Tests.HistorySparklineTests.GapAtThresholdRemainsConnected`
157. `SysMonitor.Tests.HistorySparklineTests.GpuZeroIsAValidBaselinePointWhileNullBreaksTheLine`
158. `SysMonitor.Tests.HistorySparklineTests.NonFiniteValuesAreSkippedAndFiniteOutOfRangeValuesClamp`
159. `SysMonitor.Tests.HistorySparklineTests.SamplesOlderThanWindowAreExcluded`
160. `SysMonitor.Tests.HistorySparklineTests.BrushPropertiesInvalidateRenderingAndUpdateSeriesKeepsImmutableSnapshot`
161. `SysMonitor.Tests.HistorySparklineTests.UpdateSeriesRejectsCallsFromNonOwnerThread`
162. `SysMonitor.Tests.HistorySparklineTests.CreatesAutomationPeerForAssistiveTechnology`
163. `SysMonitor.Tests.HistorySparklineTests.StaRenderSmokeHandlesSegmentsNullsAndSinglePoints`

### 23. LiveSharedMemoryProbeTests（1 项）

只读探测本机共享内存生产者。

164. `SysMonitor.Tests.LiveSharedMemoryProbeTests.ReadInstalledProducersWithoutChangingThem`

### 24. LocalizationTests（14 项）

语言解析、资源键与动态切换。

165. `SysMonitor.Tests.LocalizationTests.ResolveCulture_MapsSystemChineseVariantsToSimplifiedChinese`
166. `SysMonitor.Tests.LocalizationTests.ResolveCulture_MapsNonChineseSystemCultureToEnglish`
167. `SysMonitor.Tests.LocalizationTests.ResolveCulture_SystemUsesStartupUiCultureSnapshotAfterThreadCultureChanges`
168. `SysMonitor.Tests.LocalizationTests.ResolveCulture_HonorsExplicitSupportedCultures(preference: "en-US", expected: "en-US")`
169. `SysMonitor.Tests.LocalizationTests.ResolveCulture_HonorsExplicitSupportedCultures(preference: "EN-us", expected: "en-US")`
170. `SysMonitor.Tests.LocalizationTests.ResolveCulture_HonorsExplicitSupportedCultures(preference: "zh-CN", expected: "zh-CN")`
171. `SysMonitor.Tests.LocalizationTests.ResolveCulture_HonorsExplicitSupportedCultures(preference: "ZH-cn", expected: "zh-CN")`
172. `SysMonitor.Tests.LocalizationTests.NormalizeCulturePreference_InvalidValuesUseSystem(preference: null)`
173. `SysMonitor.Tests.LocalizationTests.NormalizeCulturePreference_InvalidValuesUseSystem(preference: "")`
174. `SysMonitor.Tests.LocalizationTests.NormalizeCulturePreference_InvalidValuesUseSystem(preference: " ")`
175. `SysMonitor.Tests.LocalizationTests.NormalizeCulturePreference_InvalidValuesUseSystem(preference: "de-DE")`
176. `SysMonitor.Tests.LocalizationTests.ResourceCultures_HaveMatchingKeysAndFormatPlaceholders`
177. `SysMonitor.Tests.LocalizationTests.DynamicDetailText_ChangesLanguageAndKeepsNeutralUnits`
178. `SysMonitor.Tests.LocalizationTests.ApplyCulture_RaisesCultureChangedWhenEffectiveCultureChanges`

### 25. MemoryFrequencyReaderTests（3 项）

内存频率首选、回退与过滤。

179. `SysMonitor.Tests.MemoryFrequencyReaderTests.ConfiguredClockSpeedTakesPrecedenceOverFallbackSpeed`
180. `SysMonitor.Tests.MemoryFrequencyReaderTests.FallsBackToReportedSpeedWhenConfiguredClockSpeedIsMissing`
181. `SysMonitor.Tests.MemoryFrequencyReaderTests.MissingOrImplausibleClockSpeedsRemainUnknown`

### 26. MetricHistoryTests（9 项）

历史缓冲区、窗口、顺序与快照。

182. `SysMonitor.Tests.MetricHistoryTests.CapacityWrapPreservesNewestPointsInChronologicalOrder`
183. `SysMonitor.Tests.MetricHistoryTests.DefaultBufferHasHardCapOfOneHundredTwentyPoints`
184. `SysMonitor.Tests.MetricHistoryTests.RealWindowRemovesOnlyPointsOlderThanExactBoundary`
185. `SysMonitor.Tests.MetricHistoryTests.NewProducerStartsFreshEpochAndMayRestartSequenceAndTimestamp`
186. `SysMonitor.Tests.MetricHistoryTests.DuplicateAndOutOfOrderSamplesAreRejectedWithoutMutatingSnapshot`
187. `SysMonitor.Tests.MetricHistoryTests.SequenceAndMonotonicTimestampAreTheOnlyOrderingInputs`
188. `SysMonitor.Tests.MetricHistoryTests.PercentValuesClampAndNonFiniteValuesBecomeNullWhileZeroRemainsValid`
189. `SysMonitor.Tests.MetricHistoryTests.SnapshotIsIndependentFromLaterRingMutations`
190. `SysMonitor.Tests.MetricHistoryTests.ConstructorRejectsInvalidClockWindowAndCapacity`

### 27. MonitorServiceTests（1 项）

启动失败清理与重试。

191. `SysMonitor.Tests.MonitorServiceTests.StartAsync_WhenGpuStartupFails_CleansUpAndAllowsRetry`

### 28. NvidiaSmiParsingTests（5 项）

nvidia-smi 解析与坏数据。

192. `SysMonitor.Tests.NvidiaSmiParsingTests.ParsesQuotedCommaAndEachMetricIndependently`
193. `SysMonitor.Tests.NvidiaSmiParsingTests.RejectsMalformedRequiredFieldsButAllowsMissingOptionalIdentity`
194. `SysMonitor.Tests.NvidiaSmiParsingTests.TimestampBoundaryPublishesPriorNonemptyPartialCycle`
195. `SysMonitor.Tests.NvidiaSmiParsingTests.CorruptRowsDoNotPublishOrContaminateCycle`
196. `SysMonitor.Tests.NvidiaSmiParsingTests.DuplicateIndexWithinTimestampIsCorrupt`

### 29. OverlayMonitorIdentityResolverTests（6 项）

显示器标识、重复项与负坐标。

197. `SysMonitor.Tests.OverlayMonitorIdentityResolverTests.ResolverReturnsAValidPrimaryMonitorSnapshot`
198. `SysMonitor.Tests.OverlayMonitorIdentityResolverTests.StablePathMatchesRegardlessOfNativePathCaseAndWhitespace`
199. `SysMonitor.Tests.OverlayMonitorIdentityResolverTests.FallbackRequiresExactGdiNameAndFullBounds`
200. `SysMonitor.Tests.OverlayMonitorIdentityResolverTests.RenamedOrMissingStablePathDoesNotMatchFallbackOrAnotherPath`
201. `SysMonitor.Tests.OverlayMonitorIdentityResolverTests.DuplicateStableIdsFailClosed`
202. `SysMonitor.Tests.OverlayMonitorIdentityResolverTests.NegativePhysicalBoundsArePreservedInFallbackIdentity`

### 30. PresentMonCsvParserTests（9 项）

PresentMon CSV 字段和边界验证。

203. `SysMonitor.Tests.PresentMonCsvParserTests.RequiresExactTenColumnHeader`
204. `SysMonitor.Tests.PresentMonCsvParserTests.ApplicationMayContainCommaBecausePidAndAddressAnchorTheRow`
205. `SysMonitor.Tests.PresentMonCsvParserTests.RejectsInvalidRuntimeValuesAndWrongTarget(line: "Game.exe,42,0x1,DXGI,1,0,0,NaN,0.1,16")`
206. `SysMonitor.Tests.PresentMonCsvParserTests.RejectsInvalidRuntimeValuesAndWrongTarget(line: "Game.exe,42,0x1,DXGI,1,0,0,1,0.1,Infinity")`
207. `SysMonitor.Tests.PresentMonCsvParserTests.RejectsInvalidRuntimeValuesAndWrongTarget(line: "Game.exe,42,0xNOPE,DXGI,1,0,0,1,0.1,16")`
208. `SysMonitor.Tests.PresentMonCsvParserTests.RejectsInvalidRuntimeValuesAndWrongTarget(line: "Game.exe,43,0x1,DXGI,1,0,0,1,0.1,16")`
209. `SysMonitor.Tests.PresentMonCsvParserTests.RejectsInvalidRuntimeValuesAndWrongTarget(line: "Game.exe,42,0x1,Vulkan,1,0,0,1,0.1,16")`
210. `SysMonitor.Tests.PresentMonCsvParserTests.RejectsInvalidRuntimeValuesAndWrongTarget(line: "Game.exe,42,0x1,DXGI,1,0,Maybe,1,0.1,16")`
211. `SysMonitor.Tests.PresentMonCsvParserTests.BoundedLineReaderRejectsOversizedRows`

### 31. PresentMonProcessSupportTests（13 项）

PresentMon 参数、会话、提权与停止。

212. `SysMonitor.Tests.PresentMonProcessSupportTests.CollectorArgumentsAreExactAndUnquotedArgumentListEntries`
213. `SysMonitor.Tests.PresentMonProcessSupportTests.TerminationArgumentsCanOnlyNameTheOwnedSession`
214. `SysMonitor.Tests.PresentMonProcessSupportTests.CollectorDoesNotRequestSelfElevationByDefault`
215. `SysMonitor.Tests.PresentMonProcessSupportTests.ElevatedHelperIsHiddenAndReceivesOnlyValidatedIdentifiers`
216. `SysMonitor.Tests.PresentMonProcessSupportTests.PresentMonHelperRequestRejectsUntrustedArguments`
217. `SysMonitor.Tests.PresentMonProcessSupportTests.DiagnosticCaptureIsBoundedButDrainsInput`
218. `SysMonitor.Tests.PresentMonProcessSupportTests.EmbeddedBinaryHasPinnedHash`
219. `SysMonitor.Tests.PresentMonProcessSupportTests.MissingTargetAndStopAreTruthfulAndIdempotent`
220. `SysMonitor.Tests.PresentMonProcessSupportTests.PersistedSessionCleanupAcceptsOnlyOwnedNames(value: "SysMonitor-123-0123456789abcdef0123456789abcdef", expected: True)`
221. `SysMonitor.Tests.PresentMonProcessSupportTests.PersistedSessionCleanupAcceptsOnlyOwnedNames(value: "PresentMon", expected: False)`
222. `SysMonitor.Tests.PresentMonProcessSupportTests.PersistedSessionCleanupAcceptsOnlyOwnedNames(value: "SysMonitor-owned", expected: False)`
223. `SysMonitor.Tests.PresentMonProcessSupportTests.PersistedSessionCleanupAcceptsOnlyOwnedNames(value: "SysMonitor-0-0123456789abcdef0123456789abcdef", expected: False)`
224. `SysMonitor.Tests.PresentMonProcessSupportTests.PersistedSessionCleanupAcceptsOnlyOwnedNames(value: "SysMonitor-123-not-a-guid", expected: False)`

### 32. ProcessExecutablePathResolverTests（3 项）

进程路径与非法 PID。

225. `SysMonitor.Tests.ProcessExecutablePathResolverTests.ResolvesCurrentProcessToExistingExecutable`
226. `SysMonitor.Tests.ProcessExecutablePathResolverTests.RejectsInvalidProcessIdentifiers(processId: 0)`
227. `SysMonitor.Tests.ProcessExecutablePathResolverTests.RejectsInvalidProcessIdentifiers(processId: -1)`

### 33. RtssLegacyCompatibilityServiceTests（14 项）

RTSS 配置、备份、冲突与恢复。

228. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.NewProfileUsesExactAsciiCrLfBytes`
229. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.SameBasenameAtAnotherCanonicalPathIsRefused`
230. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.QueryDoesNotCreateBackupDirectory`
231. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.ExistingProfilePreservesUnrelatedBytesAndRestoresExactly`
232. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.GlobalAndConfigAreNotTouched`
233. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.ModifiedProfileProducesConflictAndRetainsManifest`
234. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.MissingAppliedProfileProducesConflict`
235. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.ReenableAfterExternalModificationConflictsWithoutOverwrite`
236. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.IdempotentEnableAndDeletedExecutableDisable`
237. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.DuplicateHookingIsRejectedWithoutWrite`
238. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.ManagedEnumerationIncludesAppliedTarget`
239. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.PendingAppliedIsFinalizedAndRequiredOriginalMissingConflicts`
240. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.PendingOriginalAbsentAndMissingProfileIsCleanedUpBeforeReapply`
241. `SysMonitor.Tests.RtssLegacyCompatibilityServiceTests.TryAutoEnableForExecutableSuccessfullyAppliesProfile`

### 34. RtssSharedMemoryParserTests（7 项）

RTSS 版本、PID、FPS、过期与异常值。

242. `SysMonitor.Tests.RtssSharedMemoryParserTests.Version221FixtureUsesRollingFpsForExactPid`
243. `SysMonitor.Tests.RtssSharedMemoryParserTests.RollingFpsIsRejectedAfterItsSampleBecomesStale`
244. `SysMonitor.Tests.RtssSharedMemoryParserTests.RollingFpsAtFreshnessBoundaryRemainsValid`
245. `SysMonitor.Tests.RtssSharedMemoryParserTests.LegacyEntryUsesFrameTimeThenFrameCounterFormula`
246. `SysMonitor.Tests.RtssSharedMemoryParserTests.FreshnessUsesWrappingLow32TickArithmetic`
247. `SysMonitor.Tests.RtssSharedMemoryParserTests.RejectsStaleWrongSignatureWrongMajorAndInvalidBounds`
248. `SysMonitor.Tests.RtssSharedMemoryParserTests.ImplausibleRollingValueFallsBackButAllImplausibleValuesAreRejected`

### 35. SettingsServiceTests（35 项）

设置迁移、保存、恢复、并发与布局。

249. `SysMonitor.Tests.SettingsServiceTests.Load_LegacySettingsDefaultCultureAndPreserveAppearance`
250. `SysMonitor.Tests.SettingsServiceTests.Load_MissingGameSafeMode_MigratesToSafeDefault`
251. `SysMonitor.Tests.SettingsServiceTests.SaveAndLoad_ExplicitDisabledGameSafeModeRoundTrips`
252. `SysMonitor.Tests.SettingsServiceTests.Load_MigratesMissingOrBlankThemeToBuiltInDefault(json: "{}")`
253. `SysMonitor.Tests.SettingsServiceTests.Load_MigratesMissingOrBlankThemeToBuiltInDefault(json: "{\"ActiveThemeId\":null}")`
254. `SysMonitor.Tests.SettingsServiceTests.Load_MigratesMissingOrBlankThemeToBuiltInDefault(json: "{\"ActiveThemeId\":\"   \"}")`
255. `SysMonitor.Tests.SettingsServiceTests.TrySave_ReturnsFalseWhenSettingsDirectoryCannotBeCreated`
256. `SysMonitor.Tests.SettingsServiceTests.Load_TrimsExplicitThemeId`
257. `SysMonitor.Tests.SettingsServiceTests.Load_MigratesVisibilityFieldsWithoutOverwritingExplicitFalse(json: "{}", cpu: True, memory: True, gpu: True, download: True, upload: True, systemDisk: False)`
258. `SysMonitor.Tests.SettingsServiceTests.Load_MigratesVisibilityFieldsWithoutOverwritingExplicitFalse(json: "{\"BandMetricVisibility\":null}", cpu: True, memory: True, gpu: True, download: True, upload: True, systemDisk: False)`
259. `SysMonitor.Tests.SettingsServiceTests.Load_MigratesVisibilityFieldsWithoutOverwritingExplicitFalse(json: "{\"BandMetricVisibility\":{\"Cpu\":false}}", cpu: False, memory: True, gpu: True, download: True, upload: True, systemDisk: False)`
260. `SysMonitor.Tests.SettingsServiceTests.Load_MigratesVisibilityFieldsWithoutOverwritingExplicitFalse(json: "{\"BandMetricVisibility\":{\"Cpu\":null,\"Gpu\":fa"···, cpu: True, memory: True, gpu: False, download: True, upload: True, systemDisk: False)`
261. `SysMonitor.Tests.SettingsServiceTests.SaveAndLoad_RoundTripsAllVisibilityValues`
262. `SysMonitor.Tests.SettingsServiceTests.SaveAndLoad_PersistValidCultureAndAppearance`
263. `SysMonitor.Tests.SettingsServiceTests.Load_InvalidCultureNormalizesToSystem`
264. `SysMonitor.Tests.SettingsServiceTests.Load_DamagedFileReturnsSafeDefaults`
265. `SysMonitor.Tests.SettingsServiceTests.Save_NormalizesInvalidCultureBeforeSerialization`
266. `SysMonitor.Tests.SettingsServiceTests.SaveAndLoad_RoundTripsHudPresetAndMetricSelection`
267. `SysMonitor.Tests.SettingsServiceTests.Load_InvalidHudPresetUsesRivatunerDefault`
268. `SysMonitor.Tests.SettingsServiceTests.Load_OldSettingsUseVerticalLayoutAndLegacyPositionFallback`
269. `SysMonitor.Tests.SettingsServiceTests.SaveAndLoad_RoundTripsHorizontalLayoutAndPerMonitorCoordinates`
270. `SysMonitor.Tests.SettingsServiceTests.LivePreviewLeavesPersistedSettingsBytesAndInMemoryMapUnchanged`
271. `SysMonitor.Tests.SettingsServiceTests.Load_InvalidLayoutAndMonitorEntryFailClosed`
272. `SysMonitor.Tests.SettingsServiceTests.TryPatch_ExpectedRevisionConflictLeavesConfirmedUnchanged`
273. `SysMonitor.Tests.SettingsServiceTests.MaxRevision_IsObservedAndNextPatchWrapsToOne`
274. `SysMonitor.Tests.SettingsServiceTests.MaxRevision_TwoServicesObserveWrapAndResumeConflicts`
275. `SysMonitor.Tests.SettingsServiceTests.MaxRevision_IsObservedAndNextSaveWrapsToOne`
276. `SysMonitor.Tests.SettingsServiceTests.RevisionBeforeMax_PersistsMaxConsistentlyAcrossInstances`
277. `SysMonitor.Tests.SettingsServiceTests.TryPatch_CallbackCanUseAnotherServiceWithoutHoldingPathLock`
278. `SysMonitor.Tests.SettingsServiceTests.SnapshotsAndRetainedPatchReferencesCannotMutateConfirmedState`
279. `SysMonitor.Tests.SettingsServiceTests.ConcurrentPatchesAreSerializedWithoutLostFields`
280. `SysMonitor.Tests.SettingsServiceTests.SeparateServiceInstancesSerializePatchesForSameSettingsPath`
281. `SysMonitor.Tests.SettingsServiceTests.ReloadUpdatesConfirmedSettingsAndRevisionTogether`
282. `SysMonitor.Tests.SettingsServiceTests.FailedPatchDoesNotPolluteConfirmedState`
283. `SysMonitor.Tests.SettingsServiceTests.CorruptMainFileRecoversFromBackup`

### 36. TaskbarMotionTrackerTests（2 项）

任务栏移动事件合并与释放。

284. `SysMonitor.Tests.TaskbarMotionTrackerTests.AcceptedEventStormRetainsOnePostedCallbackAndSchedulesOneProbe`
285. `SysMonitor.Tests.TaskbarMotionTrackerTests.DisposeBeforePostedCallbackRunsDoesNotProbe`

### 37. TaskbarPlacementStabilizerTests（18 项）

任务栏约束、边界、宽度与隐藏。

286. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.HorizontalTaskbarUsesParentRelativeCenteredY(taskbarTop: 0)`
287. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.HorizontalTaskbarUsesParentRelativeCenteredY(taskbarTop: 1040)`
288. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.VerticalTaskbarRequestsHide`
289. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.ConstraintContractsImmediatelyAndExpandsAfterTwoMatches`
290. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.ConstraintStabilizesLeftAndRightIndependently`
291. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.FailedProbeDoesNotExpandAndBreaksConsecutiveConfirmation`
292. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.RootGenerationChangeResetsConstraintAndFailedNewRootHasNoConstraint`
293. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.CachedSnapshotCannotConfirmPendingExpansion`
294. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.RejectedConfirmationClearsPendingWithoutDiscardingSafeConstraint`
295. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.TrustedUnsafeObservationImmediatelyDiscardsOldConstraint`
296. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.MatchingConfirmationExpandsConstraintWithoutMovingContainedBand`
297. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.SafeBoundaryJitterDoesNotMoveContainedBand(left: 99, right: 901)`
298. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.SafeBoundaryJitterDoesNotMoveContainedBand(left: 98, right: 902)`
299. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.OutOfBoundsBandIsClampedByMinimumDistance`
300. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.OneHundredPercentUsesNewFeasibleMaximumAfterWidthGrowth`
301. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.NoFeasibleIntervalRequestsHide`
302. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.SubLegacyMinimumSingleMetricWidthCanBePlaced`
303. `SysMonitor.Tests.TaskbarPlacementStabilizerTests.ExplicitLayoutBypassesPositionDeadZoneButRemainsConstrained`

### 38. ThemeCatalogTests（13 项）

主题目录导入、并发与安全。

304. `SysMonitor.Tests.ThemeCatalogTests.BuiltInsAreImmutableDefaultsAndMatchModernPalette`
305. `SysMonitor.Tests.ThemeCatalogTests.DuplicateIdIsRejectedWithoutReplacingInstalledTheme`
306. `SysMonitor.Tests.ThemeCatalogTests.ConcurrentDifferentImportsBothCommitAndRemainInCatalog`
307. `SysMonitor.Tests.ThemeCatalogTests.InitializeCleansSafeStagingAndSkipsCorruptTheme`
308. `SysMonitor.Tests.ThemeCatalogTests.ReinitializedCatalogValidatesInstalledFilesAndPreservesIdentity`
309. `SysMonitor.Tests.ThemeCatalogTests.CatalogRejectsUnexpectedInstalledFileAndFallsBack`
310. `SysMonitor.Tests.ThemeCatalogTests.ReparseThemeDirectoryIsSkippedWhenPlatformAllowsCreation`
311. `SysMonitor.Tests.ThemeCatalogTests.CancellationReturnsStructuredErrorForImport`
312. `SysMonitor.Tests.ThemeCatalogTests.SystemThemeIdResolvesToValidBuiltInTheme(systemId: "system")`
313. `SysMonitor.Tests.ThemeCatalogTests.SystemThemeIdResolvesToValidBuiltInTheme(systemId: "SYSTEM")`
314. `SysMonitor.Tests.ThemeCatalogTests.SystemThemeIdResolvesToValidBuiltInTheme(systemId: "auto")`
315. `SysMonitor.Tests.ThemeCatalogTests.SystemThemeIdResolvesToValidBuiltInTheme(systemId: "AUTO")`
316. `SysMonitor.Tests.ThemeCatalogTests.CatalogSnapshotIncludesSystemThemeAsFirstOption`

### 39. ThemePackageTests（32 项）

主题包路径、大小、图片、JSON 与版本安全。

317. `SysMonitor.Tests.ThemePackageTests.ValidPackageInstallsAndReturnsOnlyValidatedPaths`
318. `SysMonitor.Tests.ThemePackageTests.UnsafeOrNonWhitelistedEntryIsRejected(entryName: "../escape.txt")`
319. `SysMonitor.Tests.ThemePackageTests.UnsafeOrNonWhitelistedEntryIsRejected(entryName: "/absolute.txt")`
320. `SysMonitor.Tests.ThemePackageTests.UnsafeOrNonWhitelistedEntryIsRejected(entryName: "C:/absolute.txt")`
321. `SysMonitor.Tests.ThemePackageTests.UnsafeOrNonWhitelistedEntryIsRejected(entryName: "manifest.json:payload")`
322. `SysMonitor.Tests.ThemePackageTests.UnsafeOrNonWhitelistedEntryIsRejected(entryName: "assets//preview.png")`
323. `SysMonitor.Tests.ThemePackageTests.UnsafeOrNonWhitelistedEntryIsRejected(entryName: "assets/CON.png")`
324. `SysMonitor.Tests.ThemePackageTests.UnsafeOrNonWhitelistedEntryIsRejected(entryName: "assets/COM1.png")`
325. `SysMonitor.Tests.ThemePackageTests.UnsafeOrNonWhitelistedEntryIsRejected(entryName: "README.md.")`
326. `SysMonitor.Tests.ThemePackageTests.UnsafeOrNonWhitelistedEntryIsRejected(entryName: "payload.exe")`
327. `SysMonitor.Tests.ThemePackageTests.WindowsNormalizedDuplicateEntryIsRejected(duplicateName: "MANIFEST.JSON")`
328. `SysMonitor.Tests.ThemePackageTests.WindowsNormalizedDuplicateEntryIsRejected(duplicateName: ".\\manifest.json")`
329. `SysMonitor.Tests.ThemePackageTests.ForwardAndBackslashDuplicateAssetIsRejected`
330. `SysMonitor.Tests.ThemePackageTests.InvalidOrReservedIdIsRejected(id: "CON")`
331. `SysMonitor.Tests.ThemePackageTests.InvalidOrReservedIdIsRejected(id: "theme.json")`
332. `SysMonitor.Tests.ThemePackageTests.InvalidOrReservedIdIsRejected(id: "builtin.default")`
333. `SysMonitor.Tests.ThemePackageTests.InvalidOrReservedIdIsRejected(id: "Uppercase")`
334. `SysMonitor.Tests.ThemePackageTests.ExcessiveCompressionRatioIsRejected`
335. `SysMonitor.Tests.ThemePackageTests.OversizedEntryIsRejected`
336. `SysMonitor.Tests.ThemePackageTests.InvalidPngSignatureIsRejected`
337. `SysMonitor.Tests.ThemePackageTests.DecodedPngDimensionsAreEnforced`
338. `SysMonitor.Tests.ThemePackageTests.InvalidIcoDecoderPayloadIsRejected`
339. `SysMonitor.Tests.ThemePackageTests.EmbeddedIcoDimensionsAreEnforcedBeforeDecode`
340. `SysMonitor.Tests.ThemePackageTests.MoreThanMaximumArchiveEntriesIsRejected`
341. `SysMonitor.Tests.ThemePackageTests.InvalidJsonUnknownFieldAndSchemaAreRejected`
342. `SysMonitor.Tests.ThemePackageTests.InvalidColorIsRejected(color: "#12345")`
343. `SysMonitor.Tests.ThemePackageTests.InvalidColorIsRejected(color: "red")`
344. `SysMonitor.Tests.ThemePackageTests.InvalidColorIsRejected(color: "#GG0000")`
345. `SysMonitor.Tests.ThemePackageTests.InvalidNumericRangeIsRejected(radius: -1, opacity: 0.5)`
346. `SysMonitor.Tests.ThemePackageTests.InvalidNumericRangeIsRejected(radius: 33, opacity: 0.5)`
347. `SysMonitor.Tests.ThemePackageTests.InvalidNumericRangeIsRejected(radius: 10, opacity: 1.1000000000000001)`
348. `SysMonitor.Tests.ThemePackageTests.MinimumApplicationVersionIsEnforced`

### 40. ThemeResourceApplierTests（1 项）

主题资源应用与恢复。

349. `SysMonitor.Tests.ThemeResourceApplierTests.BuiltInThemesApplyIdempotentlyAndRestoreDefaultResources`

### 41. TrayIconServiceTests（12 项）

托盘菜单、DPI、子菜单与 HUD 状态。

350. `SysMonitor.Tests.TrayIconServiceTests.GameOverlayText_ReflectsVisibilityAndCompatibilityAvailability(visible: False, available: True, expected: "TrayShowGameOverlay")`
351. `SysMonitor.Tests.TrayIconServiceTests.GameOverlayText_ReflectsVisibilityAndCompatibilityAvailability(visible: True, available: True, expected: "TrayHideGameOverlay")`
352. `SysMonitor.Tests.TrayIconServiceTests.GameOverlayText_ReflectsVisibilityAndCompatibilityAvailability(visible: False, available: False, expected: "TrayGameOverlayUnavailableCompatibility")`
353. `SysMonitor.Tests.TrayIconServiceTests.GameOverlayText_ReflectsVisibilityAndCompatibilityAvailability(visible: True, available: False, expected: "TrayGameOverlayUnavailableCompatibility")`
354. `SysMonitor.Tests.TrayIconServiceTests.MenuLayout_FitsLocalizedTextChecksAndShortcutAtCommonDpiScales(scale: 1)`
355. `SysMonitor.Tests.TrayIconServiceTests.MenuLayout_FitsLocalizedTextChecksAndShortcutAtCommonDpiScales(scale: 1.25)`
356. `SysMonitor.Tests.TrayIconServiceTests.MenuLayout_FitsLocalizedTextChecksAndShortcutAtCommonDpiScales(scale: 1.5)`
357. `SysMonitor.Tests.TrayIconServiceTests.MenuLayout_FitsLocalizedTextChecksAndShortcutAtCommonDpiScales(scale: 1.75)`
358. `SysMonitor.Tests.TrayIconServiceTests.MenuLayout_FitsLocalizedTextChecksAndShortcutAtCommonDpiScales(scale: 2)`
359. `SysMonitor.Tests.TrayIconServiceTests.MenuLayout_CapsHeightSoOverflowRemainsInsideWorkingArea`
360. `SysMonitor.Tests.TrayIconServiceTests.Submenus_ConfiguredWithMacRendererAndPadding`
361. `SysMonitor.Tests.TrayIconServiceTests.Submenu_Location_SnapsFlushToParent`

### 42. UiRefreshSchedulerTests（7 项）

刷新间隔、合并、调整、失效与释放。

362. `SysMonitor.Tests.UiRefreshSchedulerTests.StrictModeHonorsMinimumIntervalAndUsesLatestCallback`
363. `SysMonitor.Tests.UiRefreshSchedulerTests.StrictModeSetIntervalReschedulesPendingWorkEarlierAndLater`
364. `SysMonitor.Tests.UiRefreshSchedulerTests.InvalidatePendingRejectsQueuedWorkWithoutConsumingNewRequest`
365. `SysMonitor.Tests.UiRefreshSchedulerTests.RestartIntervalPreservesPendingWorkAndMovesItsBoundary`
366. `SysMonitor.Tests.UiRefreshSchedulerTests.CoalescesBurstsAndDeliversTrailingRequest`
367. `SysMonitor.Tests.UiRefreshSchedulerTests.DisposeDropsQueuedCallback`
368. `SysMonitor.Tests.UiRefreshSchedulerTests.DisposeWaitsForInFlightCallbackAndPreventsLaterCallbacks`

