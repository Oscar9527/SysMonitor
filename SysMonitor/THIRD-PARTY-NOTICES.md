# Third-party notices

SysMonitor bundles LibreHardwareMonitorLib 0.9.4 under the Mozilla Public License 2.0. It is packaged into the portable executable and is used only when the user opts into compatibility hardware sensors. Game-safe sessions do not construct or start this provider.

Project: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
License: https://www.mozilla.org/MPL/2.0/

NVIDIA GPU telemetry is read from `nvidia-smi`, which is installed and licensed with the user's NVIDIA graphics driver and is not distributed inside SysMonitor.

SysMonitor bundles the 64-bit PresentMon console application version 2.5.1 under the MIT License. PresentMon is used as a non-injecting child process to read Windows ETW presentation events for a specifically selected process. The bundled executable is verified against SHA-256 `9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191` before use.

Project: https://github.com/GameTechDev/PresentMon
Version: 2.5.1
License: the complete `ThirdParty/PresentMon-2.5.1/LICENSE.txt` text is embedded in the portable executable and retained in the source tree.

Frame-rate telemetry first reads an already-running RTSS producer through its read-only shared-memory mapping. If that mapping has no usable target sample, SysMonitor falls back to the bundled PresentMon ETW reader. By default SysMonitor does not install, start, configure, inject, or write to RTSS. An optional, explicit per-game legacy DirectDraw compatibility control may back up and change only that executable's RTSS application profile (`EnableHooking=1` and `HookDirectDraw=1`), and can restore the original profile. It never changes RTSS Global or Config profiles. RTSS has its own license and may use graphics-API hooks.
