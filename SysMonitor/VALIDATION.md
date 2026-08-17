# SysMonitor 1.5.0 validation

## v1.5.0 beta.9 menu, opacity and window-follow pass

- Tray root and nested menus use localized preferred sizing, a dedicated shortcut column, DPI-scaled item height, and monitor-work-area width/height caps with overflow scrolling.
- HUD background opacity is persisted independently from text colors. Legacy settings default to the former 80% black surface; invalid values are finite-clamped to 0–100%.
- Game-window following uses out-of-context WinEvent hooks only. Native callbacks enqueue coalesced UI work; a roughly 16 ms render-priority cadence is active only during move/size and the original 750 ms recovery poll remains.
- Target identity is validated by HWND, PID, and process start generation. Minimize is temporary; destroy or identity mismatch permanently invalidates the binding. Hook setup failure safely falls back without injection or cross-process reparenting.
- Full automated result: 329 passed, 0 failed, 0 skipped. Portable single-file size: 8,986,112 bytes. SHA-256: `218BA20E07421CDFC234C26C8CA37EC1258F4A3F7AD727351F5C4D0F0D29653B`.

Validation date: 2026-08-17
Environment: Windows 11 x64; running game and foreground input left untouched

## Exact per-monitor HUD positioning and horizontal mode

- The game HUD can now use signed physical virtual-screen X/Y coordinates,
  including negative coordinates for displays placed left of or above the
  primary monitor. Coordinates are remembered independently for each monitor.
- Stable DisplayConfig monitor device paths are preferred for persistence. A
  conservative GDI-name plus exact-bounds identity is used only as a fallback;
  missing, renamed, or duplicate identities fail closed instead of applying a
  position to the wrong screen.
- Exact placement uses the HUD's PerMonitorV2 DPI and full monitor bounds, then
  clamps the complete HUD onscreen. The unchanged percentage fallback continues
  to use the game client or monitor work area.
- The settings window exposes vertical and horizontal modes in English and
  Simplified Chinese. Horizontal mode contains only CPU usage/temperature, GPU
  usage/temperature, and a valid FPS sample; unavailable FPS remains hidden.
- X/Y sliders use the current HUD size to expose only fully visible positions;
  synchronized numeric boxes remain available for one-pixel adjustment.
- Slider, numeric, exact/default-position, and vertical/horizontal edits now
  preview directly on the HUD without calling the settings save path. Cancel and
  title-bar close restore an immutable snapshot of the actual pre-open runtime
  layout and complete per-monitor position map; successful Save adopts it.
- The novice view contains only display style and position. Metric order,
  sampling, and per-game RTSS compatibility are grouped in one collapsed
  Advanced section.
- Unrelated Apply actions do not create or reset exact coordinates. Position
  mutations revalidate the selected monitor before and after any slower RTSS
  compatibility operation.
- A failed settings-file write restores the pre-apply fields and runtime preview,
  keeps the dialog open, and reports the failure instead of pretending Save
  succeeded.
- Full result: 292/292 tests passed. Release packaging completed with 0 warnings
  and 0 errors without launching the application or taking foreground input.
- Final framework-dependent single portable launcher: 8,870,912 bytes (8.46 MiB),
  SHA-256 `2AE0058EC2CB895B8571262D92AA2ACAB7D578425E61D5B23C2D2E31E139CA43`.
  The artifact directory contains exactly one file, `SysMonitor.exe`.

## Opt-in per-game RTSS compatibility

- The default FPS path remains read-only RTSS shared memory followed by the
  bundled PresentMon ETW fallback. If neither source yields a finite active
  sample, the entire FPS row is collapsed.
- A user must first select a concrete game target and then confirm the legacy
  compatibility warning. Startup and foreground tracking never create profiles.
- The manager writes only `EnableHooking=1` and `HookDirectDraw=1` in the
  selected executable's application profile. It preserves unrelated bytes,
  stores an original-byte/hash manifest, and verifies apply and restore hashes.
- `Global` and `Config` are hard rejected. Canonical-path and same-basename
  checks prevent a second directory's identically named executable from taking
  over an existing managed RTSS profile.
