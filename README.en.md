<div align="center">

<img src="SysMonitor/Assets/sysmonitor-icon.png" alt="SysMonitor Logo" width="96" height="96" />

# SysMonitor

**Windows Taskbar System Monitor & Game FPS Overlay**

<p><a href="README.md">简体中文</a> · <b>English</b></p>

[![Release](https://img.shields.io/github/v/release/Oscar9527/SysMonitor?display_name=tag&sort=semver&style=flat-square)](https://github.com/Oscar9527/SysMonitor/releases/latest)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-0078D4?style=flat-square)](https://github.com/Oscar9527/SysMonitor)
[![Runtime](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/Oscar9527/SysMonitor?style=flat-square)](LICENSE)

<p>
  <a href="#download">Download</a> ·
  <a href="#features">Features</a> ·
  <a href="#screenshots">Screenshots</a> ·
  <a href="#whats-new-in-v1010">What's New</a> ·
  <a href="#build-from-source">Build from Source</a>
</p>

</div>

**SysMonitor** is a portable system monitor for Windows 10 and 11. It embeds CPU, GPU, memory, network, and disk metrics directly into the Windows taskbar, with a click-to-open details panel and a hotkey-toggled in-game FPS overlay (`Ctrl+Shift+F10`).

---

## Download

Get the latest build from [GitHub Releases](https://github.com/Oscar9527/SysMonitor/releases/latest) ([`SHA256SUMS-v1.0.10.txt`](https://github.com/Oscar9527/SysMonitor/releases/download/v1.0.10/SHA256SUMS-v1.0.10.txt)):

| Edition | Download | Requirements & Notes |
| :--- | :--- | :--- |
| **Standalone (Recommended)** | [`SysMonitor-v1.0.10-Standalone.exe`](https://github.com/Oscar9527/SysMonitor/releases/download/v1.0.10/SysMonitor-v1.0.10-Standalone.exe) | Self-contained single EXE with the .NET 8 runtime bundled |
| **Light** | [`SysMonitor-v1.0.10-Light.exe`](https://github.com/Oscar9527/SysMonitor/releases/download/v1.0.10/SysMonitor-v1.0.10-Light.exe) | Smaller framework-dependent EXE; requires x64 .NET 8 Desktop Runtime |

> [!TIP]
> To upgrade, exit the previous version and run the new EXE. Your settings are stored in `%APPDATA%\SysMonitor\settings.json`.

---

## Features

- **Taskbar Monitor**: Displays CPU and GPU usage and temperatures, RAM usage, upload/download rates, and system drive usage. Follows the taskbar layout, handles display scaling and auto-hide, and stays out of `Alt+Tab`.
- **Details Panel**: Shows the last 60 seconds of CPU and GPU usage alongside per-drive storage capacity and usage bars.
- **Game Overlay (HUD)**: Displays FPS and hardware metrics in horizontal or vertical layouts (`Ctrl+Shift+F10`), follows the selected game window, and remembers positions per display.
- **Appearance Settings**: Customize font, size, spacing, and taskbar band position, toggle individual metrics, and switch between Day and Night themes.
- **Tray Menu**: Quick access to appearance and overlay settings, startup options, safe mode, and keeping the details panel on top.

> [!NOTE]
> **FPS and Hardware Readings**
> - The overlay uses a usable RTSS sample when available and falls back to the bundled PresentMon 2.5.1 collector reading Windows ETW events. Older DirectDraw games and unusual rendering paths may not report FPS; GPU metrics depend on hardware, driver, and sensor availability.
> - The main app runs without elevation. Some hardware sensors or PresentMon capture sessions may request administrator access through a helper process; affected readings remain unavailable if declined.

---

## Screenshots

<table>
  <tr>
    <td align="center" valign="top" width="58%">
      <img src="docs/images/appearance-settings.png" alt="SysMonitor taskbar appearance settings" width="350" />
      <br />
      <sub><b>Taskbar Appearance Settings</b><br />Customize fonts, sizing, spacing, visible metrics, and themes</sub>
    </td>
    <td align="center" valign="top" width="42%">
      <img src="docs/images/tray-menu.png" alt="SysMonitor system tray menu" width="220" />
      <br />
      <sub><b>System Tray Menu</b><br />Toggle HUD, settings windows, startup options, and pin state</sub>
    </td>
  </tr>
</table>

---

## What's New in v1.0.10

- Added Day and Night themes and redesigned the details panel, tray menu, and settings windows.
- Fixed the stray strip that could remain on the desktop after minimizing the details panel.
- Fixed clipped taskbar monitor text at 125% scaling.

See [GitHub Releases](https://github.com/Oscar9527/SysMonitor/releases) for full release notes and earlier versions.

---

## Build from Source

On Windows x64, install the **.NET 8 SDK** (or newer) and run from the repository root:

```powershell
.\Launcher\Build-Release.ps1
```

The script writes the Light and Standalone EXEs to `artifacts\`. See [the launcher documentation](Launcher/README.md) for build details.

---

## License

SysMonitor is released under the [MIT License](LICENSE). See the [third-party notices](SysMonitor/THIRD-PARTY-NOTICES.md) for bundled dependencies.
