# SysMonitor

[![Release](https://img.shields.io/github/v/release/Oscar9527/SysMonitor?display_name=tag&sort=semver)](https://github.com/Oscar9527/SysMonitor/releases)
[![License](https://img.shields.io/github/license/Oscar9527/SysMonitor)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)](https://github.com/Oscar9527/SysMonitor)

SysMonitor 是一个轻量、便携的 Windows 任务栏系统性能监视器。它把 CPU、内存、GPU、温度、网络速率和系统盘使用率直接显示在任务栏中，并提供现代化的详情面板。

项目定位是“常驻但不打扰”：Band 不抢焦点、不出现在 Alt+Tab，详情面板按需创建；采集线程按不同指标使用合适的刷新周期，目标是在日常运行时保持很低的 CPU 占用。

## 界面预览

任务栏 Band 会贴合 Windows 任务栏显示，支持底部、顶部和自动隐藏任务栏。下面的截图只保留了监视器区域，数值会随电脑当前状态变化。

![任务栏 Band 预览](docs/images/taskbar-band.png)

点击 Band 或托盘图标即可打开 Apple 风格详情面板：

![详情面板预览](docs/images/detail-panel.png)

## 功能一览

| 模块 | 能力 |
| --- | --- |
| 任务栏 Band | 透明无边框、置顶显示、自动适应任务栏位置和 DPI、支持自动隐藏任务栏 |
| CPU | 总体使用率、逻辑处理器数量、可用时显示 CPU 温度 |
| 内存 | 物理内存使用率、已用/总容量 |
| GPU | NVIDIA 使用率、温度、显存使用量；没有 NVIDIA GPU 时自动隐藏 |
| 网络 | 所有活动 IPv4 网卡合计的下载/上传速率 |
| 磁盘 | 首个固定磁盘的使用率 |
| 托盘 | 显示/隐藏面板、窗口置顶、开机自启、退出 |
| 外观 | 字体、字号、项目间距 `0–18 px`、左右位置和安全区域 |
| 稳定性 | 固定指标槽位、等宽数字、任务栏图标边界保护、Windows 10 重绘抑制 |
| 便携运行 | 单文件启动器自动释放核心程序，不写入安装目录，不需要管理员权限 |

## 下载与发行版

- [下载最新 Release](https://github.com/Oscar9527/SysMonitor/releases)
- [SysMonitor v1.2.13](https://github.com/Oscar9527/SysMonitor/releases/tag/v1.2.13)
- [直接下载 SysMonitor.exe](https://github.com/Oscar9527/SysMonitor/releases/download/v1.2.13/SysMonitor.exe)

当前版本：**1.2.13**

发行包信息：

| 项目 | 值 |
| --- | --- |
| 文件名 | `SysMonitor.exe` |
| 平台 | Windows 10/11 x64 |
| 类型 | 便携式、单文件、无需安装 |
| 大小 | 约 5.7 MB |
| SHA-256 | `46C139370370042D05EF25AF5262E645BE1CBD730C76BBEF2D0BDC9747F0FA7E` |

> 这是 framework-dependent 单文件版本，目标电脑需要安装 Microsoft .NET 7 Desktop Runtime x64。启动器检测到运行时缺失时，会打开官方 .NET 下载页面。

## 快速开始

1. 从 [Release 页面](https://github.com/Oscar9527/SysMonitor/releases) 下载 `SysMonitor.exe`。
2. 双击运行，不需要安装，也不需要管理员权限。
3. 程序启动后，Band 会出现在任务栏附近，托盘区会出现 SysMonitor 图标。
4. 点击 Band 或托盘图标打开详情面板。
5. 在托盘菜单中打开“任务栏外观”，调整字体、字号、间距和左右位置。

关闭详情窗口只会隐藏面板，程序仍会在托盘和任务栏 Band 中运行；需要完全退出时，请使用托盘菜单的“退出”。

## 显示规则

### 任务栏 Band

- 小任务栏（高度 ≤ 30 px）：使用紧凑布局。
- 默认任务栏（31–40 px）：使用标准布局。
- 大任务栏（> 40 px）：使用宽屏布局。
- 任务栏在底部时，Band 显示在任务栏上方；任务栏在顶部时，Band 显示在任务栏下方。
- 支持 100%–200% DPI 缩放。
- 指标槽位固定宽度，数值使用等宽数字，避免数值变化造成左右晃动。
- 自动隐藏任务栏进入隐藏状态时，Band 会跟随任务栏一起隐藏。

### 详情面板颜色

- 使用率低于 75%：CPU 蓝色、内存紫色、GPU 绿色。
- 75%–89%：橙色警告。
- 90% 及以上：红色危险。

## 数据来源与刷新周期

| 数据 | 来源 | 刷新周期 | 备注 |
| --- | --- | --- | --- |
| CPU 使用率 | PDH `Processor Utility` | 1 秒 | 使用 Windows 性能计数器 |
| CPU 温度 | LibreHardwareMonitor 传感器 | 按需/缓存 | 取决于主板和 CPU 传感器支持 |
| 内存 | PSAPI `GetPerformanceInfo` | 1 秒 | 物理内存使用率 |
| GPU | `nvidia-smi` | 30 秒 | NVIDIA GPU 使用率、温度和显存 |
| 网络 | `NetworkInterface.GetIPv4Statistics` | 1 秒 | 所有活动网卡合计 |
| 网卡列表 | `GetAllNetworkInterfaces` | 60 秒 | 重新发现活动网卡 |
| 磁盘 | `DriveInfo.GetDrives()` | 10 秒 | 首个固定磁盘 |

所有指标均在本机采集，程序不会上传性能数据。

## 温度与硬件兼容性

- CPU 使用率和内存使用率不依赖厂商，Windows 10/11 通常都可以读取。
- CPU 温度没有统一的 Windows 公共接口，因此需要主板/CPU 的硬件监控传感器能够被 LibreHardwareMonitor 识别。
- 某些笔记本、服务器、较新的主板或 BIOS 可能不暴露 CPU 温度；此时使用率仍然正常，温度会显示为不可用。
- Windows 没有统一可靠的“内存温度”接口，因此 SysMonitor 不伪造或估算内存温度。
- GPU 温度只针对 NVIDIA `nvidia-smi` 路径；没有 NVIDIA GPU 时 GPU 卡片和 Band 项会自动隐藏。

## 设置与开机自启

设置文件：

```text
%APPDATA%\SysMonitor\settings.json
```

开机自启使用当前用户注册表，不需要管理员权限：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SysMonitor
```

设置项包括：

- Band 字体和字号。
- CPU、内存、GPU、下载、上传、磁盘项目之间的间距。
- Band 左右位置偏移。
- 任务栏图标和通知区的安全边界。
- 面板是否置顶。

## 构建

需要 Windows 环境和 .NET 7 SDK：

```powershell
dotnet restore .\SysMonitor\SysMonitor.csproj -r win-x64
dotnet publish .\SysMonitor\SysMonitor.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

发布结果位于 `bin/Release` 对应目录。项目默认发布为 framework-dependent 单文件版本；如果需要完全自包含版本，可将 `--self-contained false` 改为 `--self-contained true`，但文件体积会明显增大。

## 项目结构

```text
SysMonitor/
├─ Models/       # 设置模型和监控快照
├─ Services/     # 性能采集、任务栏定位、托盘和启动服务
├─ UI/           # Band、详情面板和外观设置窗口
├─ Assets/       # 图标与资源
└─ SysMonitor.csproj
docs/images/     # README 界面预览图
release/         # 可直接分发的单文件版本
```

## 常见问题

### 双击后没有启动

请确认目标系统安装了 Microsoft .NET 7 Desktop Runtime x64。正式版启动器会在检测到运行时缺失时打开官方下载页。

### 任务栏上没有 Band

先检查托盘区是否存在 SysMonitor 图标；如果有，打开托盘菜单中的“显示/隐藏面板”或“任务栏外观”。对于多显示器和自动隐藏任务栏，首次定位可能需要等待一次任务栏状态刷新。

### CPU 温度显示为空

这通常不是 CPU 使用率读取失败，而是当前电脑没有向 LibreHardwareMonitor 暴露可读的温度传感器。可以保留使用率、内存、GPU 和网络监控；程序不会显示不准确的估算值。

### GPU 项目隐藏

GPU 项目依赖 NVIDIA 驱动和 `nvidia-smi`。AMD、Intel 或没有 NVIDIA GPU 的电脑会自动隐藏该项目，不影响其他指标。

### Windows 10 任务栏出现闪烁

1. 确认使用的是最新 Release。
2. 在“任务栏外观”中调整左右位置和项目间距，避开通知区和任务栏图标安全区域。
3. 不要同时运行多个旧版本 SysMonitor；新版启动器会迁移并清理旧核心进程。

## 隐私与安全

- 指标只在本机采集和显示。
- 不上传 CPU、温度、网络速率、硬盘或设备信息。
- 不需要管理员权限。
- 开机自启只写入当前用户的 `HKCU` 注册表项。
- GPU 数据通过本机 `nvidia-smi` 进程读取，不连接远程服务。

## 许可证

本项目采用 [MIT License](LICENSE)。

## 变更记录

详见 [CHANGELOG.md](CHANGELOG.md)。