- Pending operations reconcile only when the current state exactly matches the
  original or intended hash; all other states remain conflicts with no profile
  write. Managed targets remain restorable after the executable is deleted.
- RTSS is not started or restarted, no RTSS DLL is loaded by SysMonitor, and no
  SysMonitor code is injected into the game. Applying or restoring requires the
  affected game to be fully restarted.
- Full result: 269/269 tests passed. Release packaging completed with 0 warnings
  and 0 errors without launching the application or changing the live RTSS state.
- Final framework-dependent single portable launcher: 8,743,936 bytes (8.34 MiB),
  SHA-256 `1BE0211FE721D2D885A5C5D99763D5C85EB5B8274A5CDA9918F7FED72F65E20D`.
  The artifact directory contains exactly one file, `SysMonitor.exe`.

## Taskbar band disappearance and overlay availability hotfix

- Runtime diagnostics reproduced the failure deterministically: immediately
  after the HUD called cross-process `SetParent` on a legacy DPI-unaware game,
  the already-created taskbar Band changed from per-monitor-v2 awareness to
  DPI-unaware. The Band integrity guard then safety-parked its retained HWND at
  an off-screen 1x1 rectangle, so the process stayed alive while the taskbar
  monitor appeared to vanish.
- The game HUD no longer changes parent or child-window styles. It remains a
  top-level `WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW` window,
  follows the target client rectangle in screen coordinates, and synchronizes
  its topmost tier with the target without activating either window.
- `GameSafeMode` now selects hardware sensor providers only. The frame provider,
  HUD window, controller, global hotkey, and tray commands are created in both
  sensor modes. The obsolete controller compatibility gate was removed.
- Z-order tests cover normal and topmost targets, an existing predecessor, an
  already-correct overlay position, and no-target demotion. Controller tests
  prove that a tray request starts the HUD without a sensor-mode gate.
- When neither source has a valid value, the HUD now hides the FPS row entirely and
  does not add a distracting “no frames captured” status message.
- Full result: 251/251 tests passed. Release packaging completed with 0 warnings
  and 0 errors. A separate read-only structural review found no blocking issue.
- Final framework-dependent single portable launcher: 8,604,672 bytes,
  SHA-256 `D33B3F7F8B3BE2FB155860E509363401E135B5D0A98633953858E5F899CF49BA`.
  The artifact directory contains exactly one file, `SysMonitor.exe`.
- The repaired binary was not launched because the foreground game session was
  intentionally left untouched. The currently running damaged process must be
  exited once before this build can recreate a healthy per-monitor-aware Band.

## Legacy DirectDraw / Direct3D 7 windowed-game FPS repair

- The affected 32-bit target loaded `DDRAW.dll`, `D3DIM700.dll`, and
  `RTSSHooks.dll`. The bundled PresentMon collector was running with the exact
  target PID but received no presentation rows, which the HUD correctly rendered
  as unavailable rather than a fabricated value.
- Production wiring now uses `AdaptiveFrameRateProvider`: read-only RTSS shared
  memory first, then SysMonitor-owned PresentMon after one second without a fresh
  RTSS sample. A factory test prevents the app from silently reverting to the
  previously hard-wired PresentMon-only path.
- New tests prove that a fresh RTSS sample never starts PresentMon and that
  stopping before the fallback delay cannot launch a late collector. Existing
  tests retain fallback, two-sample RTSS recovery, and idempotent lifecycle
  coverage.
- A PID-specific read-only probe found the game's RTSS entry. Because the game
  was no longer foreground while diagnostics ran, that entry was stale; the
  reader returned no FPS, as required. No end-to-end active-game value is claimed
  from that backgrounded probe.
- Full result: 245/245 tests passed. Release build completed with 0 warnings and
  0 errors.
- Final framework-dependent single portable launcher: 8,604,672 bytes,
  SHA-256 `6088470112E589AE1D7267524C2E3FB8B00D230DA6DFC29FD7BA140907F0B76A`.
  The artifact directory contains exactly one file, `SysMonitor.exe`.
