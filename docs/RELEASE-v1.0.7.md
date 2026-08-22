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

## 验证

- Release 构建：0 警告、0 错误。
- 自动化测试：353/353 通过。
- NuGet 当前源未报告直接或传递依赖的已知漏洞。
- 真机 UAC 路径成功读取 CPU 温度与功耗。
- 详细内存口径和可复跑 A/B 脚本见 `docs/MEMORY-OPTIMIZATION-2026-08-22.md` 与 `tools/Measure-HelperMemory.ps1`。

## SHA-256

- `SysMonitor-v1.0.7-Light.exe`：`0CEF932698EA65E2F515EC7C6120D2D0AFEEC64A408E747902164AE2B7A0C272`
- `SysMonitor-v1.0.7-Standalone.exe`：`0ED4640F81630F8B95A60300F003234F8D22BBC36C6C304A557F62BFA40C54BB`
