# SysMonitor 1.2

SysMonitor 是一款轻量 Windows 系统监控工具。它把 CPU、内存、NVIDIA/AMD/Intel GPU 使用率与可用温度、实时网速和系统盘占用直接显示在主任务栏内；点击监控条或托盘图标可打开现代浅色详情面板。

## 系统要求

- Windows 10/11 64 位
- Framework-dependent 版本需要 Microsoft .NET 8 Desktop Runtime（x64）
- GPU 数据依赖 NVIDIA、AMD 或 Intel 正式显卡驱动公开的遥测接口
- 不需要管理员权限

## 使用

1. 运行 `SysMonitor.exe`。
2. 左键点击任务栏监控条或托盘图标，打开/隐藏详情面板。
3. 右键托盘图标可切换置顶、开机自启、任务栏外观或退出。
4. 外观设置支持跟随系统、简体中文或 English，并可调整字体、9–20 号字号、项目间距和 0%–100% 左右位置。
5. 详情面板关闭按钮只隐藏面板；完全退出请使用托盘菜单。

程序只允许运行一个实例。开机自启使用 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SysMonitor`，启动新版后会把已经存在的旧路径迁移到当前 EXE；如果原来没有开启自启，则不会擅自创建。

## 数据来源

- CPU：优先读取 `Processor Information(_Total)\% Processor Utility`，与现代 Windows 任务管理器的总体 CPU 口径一致；不可用时依次回退到 `% Processor Time` 和 `GetSystemTimes`。
- 内存：PSAPI `GetPerformanceInfo`。
- GPU：NVIDIA 优先使用一个常驻 `nvidia-smi` 数据流，AMD/Intel 及 NVIDIA 回退使用 LibreHardwareMonitor；显示驱动实际公开的使用率、型号、核心温度和专用显存。
- 网络：活动物理以太网与 Wi-Fi 网卡收发字节增量，默认排除 VPN、TAP/TUN 和虚拟网卡。
- 磁盘：Windows 系统卷容量占用率。

Windows 没有通用可靠的内存温度接口，因此不显示估算的内存温度。CPU 温度通过 LibreHardwareMonitor 读取，必要时按需启动同一 EXE 的管理员助手。GPU 只接受明确的核心温度传感器，不会把热点、显存结温或 VRM 温度冒充核心温度；驱动未公开的项目会显示为不可用。

## 任务栏行为

- Band 是 Explorer 任务栏的真实子窗口，透明背景，随任务栏自动隐藏动画一起移动。
- 定位使用任务栏客户区相对坐标；健康状态不会周期性重新定位，避免 Windows 10 任务栏指示线闪烁。
- 左右位置会根据任务栏应用图标和通知区图标实时计算；整个 Band 都被限制在两侧图标之间，不能覆盖图标。
- 安全边界向内立即收紧、向外连续确认；只要 Band 仍在安全区内，像素级边界波动不会带动它左右晃动。
- 深色任务栏显示白字，浅色任务栏显示黑字；支持 100%–200% DPI。
- 点击不抢焦点，详情面板惰性创建，350 ms 内重复输入会合并。
- 边界读取在后台 STA 线程执行，不阻塞界面；任务栏自动隐藏时保留同一 HWND 并随父窗口移动。
- 如果图标之间暂时没有足够空间，Band 会安全隐藏但继续检测；空间恢复后使用同一 HWND 自动重新显示。
- Band 支持顶部和底部水平任务栏；Windows 10 左/右侧竖直任务栏会安全隐藏 Band，托盘和详情面板仍可使用。
- 只有 Windows 已明确销毁原生窗口（例如 Explorer 重启）时才创建一个新 Band。
- 诊断日志位于 `%AppData%\SysMonitor\band-debug.log`。

## 构建单文件

```powershell
dotnet publish .\SysMonitor.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```