- The default collection path did not start, configure, write to, or inject RTSS.
  The separate opt-in control can write only the confirmed per-game profile. RTSS itself may
  use graphics-API hooks; its game and anti-cheat compatibility is outside
  SysMonitor's control. Without RTSS, legacy DirectDraw/D3D7 games may remain
  unavailable through PresentMon; without a valid sample the FPS row stays hidden.

Validation date: 2026-08-11
Environment: Windows 11 x64; interactive user session left untouched

## v1.5.0 game-safe overlay development build

### Read-only shared-memory repair

- RTSS `RTSSSharedMemoryV2` is opened with read rights and a read-only view only; the reader reopens on every sample and contains no create/write path.
- Deterministic byte fixtures cover RTSS signatures, v2 layouts, checked capacities, stale samples, and rolling/frame-time/frame-counter formulas.
- The adaptive frame provider polls only while started, delays the SysMonitor-owned PresentMon fallback by about one second, and switches back after two valid RTSS recovery samples.
- Game-safe monitor options keep the GPU LibreHardwareMonitor provider disabled while enabling the independent CPU-only temperature reader.

- Release build completed with 0 warnings and 0 errors; the full suite passed 219/219 tests after removing the MSI Afterburner/MAHM integration tests.
- A read-only production-reader probe matched the active Delta Force process (PID 28276) and returned 88.9 FPS from RTSS. The independent CPU reader remains nullable and does not substitute a fixed environmental value.
- The final portable launcher is one `SysMonitor.exe`, version 1.5.0, 8,391,680 bytes (8.00 MiB), SHA-256 `FBAFF32F0DB5CA7D6C4181C933E6D191B67921C3EE7701B7DB14B251EFCC25CB`.
- The launcher contains the versioned `SysMonitor.Core.1.5.0.exe` resource. The core embeds the pinned official PresentMon 2.5.1 x64 binary, its MIT license, and the project third-party notice.
- The embedded PresentMon binary is verified by test and at extraction time against SHA-256 `9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191`.
- Exact collector arguments, owned-session termination arguments, bounded output, comma-bearing application names, invalid/overlong rows, per-swapchain aggregation, hysteresis and stale clearing have deterministic coverage.
- Game-safe settings migrate to enabled before `MonitorService` construction. In that mode the service constructs only the CPU temperature reader plus NVIDIA's `nvidia-smi` path; the full LibreHardwareMonitor GPU provider remains disabled.
- Static audit found no DLL injection, graphics API hook, game-process memory read/write, low-level input hook or frame-rate kernel driver path in SysMonitor.
- Overlay tests cover no-activate/tool/transparent styles, `HTTRANSPARENT`, hotkey registration/disposal, rapid cancellation, target exclusion/PID identity, negative monitor coordinates and DPI placement without sending physical input.
- CPU frequency is nullable and comes from Windows `CallNtPowerInformation`; GPU clocks are nullable and come from `nvidia-smi` or compatibility sensors. No nominal clock or zero-value substitute is shown when unavailable.
- A hidden portable-launcher smoke run exited successfully and replaced/restarted only the versioned SysMonitor runtime core as designed; it did not activate a foreground window. The restarted core keeps GPU compatibility disabled while enabling the independent CPU-only reader (`cpuTemperature=True`).
- Physical game/ACE compatibility, exclusive-fullscreen z-order and multi-GPU sensor coverage are not claimed by this deterministic pass. ETW is non-injection system observation, not an anti-cheat certification.

Validation date: 2026-08-12
Environment: Windows 11 x64, 16 logical processors, NVIDIA RTX 3060 Laptop GPU, auto-hidden taskbar

## v1.4.1 Band click reliability release

