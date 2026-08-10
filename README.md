# SysMonitor

轻量、便携的 Windows 任务栏系统性能监视器。它把 CPU、内存、GPU、温度、网络速率和系统盘使用率直接显示在任务栏中，并提供现代化详情面板。

## 功能

- 任务栏 Band：透明、无边框、无需抢焦点，支持任务栏顶部/底部和自动隐藏任务栏。
- 系统托盘：显示/隐藏面板、窗口置顶、开机自启和退出。
- 详情面板：Apple 风格卡片布局，支持拖拽、最小化和惰性加载。
- 数据采集：CPU、内存、NVIDIA GPU 使用率/温度/显存、网络上下行和系统盘使用率。
- 外观设置：字体、字号、项目间距 `0–18 px` 和安全区域内的左右位置。
- 稳定性：固定指标槽位、等宽数字、任务栏图标边界保护，以及 Windows 10 任务栏重绘抑制。
- 便携运行：单文件启动器自动释放核心程序，不写入安装目录，不需要管理员权限。

## 下载

当前版本：**1.2.13**

可直接运行的单文件版本位于：

`release/SysMonitor-1.2.13-single/SysMonitor.exe`

SHA-256：`46C139370370042D05EF25AF5262E645BE1CBD730C76BBEF2D0BDC9747F0FA7E`

## 系统要求

- Windows 10/11 x64
- Microsoft .NET 7 Desktop Runtime x64
- NVIDIA GPU 数据需要已安装正常工作的 NVIDIA 驱动和 `nvidia-smi`；没有 NVIDIA GPU 时 GPU 项目自动隐藏。
- CPU 温度依赖硬件传感器支持；Windows 没有统一的内存温度接口，因此不显示估算的内存温度。

## 构建

在安装 .NET 7 SDK 的 Windows 环境执行：

```powershell
dotnet restore .\SysMonitor\SysMonitor.csproj -r win-x64
dotnet publish .\SysMonitor\SysMonitor.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

发布结果是 framework-dependent 单文件版本；目标电脑需要 .NET 7 Desktop Runtime。启动器会在缺少运行时的情况下打开官方 .NET 下载页面。

## 数据来源与刷新

| 数据 | 来源 | 刷新间隔 |
| --- | --- | --- |
| CPU 使用率 | PDH `Processor Utility` | 1 秒 |
| CPU 温度 | LibreHardwareMonitor 传感器 | 按需/缓存 |
| 内存 | PSAPI `GetPerformanceInfo` | 1 秒 |
| GPU | `nvidia-smi` | 30 秒 |
| 网络 | `NetworkInterface.GetIPv4Statistics` | 1 秒 |
| 磁盘 | `DriveInfo.GetDrives()` | 10 秒 |

所有指标均在本机采集。程序不会上传性能数据。

## 运行与配置

首次运行后可通过托盘菜单打开“任务栏外观”。设置保存在：

`%APPDATA%\SysMonitor\settings.json`

开机自启使用当前用户注册表：

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SysMonitor`

## 许可证

本项目采用 MIT License，详见 [LICENSE](LICENSE)。

## 变更记录

详见 [CHANGELOG.md](CHANGELOG.md)。
