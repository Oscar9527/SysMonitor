# SysMonitor

## v1.5 游戏监控悬浮窗

- 按 `Ctrl+Shift+F10` 显示或隐藏透明悬浮窗；窗口不抢焦点、鼠标可穿透，并跟随目标程序所在显示器。
- 显示 Present FPS，以及系统总体 CPU、内存、GPU、温度和可用的当前频率。缺失数据会显示 `--`，不会伪造数值。
- 帧率使用固定随包的 PresentMon 2.5.1 Windows ETW 独立采集，不依赖 MSI Afterburner 或 RTSS。SysMonitor 自身不注入 DLL、不 Hook 图形 API、不读写游戏进程内存，也不为帧率安装驱动。
- 游戏安全模式默认开启；GPU 兼容传感器保持关闭，CPU 温度由 SysMonitor 自带的 CPU 专用读取器独立采集。若主动启用完整兼容传感器，仍需重启，且该次运行不会启动游戏悬浮窗。
- ETW 属于 Windows 系统级观测机制，但不同游戏和反作弊策略可能不同，因此本项目不宣称获得 ACE 或其他反作弊产品认证。无权限或没有 Present 事件时会显示明确状态。

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
| GPU | NVIDIA、AMD、Intel 使用率；硬件可提供时显示核心温度和专用显存 |
| 历史曲线 | 详情面板显示最近 60 秒 CPU、GPU 使用率；漏采或不可用数据以断线表示 |
| 点击交互 | 整个透明 Band 区域均可点击；打开详情时保持当前应用焦点 |
| 网络 | 所有活动 IPv4 网卡合计的下载/上传速率 |
| 磁盘 | Band 显示系统盘使用率；详情面板显示所有可用固定磁盘的已用/总容量与使用率 |
| 托盘 | 显示/隐藏面板、窗口置顶、开机自启、退出 |
| 外观 | 字体、字号、项目间距 `0–18 px`、左右位置、按项显示/隐藏、简体中文/English、安全区域和安全主题包 |
| 稳定性 | 父任务栏相对定位、固定指标槽位、等宽数字、边界迟滞和 Windows 10 重绘抑制 |
| 便携运行 | 单文件启动器自动释放核心程序，不写入安装目录，不需要管理员权限 |

## 下载与发行版