- Root cause was confirmed with native per-pixel hit testing: the transparent layered Band routed only 1,878 of 13,226 client pixels (14.2%) to its own HWND; transparent margins and glyph interiors were routed to Explorer.
- A theme-independent ARGB alpha-1 hit surface now covers the complete Band rectangle while the visible theme layer remains transparent or image-backed. The same native `WindowFromPoint` scan after the fix routed all 13,226 pixels to the Band HWND (100%, 0 misses).
- The toggle entry point is now `WM_LBUTTONDOWN`; `WM_LBUTTONUP` never toggles. A monotonic 350 ms down-event debouncer collapses native double-click sequences without extending the suppression window.
- Deterministic tests cover the first click, exact debounce boundary, repeated timestamps, clock rollback, down-only message policy, theme-independent hit surface, tray activation policy and normal/pinned z-order requests.
- A live down/up sequence showed and hid the detail panel correctly. The panel was above the existing foreground window while `GetForegroundWindow` remained unchanged, and the final state was hidden. A burst pair toggled exactly once.
- Non-pinned Band shows use a non-activating topmost/not-topmost z-order sequence, leaving the final window non-topmost. Pinned shows retain true topmost state. Tray shows keep the existing activation behavior.
- Release build completed with 0 warnings and 0 errors; the full suite passed 156/156 tests.
- A 30.9-second settled sample observed one Band HWND, one parent, one X position, one width and one Y position. Every sample remained no-activate and the Band never became foreground.
- During that sample, normalized CPU use was below the measurable 0.0001% threshold. Handles changed from 874 to 865, GDI objects stayed 39 to 39, USER objects changed from 44 to 43, and exactly one v1.4.1 core process remained.
- The final portable executable SHA-256 and immutable source commit are recorded in the annotated `v1.4.1` tag after the final rebuild succeeds.

## v1.4.0 history chart release

- Release build completed with 0 warnings and 0 errors; the full suite passed 147/147 tests.
- The detail panel keeps an exact 60-second, 120-sample hard-capped CPU/GPU history on the UI dispatcher. GPU-unavailable samples remain nullable and create visible gaps rather than false 0% values.
- The hidden or minimized detail panel does not allocate immutable chart snapshots or invalidate chart rendering. Before every show, the latest snapshot and complete retained history are injected before the first visible frame.
- The custom WPF chart uses the active theme, fixed 0%–100% scaling, DPI-aware drawing and a UI Automation peer. Geometry, epoch resets, time-window expiry, null gaps and STA rendering are covered by 20 focused tests.
- A 30-second live Band sample collected 887 observations of one HWND with one rectangle and one parent. Every observation remained a no-activate taskbar child and it never became foreground.
- With the detail charts visible for 182.3 seconds, normalized CPU use was 0.0070% across 16 logical processors. GDI objects stayed 54 to 54, USER objects 67 to 66, and handles 1043 to 1028 (maximum 1047).
- During that visible-chart run, working set moved from 190.5 MiB to 194.7 MiB (maximum 195.0 MiB); private memory moved from 161.7 MiB to 166.0 MiB.
- A launcher-over-existing-runtime test exited the old core and started exactly one version 1.4.0 core. After settling, a further 20-second sample observed one stable Band HWND, one rectangle and one parent, with no foreground activation.
- The final portable executable SHA-256 and immutable source commit are recorded in the annotated `v1.4.0` tag after the final rebuild succeeds.

## v1.2.15 retained regression baseline

## Build

- Release build: passed, 0 warnings, 0 errors
- Publish: win-x64, framework-dependent, single EXE
- Version: 1.2.15
- Runtime: Microsoft .NET 8 Desktop Runtime x64
- LibreHardwareMonitorLib: 0.9.4 (selected after a 0.9.5/0.9.6 Ryzen temperature regression comparison)

## Safe-range positioning

