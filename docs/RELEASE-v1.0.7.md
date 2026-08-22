v1.0.7 重点降低监控开启后的辅助进程内存，并修复长时间运行可能积累的缓存与任务栏事件队列。

## 下载说明

- **SysMonitor-v1.0.7-Standalone.exe（推荐）**：内置 .NET 8，无需另外安装运行时。
- **SysMonitor-v1.0.7-Light.exe**：体积更小，需要 x64 .NET 8 Desktop Runtime；缺少运行时时会提示并可进入微软官方下载。

## 主要改进

- CPU 温度与 PresentMon 助手在 WPF 初始化前分流，避免辅助进程加载完整 WPF 界面。
- HUD 窗口、帧率提供器与控制器改为首次使用时创建。
- 限频日志键最多保留 512 个并按 24 小时过期。
- PresentMon 交换链缓存最多保留 256 条并淘汰非活动链。
- 合并任务栏 WinEvent 的 Dispatcher 通知，避免事件风暴积压闭包。
- 保留 CPU 温度、功耗、UAC、命名管道、Light 和 Standalone 的原有功能。

## SHA-256

- `SysMonitor-v1.0.7-Light.exe`：`CE52E5735754035B04ACF5E1E2599C76DA42FC4AA12D76AEFAB7EDB1AB353752`
- `SysMonitor-v1.0.7-Standalone.exe`：`29851CF5B6091EE707B7B3093899A83AE247FCD35B7AFAC4DCAC9D3038516DCE`