- [下载最新 Release](https://github.com/Oscar9527/SysMonitor/releases)
- [SysMonitor v1.4.1](https://github.com/Oscar9527/SysMonitor/releases/tag/v1.4.1)
- [直接下载 SysMonitor.exe](https://github.com/Oscar9527/SysMonitor/releases/download/v1.4.1/SysMonitor.exe)

当前公开版本：**1.4.1**（v1.5.0 游戏悬浮窗正在开发分支验证）

发行包信息：

| 项目 | 值 |
| --- | --- |
| 文件名 | `SysMonitor.exe` |
| 平台 | Windows 10/11 x64 |
| 类型 | 便携式、单文件、无需安装 |
| 大小 | 7,126,016 字节（约 6.80 MiB） |
| SHA-256 | `68E812EEF1BD57692D3BF86DBD7367BCA09F35F1597F4D3A9E4EBF3F5F996C5B` |

> 这是 framework-dependent 单文件版本，目标电脑需要安装 Microsoft .NET 8 Desktop Runtime x64。启动器检测到运行时缺失时，会打开官方 .NET 下载页面；不会静默安装或提权。

## 快速开始

1. 从 [Release 页面](https://github.com/Oscar9527/SysMonitor/releases) 下载 `SysMonitor.exe`。
2. 双击运行，不需要安装，也不需要管理员权限。
3. 程序启动后，Band 会出现在任务栏附近，托盘区会出现 SysMonitor 图标。
4. 点击 Band 或托盘图标打开详情面板。
5. 在托盘菜单中打开“任务栏外观 / Taskbar appearance”，调整语言、字体、字号、间距和左右位置。

关闭详情窗口只会隐藏面板，程序仍会在托盘和任务栏 Band 中运行；需要完全退出时，请使用托盘菜单的“退出”。

## 显示规则

### 任务栏 Band

- 小任务栏（高度 ≤ 30 px）：使用紧凑布局。
- 默认任务栏（31–40 px）：使用标准布局。
- 大任务栏（> 40 px）：使用宽屏布局。
- 任务栏在底部时，Band 显示在任务栏上方；任务栏在顶部时，Band 显示在任务栏下方。
- 支持 100%–200% DPI 缩放。
- 指标槽位固定宽度，数值使用等宽数字，避免数值变化造成左右晃动。
- Band 是 `Shell_TrayWnd` 的原生子窗口，自动隐藏时保持客户区相对坐标并随任务栏一起移动，不使用 Explorer 注入。
- 安全边界向内变化会立即生效；向外扩大需要连续确认。只要当前 Band 仍在安全区内，1–2 px 的图标边界波动不会触发移动。
- Band 当前支持底部和顶部的水平任务栏。Windows 10 左/右侧竖直任务栏会安全隐藏 Band，托盘图标和详情面板仍可使用。

### 详情面板颜色

- 使用率低于 75%：CPU 蓝色、内存紫色、GPU 绿色。
- 75%–89%：橙色警告。
- 90% 及以上：红色危险。

## 数据来源与刷新周期

| 数据 | 来源 | 刷新周期 | 备注 |
| --- | --- | --- | --- |
| CPU 使用率 | PDH `Processor Utility` | 1 秒 | 使用 Windows 性能计数器 |
| CPU 温度 | CPU 专用 LibreHardwareMonitor 读取器 | 每秒 | 无需安装 MSI Afterburner；必要时使用独立管理员助手 |
| 内存 | PSAPI `GetPerformanceInfo` | 1 秒 | 物理内存使用率 |
| GPU（NVIDIA） | `nvidia-smi`，LibreHardwareMonitor 回退 | 1 秒 | 优先使用 NVIDIA 驱动数据 |
| GPU（AMD/Intel） | LibreHardwareMonitor | 1 秒 | 读取驱动公开的利用率和可用传感器 |
| 网络 | `NetworkInterface.GetIPv4Statistics` | 1 秒 | 所有活动网卡合计 |
| 网卡列表 | `GetAllNetworkInterfaces` | 60 秒 | 重新发现活动网卡 |
| 磁盘 | `DriveInfo.GetDrives()` | 10 秒 | 详情显示全部固定磁盘，Band 只显示系统盘 |

所有指标均在本机采集，程序不会上传性能数据。

## 温度与硬件兼容性

- CPU 使用率和内存使用率不依赖厂商，Windows 10/11 通常都可以读取。
- CPU 温度没有统一的 Windows 公共接口，因此需要主板/CPU 的硬件监控传感器能够被 LibreHardwareMonitor 识别。
- 某些笔记本、服务器、较新的主板或 BIOS 可能不暴露 CPU 温度；此时使用率仍然正常，温度会显示为不可用。
- Windows 没有统一可靠的“内存温度”接口，因此 SysMonitor 不伪造或估算内存温度。
- NVIDIA、AMD 和 Intel 显卡均可显示。双显卡电脑会选择当前更活跃的适配器，并使用切换防抖避免名称来回跳动。
- GPU 核心温度只在驱动公开了可靠的核心温度传感器时显示；热点、显存结温、VRM 温度不会冒充核心温度。
- Intel 集显以及部分 AMD/笔记本驱动可能只公开使用率，不公开核心温度或物理显存总量；缺失项显示为不可用，不以 `0` 伪装。

当前兼容性验证状态：

| GPU 厂商 | 实现状态 | 真实硬件验证 |
| --- | --- | --- |
| NVIDIA | `nvidia-smi` 主通道 + LibreHardwareMonitor 故障回退 | 已在 RTX 3060 Laptop GPU / Windows 11 验证 |
| AMD | LibreHardwareMonitor 核心负载、核心温度、可用专用显存 | 已通过传感器选择与缺失值自动测试；等待更多真实 AMD GPU 反馈 |
| Intel | LibreHardwareMonitor D3D 引擎负载、可用专用显存 | 已通过多引擎选择与缺失值自动测试；等待更多真实 Intel GPU 反馈 |

“支持”表示程序已经识别并处理该厂商的数据结构，不代表每块显卡都一定公开温度或显存总量。最终显示内容以显卡驱动实际提供的传感器为准。

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

- 界面语言：跟随系统、简体中文或 English；切换后立即生效。
- 主题：内建 Default / Midnight，或导入经过安全校验的 `.smonitor-theme` 文件；选择时实时预览，点击“应用”后保存。
- Band 字体和字号。
- CPU、内存、GPU、下载、上传、磁盘项目之间的间距。
- CPU、内存、GPU、下载、上传和系统盘可分别显示或隐藏；全部隐藏后可从托盘重新打开设置。
- Band 左右位置偏移。
- 任务栏图标和通知区的安全边界。
- 面板是否置顶。

## 自定义主题

1.3.0 的主题系统采用纯数据包设计：主题可以修改应用卡片、指标颜色、警告色、圆角、阴影、Band 背景和托盘图标，但不能包含或执行 DLL、脚本、XAML、字体及其他任意代码。

主题包通过“任务栏外观”窗口导入，安装目录为：

```text
%APPDATA%\SysMonitor\Themes
```

程序会对 ZIP 路径、文件白名单、压缩率、解压大小、JSON 字段及 PNG/ICO 实际内容进行校验，并通过临时目录完整复验后一次性安装。关闭设置窗口会恢复最后已应用的主题，刚导入的包则保留在本地，方便之后再次选择。

完整格式、限制和可直接打包的示例见 [主题包开发文档](docs/themes/README.md)。

## 构建

需要 Windows 环境和 .NET 8 SDK：

```powershell
dotnet restore .\SysMonitor\SysMonitor.csproj -r win-x64
dotnet publish .\SysMonitor\SysMonitor.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

发布结果位于 `bin/Release` 对应目录。项目默认发布为 framework-dependent 单文件版本；如果需要完全自包含版本，可将 `--self-contained false` 改为 `--self-contained true`，但文件体积会明显增大。

要生成与 Release 相同、带运行时检测和升级交接的最终便携单文件：

```powershell
.\Launcher\Build-Portable.ps1
```

结果位于 `artifacts\SysMonitor.exe`，脚本会同时输出 SHA-256。

## 项目结构

```text
SysMonitor/
├─ Models/       # 设置模型和监控快照
├─ Services/     # 性能采集、任务栏定位、托盘和启动服务
├─ UI/           # Band、详情面板和外观设置窗口
├─ Assets/       # 图标与资源
└─ SysMonitor.csproj
SysMonitor.Tests/ # GPU、任务栏稳定、本地化和设置迁移测试
Launcher/         # 最终便携单文件启动器与构建脚本
docs/images/     # README 界面预览图
docs/themes/     # 主题包规范、限制与可打包示例
release/         # 可直接分发的单文件版本
```

## 常见问题

### 双击后没有启动

请确认目标系统安装了 Microsoft .NET 8 Desktop Runtime x64。正式版启动器会在检测到运行时缺失时打开官方下载页。

### 任务栏上没有 Band

先检查托盘区是否存在 SysMonitor 图标；如果有，打开托盘菜单中的“显示/隐藏面板”或“任务栏外观”。对于多显示器和自动隐藏任务栏，首次定位可能需要等待一次任务栏状态刷新。

### CPU 温度显示为空

CPU 温度由 SysMonitor 自带的 CPU 专用 LibreHardwareMonitor 读取器独立采集，不依赖 MSI Afterburner。普通权限无法读取 MSR 温度时，程序会按需启动同一 EXE 的独立管理员助手；若硬件或固件仍未公开温度，显示 `--`，不会伪造数值。

### GPU 项目隐藏或部分数据为空

请先安装对应厂商的正式显卡驱动。SysMonitor 支持 NVIDIA、AMD 和 Intel，但温度与显存项目取决于具体驱动、显卡和传感器是否公开；使用 Microsoft 基本显示适配器、远程虚拟显卡或过旧驱动时可能没有可读数据。AMD/Intel 路径已经实现并通过确定性逻辑测试，仍需要更多真实机型反馈来扩充兼容性记录。

### Windows 10 任务栏出现闪烁

1. 确认使用的是最新 Release。
2. 1.2.16 已取消健康状态下的周期性重新定位，并过滤任务栏安全边界的像素级波动。
3. 在“任务栏外观”中调整左右位置和项目间距，避开通知区和任务栏图标安全区域。
4. 不要同时运行多个旧版本 SysMonitor；新版启动器会迁移并清理旧核心进程。

## 隐私与安全

- 指标只在本机采集和显示。
- 不上传 CPU、温度、网络速率、硬盘或设备信息。
- GPU 监控和主程序不需要管理员权限；少数无法直接读取 CPU 温度的机器可能触发既有的可选管理员温度助手。
- 开机自启只写入当前用户的 `HKCU` 注册表项。
- GPU 数据通过本机显卡驱动和 LibreHardwareMonitor 读取，不连接远程服务。

## 许可证

本项目采用 [MIT License](LICENSE)。

## 变更记录

详见 [CHANGELOG.md](CHANGELOG.md)。版本提交、标签和安全回退流程见 [版本留存与回退](docs/RELEASES.md)。