- Positioning now uses taskbar-client relative X/Y coordinates. A stale visible-state `TaskbarTop` is never converted through a moving parent, so native parent motion cannot be cancelled by a compensating child offset.
- Safe constraints contract immediately and expand only after two consecutive matching observations. The applied Band X remains unchanged while its full rectangle is still safe.
- A 12-second read-only live sample collected 186 Band observations: one HWND, one parent, 0 px X/Y/width variance, 100% visible, child, tool-window and no-activate states, with every parent equal to the current `Shell_TrayWnd`.
- Stable-state diagnostics recorded one initial placement and no periodic placement calls afterward.
- Horizontal control spans 0%–100% of the currently available taskbar gap.
- The full Band rectangle is clamped after the last task/app icon and before the first notification-area icon.
- UI Automation probing runs on a dedicated STA worker; Win32 fallback accepts only explicit task-list and notification classes.
- No guessed placement is used when trustworthy bounds are unavailable. The persistent HWND safety-hides and keeps monitoring until recovery.
- Taskbar create/destroy/show/hide/reorder/location events are debounced at 150 ms; the 2-second recovery probe runs only while no trusted layout is available.
- Auto-hidden taskbar transitions keep the same child HWND and do not clear the last trusted visible-state bounds.
- Healthy-state polling validates the native child contract without repositioning the Band; normal moves preserve child z-order and `WS_CLIPSIBLINGS` isolates Windows 10 taskbar siblings.
- Metric values use clipped fixed Grid slots with tabular numerals, so changing digit counts and rate units do not move adjacent columns.
- Taskbar UI Automation uses actual Button/ListItem/TabItem edges and ignores broad empty task-list containers, maximizing safe travel without covering icons.
- Horizontal top/bottom taskbars are supported. A vertical taskbar has no legal horizontal Band layout and therefore uses the existing safety park; tray and detail functions remain available.

## Localization and interface

- Runtime culture choices are `system`, `en-US` and `zh-CN`; any `zh-*` system UI culture resolves to simplified Chinese and all other system cultures resolve to English.
- Switching language updates the open detail window, appearance window and tray menu without recreating the Band HWND.
- English and Chinese resources contain identical key sets and matching format placeholders. Chinese text is embedded in the final single-file core.
- Legacy settings without `UiCulture` load as `system`; invalid values normalize safely without losing existing appearance settings.
- The detail and appearance windows use shared modern-light design tokens, opaque WPF rendering, rounded grouped surfaces and `Segoe UI Variable Text` with `Segoe UI` fallback for Windows 10.

## Appearance range

- Item spacing is persisted and live-previewed from 0 to 18 DIP.
- Band width follows item spacing instead of reserving a fixed rectangle.
- On the 45-DIP test taskbar, default spacing produced a 473 px Band and 565 px of safe travel; the previous fixed 520 px Band allowed 518 px, so the default gained 47 px of travel.
- At zero spacing the same layout uses 413 px and provides 625 px of safe travel, a 107 px gain over the previous fixed width.
- The settings window was rendered at 420 x 450: both sliders, value badges, explanatory text, preview card, and footer buttons were visible without clipping.

## Prior 1.2.13 regression baseline

- The following taskbar/launcher baseline was established in 1.2.13 and retained unchanged by the GPU-only integration:
- Launching 1.2.13 while the extracted 1.2.12 core was still running terminated only the old SysMonitor runtime core, then started 1.2.13 successfully.
- The startup registration migrated from the 1.2.12 portable EXE to the 1.2.13 portable EXE.
- A deliberately stale 1.2.12 payload renamed to the cached 1.2.13 core path was detected by embedded SHA-256 comparison and replaced before launch.
- The appearance window title includes the executing core version so a remote machine can be checked without inspecting files or logs.

## Click-disappearance regression

- Static audit found no Band recovery path using `Hide`, `Close`, `SW_HIDE`, `SWP_HIDEWINDOW`, or live-window replacement.
- Real system mouse input was sent to the actual taskbar child window after revealing the auto-hidden taskbar.
- Before Explorer restart, 1050 samples at 20 ms intervals observed one stable Band HWND: 0 invisible samples, 0 wrong-parent samples, 0 non-child samples, and 0 topmost samples.
- Cross-window clicks alternated the detail panel while pre/post invariants reported the same live Band HWND.
- Explorer was restarted. Windows destroyed the old child HWND; the process survived and created exactly one new Band after proven `WM_NCDESTROY`.
- After recovery, 645 samples and a further 753 auto-hide samples observed the new HWND continuously visible and attached to the new taskbar.
- No recovery-hide message, unexpected close, or second destruction occurred in the new session.
- Launching a second EXE instance exited the duplicate and preserved the original PID and Band HWND.

