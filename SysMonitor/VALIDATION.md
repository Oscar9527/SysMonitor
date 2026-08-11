# SysMonitor 1.2 validation

Validation date: 2026-08-11
Environment: Windows 11 x64, 16 logical processors, NVIDIA RTX 3060 Laptop GPU, auto-hidden taskbar

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
