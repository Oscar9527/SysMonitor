# 游戏监控 Overlay 路线图

游戏监控层与任务栏 Band 使用不同的窗口和生命周期，避免 FPS 采集或游戏兼容问题影响常驻监控器的稳定性。

## 第一阶段：透明监控窗

- 新增独立 `GameOverlayWindow`，显示现有 CPU、内存、GPU、温度和显存数据。
- 使用 `RegisterHotKey` 注册可配置全局快捷键；快捷键冲突时明确提示，并在退出时可靠注销。
- 窗口无边框、置顶、无激活、点击穿透；进入编辑模式时临时允许拖拽、调整透明度、锚点和显示项目。
- 系统指标继续按 1 秒刷新，不因为游戏帧率提高 WPF 重绘频率。

## 第二阶段：真实 FPS

- FPS 使用可关闭、无 DLL 注入的 ETW/PresentMon 采集进程，并与主程序隔离。
- 只报告目标进程真实 Present 事件的统计值，不使用 GPU 占用、显示器刷新率或其他数据推算 FPS。
- FPS 数据包含目标 PID、来源、采样时间和状态；无目标、不支持、权限不足、过期或采集失败均显示 `--` 和原因，不显示伪造的 `0 FPS`。
- 无边框和窗口化游戏是主要支持目标。真正独占全屏可能绕过桌面合成，普通透明窗口不保证能够覆盖。

## 第三阶段：频率与更多传感器

- NVIDIA GPU 优先读取驱动公开的核心/显存实时频率；AMD、Intel 仅在明确传感器可用时显示。
- CPU 频率区分“传感器实测”和“Windows 性能计数器估算”，界面标注来源，不把标称频率当实时频率。
- 所有温度和频率字段保持可空；硬件或驱动没有公开数据时显示不可用。

## 游戏兼容与安全边界

- 不采用 DLL 注入、DirectX Hook、游戏内存读写、内核驱动或低级键盘钩子。
- 提供“游戏兼容模式”，可禁用 LibreHardwareMonitor 和 CPU 温度提权助手，只保留 Windows 与厂商公开接口。
- FPS 采集只在 Overlay 可见且目标游戏稳定时运行；隐藏 Overlay 后立即停止采集进程。
- ETW/PresentMon 虽然不注入游戏，但仍不能承诺与所有反作弊系统兼容；不支持时应安全降级。

## 建议模块

```text
MonitorService ───────────────┐
                             ├─ OverlaySnapshotComposer ─ GameOverlayWindow
FrameRateService ────────────┘
GameTargetResolver ──────────┘

GlobalHotkeyService ─ OverlayController
OverlaySettings ───── OverlayController
```

FPS 采集、目标游戏识别和窗口渲染彼此隔离，任一模块失败都不能终止任务栏 Band 或系统数据采集。
