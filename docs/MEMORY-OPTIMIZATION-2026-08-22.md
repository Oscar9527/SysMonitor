# SysMonitor 内存优化验证（2026-08-22）

## 结论

本轮优化能显著降低 CPU 温度辅助进程的真实私有内存，同时小幅降低从未使用 HUD 时的主进程基线。它不能把 WPF 主进程压到 20–30MB，也不使用强制 GC 或工作集裁剪伪造低值。

## 指标口径

- `Private Bytes`：进程独占的已提交内存，是本轮主要验收指标。
- `Working Set`：当前驻留物理页，包含私有页和共享页；对应任务管理器观感，但会受文件缓存、驱动、RTSS 和其他进程影响。
- `GC Heap`：仅代表托管堆。v1.0.6 主进程 15 秒采样为约 4.0–5.05MB，不能解释 100MB 以上的工作集。
- 主进程、CPU 辅助进程和 `nvidia-smi`/PresentMon 等条件性子进程必须分开记录，再计算进程树总量。

## 基线

运行 v1.0.6 Light，任务栏 Band 开启、HUD 隐藏：

| 进程 | Private Bytes | Working Set | 备注 |
| --- | ---: | ---: | --- |
| 主进程（15 次稳定采样均值） | 126.66MiB | 156.68MiB | WPF + WinForms 托盘 + 监控服务 |
| 提权 CPU 辅助进程 | 69.30MiB | 61.67MiB | 同一完整 WPF 可执行文件 |
| 合计 | 195.96MiB | 218.35MiB | 不含短时 `nvidia-smi` |

## 辅助进程同负载 A/B

方法：旧版与新版均通过当前用户命名管道直接启动合法的
`--cpu-temperature-helper` 请求；等待管道连接、LibreHardwareMonitor 打开并输出首行，再以 500ms 间隔取 10 次样本。每版运行三次。非提权场景均返回 `NA,NA`，因此比较的是相同的传感器打开/管道负载，而不是不同权限下的传感器成功率。

仓库已提供可复跑脚本：

```powershell
.\tools\Measure-HelperMemory.ps1 `
  -OldExecutable <v1.0.6-core-or-standalone.exe> `
  -NewExecutable <optimized-core-or-standalone.exe> `
  -Runs 3
```

| 版本（三次中位数） | Private Bytes | Working Set | 线程 | 模块 |
| --- | ---: | ---: | ---: | ---: |
| v1.0.6 | 66.96MiB | 53.70MiB | 16 | 84 |
| 当前优化 | 9.06MiB | 30.69MiB | 16 | 52 |
| 变化 | -57.90MiB（-86.5%） | -23.01MiB（-42.8%） | 0 | -32 |

新版辅助路径未加载 `PresentationFramework.dll`、`PresentationCore.dll` 或 `System.Windows.Forms.dll`。其关键点不是拆出第二个发布文件，而是让同一单文件在构造 WPF `Application` 之前完成模式分流。

Standalone 另做一次同负载指示性对照：v1.0.6 辅助进程为 120.97MiB Private Bytes/150.73MiB 工作集，当前优化为 23.55MiB/57.01MiB。自包含单文件的托管程序集可能从 bundle 内存加载，不能仅依赖 `Process.Modules` 判断 WPF 是否加载，因此这里以同机同负载内存结果和入口代码路径为证据，不把模块列表当作独立结论。

## 真实 UAC 与主进程

新版由正常主进程以原有 `runas` 路径启动辅助进程，日志确认读取到 CPU 81.5°C、功耗 21.2W。15 次稳定样本：

| 状态 | Private Bytes | Working Set |
| --- | ---: | ---: |
| 主进程，HUD 从未使用 | 122.96MiB | 150.19MiB |
| 提权 CPU 辅助进程 | 9.92MiB | 36.51MiB |
| 进程树合计（不含短时 `nvidia-smi`） | 132.88MiB | 186.70MiB |
| 主进程，首次打开 HUD 后 | 128.73MiB | 159.07MiB |

相对最初稳定基线，未使用 HUD 时主进程约减少 3.70MiB Private Bytes/6.49MiB 工作集；整个主进程 + CPU 辅助进程约减少 63.08MiB Private Bytes/31.65MiB 工作集。主进程对照和新版采样不是同一进程生命周期，数值应视为本机实测而非所有机器的保证。

## 代价与边界

- HUD 懒加载：首次热键/托盘打开需要创建窗口、控制器和帧率提供器；本机日志显示在发送热键后的同一秒完成。首次创建后对象保留到退出，内存会回到 HUD 正常使用水平。
- 辅助进程：入口调度和异步宿主略增加代码复杂度；正常 UI 在独立 STA 线程运行。温度、功耗、UAC、管道与单文件部署协议不变。
- 有界缓存：当 512 个日志键或 256 条交换链的上限被异常负载占满时，会优先淘汰最旧的非活动记录；实时活动链和最新日志键保留。
- WPF/WinForms 主界面仍是主进程大头。若继续追求明显低于 100MB 的主进程工作集，需要评估原生托盘替换、传感器彻底进程隔离甚至 UI 技术栈重写，开发和兼容性代价显著更高。
- 不建议 `EmptyWorkingSet`、周期性强制 Gen2 GC 或 GC 硬上限；这些做法容易增加缺页、卡顿或 OOM 风险，却不等于减少真实功能所需内存。

## 验证

- 自动化测试：353/353 通过。
- Release 编译：0 警告、0 错误。
- `StartupObject`：`SysMonitor.Program`。
- 框架依赖单文件核心：5,448,592 字节。
- 自包含压缩单文件：73,555,073 字节。
- NuGet 直接/传递依赖：当前源未报告已知漏洞。
- 这些改动组成 v1.0.7；v1.0.6 GitHub 资产保持不变。