## Data checks

- CPU source selected: PDH `Processor Utility`, matching Task Manager semantics.
- Independent `typeperf` sampling confirmed the Utility counter is available and materially different from the old busy-time counter on this frequency-scaled CPU.
- CPU temperature remained available on the AMD Ryzen 7 5800H (`Core (Tctl/Tdie)`, 62–65 °C during the 1.2.14 run).
- LibreHardwareMonitorLib 0.9.5 and 0.9.6 returned an invalid 0 °C for that same sensor; 0.9.4 returned 66.6 °C and was retained to prevent regression.
- The GPU coordinator first produced a valid LibreHardwareMonitor sample, then selected the NVIDIA primary source when its first timestamp-delimited cycle arrived.
- Selected NVIDIA device: RTX 3060 Laptop GPU. Core temperature was 45–46 °C and total VRAM was 6144 MiB, matching an independent `nvidia-smi` query.
- NVIDIA, AMD and Intel selector/coordinator behavior is covered by 22 deterministic tests. AMD and Intel physical GPU validation was not available on this machine and is not claimed.
- Missing usage, core temperature or VRAM remains nullable and renders as unavailable instead of a false zero.

## Automated checks

- Full result: 58 passed, 0 failed, 0 skipped; Release build completed with 0 warnings and 0 errors.
- Added parent-relative geometry, vertical-taskbar safety, constraint contraction/expansion, boundary jitter, width growth and no-feasible-region coverage.
- Added culture resolution, bilingual resource parity, dynamic detail copy, legacy settings migration and corrupt/invalid settings coverage.

### GPU checks retained

- Exact NVIDIA/AMD `GPU Core` load selection; controller, video and bus loads are excluded.
- Intel uses the maximum finite D3D engine load.
- Only exact `GPU Core` temperature is accepted; hotspot, memory junction and VRM sensors are excluded.
- MiB-to-byte conversion, Total-Free derivation, D3D dedicated-used fallback, shared-memory exclusion, zero/invalid/overflow values.
- Quoted CSV fields, `N/A` metrics, timestamp cycle boundaries, partial/corrupt rows and adapter-set changes.
- Fresh NVIDIA-primary suppression of duplicate LibreHardwareMonitor NVIDIA devices, exact-identity-only merge, stale fallback, out-of-order rejection and two-tick selection hysteresis.
- Repeated start/stop/dispose is idempotent and awaited.
- The original 22 GPU checks remain part of the 58-test full suite.

## Portable upgrade and process cleanup

- Starting the 1.2.15 launcher over a running extracted 1.2.14 core stopped the old core and started exactly one 1.2.15 core; no 1.2.14 core remained.
- Extracted `SysMonitor.Core.1.2.15.exe` ran from the versioned runtime cache and startup registration migrated to the 1.2.15 portable launcher.
- The application manifest remains `asInvoker`; the launcher only detects the .NET 8 Desktop Runtime and opens the official page when missing. This host session runs at high integrity, so a clean standard-user machine validation was unavailable and is not claimed.

## Resource target

- Sampling interval: 1 second.
- The final packaged core ran for a 182.1-second stable-state sample at 0.0257% of one core, or 0.0016% normalized across 16 logical processors, below the 1% target.
- Average/max working set was 160.00/161.42 MiB; maximum private memory was 140.01 MiB and maximum handle count was 867.
- Final portable launcher: 6,798,336 bytes (6.48 MiB), inside the requested 5–50 MB range.
- Final SHA-256: `F4C3A8E621DC7735023C60F2E528F582261448131A4DC14D3D736BF55AE04ED0`.

## Hardware coverage boundary for this release

- This pass did not move the user's mouse or toggle their taskbar settings. The live check therefore proves stable attachment and zero stationary jitter on the available Windows 11 host, while the parent-motion path is covered deterministically by relative-coordinate tests.
- A separate physical Windows 10 auto-hide transition and secondary-monitor shell animation were not available in this environment and are not claimed as hardware-tested in this release.
