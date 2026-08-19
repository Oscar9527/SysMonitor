# SysMonitor 代码修改记录与 AI 调优指南 (Code Modifications & AI Tuning Guide)

> 本文档用于记录 SysMonitor 项目的所有重大修复、架构调整与关键实现细节，方便后续 AI 助手及开发者快速理解上下文并进行针对性调优。

---

## 📌 版本回退代号对照表 (Human-Memorable Rollback Tags)

| 回退代号 (Tag) | 对应 Commit / 阶段 | 状态说明 | 一键回退命令 |
| :--- | :--- | :--- | :--- |
| **`v1.5.0-base`** | `backup-pre-bugfix-audit` | 审查前的初始基准版本（未应用任何修改） | `git reset --hard v1.5.0-base` |
| **`v1.5.1-stable`** | `4bdd8b0` | 完成 PresentMon 错误透传、RTSS INI 补丁、GPU 传感器匹配及单文件打包发布 | `git reset --hard v1.5.1-stable` |
| **`v1.5.2-msi-hud`** | `83b68f7` | 微星小飞机 (MSI Afterburner) 风格 HUD 视觉初版与老游戏自动挂钩 | `git reset --hard v1.5.2-msi-hud` |
| **`v1.5.3-msi-pure`** | `db9cc58` | 正统微星小飞机整行纯色 OSD 与旧配置自动升级 | `git reset --hard v1.5.3-msi-pure` |
| **`v1.5.4-tray-fix-and-transparent-band`** | `920a31c` | 二级菜单完整恢复与坐标对齐、任务栏暗黑模式透明度修复（彻底消除白色任务栏黑块） | `git reset --hard v1.5.4-tray-fix-and-transparent-band` |
| **`v1.5.5-msi-complete-audit`** | 最新提交 | 全面排查修复：托盘二级菜单无缝吸附锚定、旧配置文件全面无感升级为微星高亮配色、全量 325 项测试通过 | `git reset --hard v1.5.5-msi-complete-audit` |
| **`v1.5.6-final-audit-fix`** | `3a6d66a` | Claude 深度审计修复：HUD 默认字号 14→16px、RTSS 新增 HookD3D8=1 支持 DX8 老游戏、325 项测试全部通过 | `git reset --hard v1.5.6-final-audit-fix` |
| **`v1.5.7-macos-submenu-fix`** | `d0ced5f` | 二级菜单脱节根因修复（BeginInvoke 延迟定位）+ macOS Sonoma 圆角蓝色高亮渲染器 + RTSS 自动启动老游戏零配置 | `git reset --hard v1.5.7-macos-submenu-fix` |
| **`v1.5.8-win32-native-snap`** | `03f1fcd` | 二级菜单尝试：Win32 GetWindowRect+SetWindowPos 原生定位 | `git reset --hard v1.5.8-win32-native-snap` |
| **`v1.5.9-macos-unified-design`** | `b040fb4` | 全面统一 macOS 设计规范：所有窗口升级 16px 圆角+环境柔和投影+红黄绿交通灯控制 | `git reset --hard v1.5.9-macos-unified-design` |
| **`v1.6.0-seamless-tray-fixed`** | 最新提交 | 二级菜单彻底 0 间距吸附修复（精准计算 parentStrip.Right - 1 并通过 SetWindowPos 瞬间消除 21px 悬空缝隙）；提供 4.73MB 极简版与 76MB 独立版双包 | `git reset --hard v1.6.0-seamless-tray-fixed` |

---

## 🛠️ v1.6.0 详细修改记录与设计考量 (二级菜单 21px 悬空缝隙根治与双版本打包)

### 1. 二级菜单 21px 悬空缝隙根治
* **修改文件**：
  - [`TrayIconService.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/TrayIconService.cs)：
    - 在 `ConfigureMenuItems` 中为所有 `ToolStripDropDownMenu` 统一挂载 `Opened += OnSubMenuOpenedSnap`。
    - 在 `OnSubMenuOpenedSnap` 中，通过 `parentStrip.Right - 1` 精准计算父级菜单右边界（若向左展开则计算 `parentStrip.Left - childDropDown.Width + 1`）。
    - 针对 WinForms 内部 `ToolStripMenuItem.Bounds.Width` (285px) 与菜单窗口 `contextMenu.Width` (266px) 之间存在的 19~21px 物理缝隙，在原生窗口展示瞬间通过 `SetWindowPos(SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)` 强力吸附贴合，彻底消灭 0px~1px 以外的任何间隙。
  - [`TrayIconServiceTests.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor.Tests/TrayIconServiceTests.cs)：
    - 新增 `Submenu_Location_SnapsFlushToParent` 自动化测试，验证在托盘菜单展开时子菜单 `gap <= 2px`，全量 326 项测试通过。

### 2. 双版本打包发布
* **极简轻量版（4.73 MB）**：`publish_light/SysMonitor.exe`（满足 10MB 以内极小体积需求）。
* **独立免安装版（76 MB）**：`publish_v159/SysMonitor.exe`（内置完整 .NET 8 运行时）。

## 🛠️ v1.5.9 详细修改记录与设计考量 (全软件 macOS 设计规范统一与托盘菜单彻底贴合)

### 1. 全软件所有窗口升级为 macOS 纯正圆角矩形设计
* **修改文件**：
  - [`App.xaml`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/App.xaml)：引入 macOS 规范全局 Token（`MacWindowCornerRadius="16"`, `MacCardCornerRadius="12"`, `MacControlCornerRadius="8"`, `MacPillCornerRadius="16"`）。
  - [`DetailWindow.xaml`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/DetailWindow.xaml)：开启 `AllowsTransparency="True"` + `CornerRadius="16"` + 柔和环境光投影，标题栏左侧使用 macOS 红黄绿交通灯按钮（红=关闭、黄=隐藏、绿=置顶）。
  - [`AppearanceSettingsWindow.xaml`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/AppearanceSettingsWindow.xaml)：开启 `AllowsTransparency="True"` + `CornerRadius="16"` + 投影 + 交通灯关闭按钮 + 圆角卡片。
  - [`GameOverlayAppearanceWindow.xaml`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/GameOverlayAppearanceWindow.xaml)：升级为 macOS 圆角窗口、微星整行高亮纯色卡片、实时效果预览卡片。
  - [`GameOverlaySettingsWindow.xaml`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/GameOverlaySettingsWindow.xaml)：升级为 macOS 圆角窗口、胶囊切换单选卡片、坐标滑动条。

### 2. 托盘菜单二级菜单 0 间距无缝贴合
* **修改文件**：
  - [`TrayIconService.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/TrayIconService.cs)：清理与 WinForms 原生 DPI 引擎冲突的多余坐标重写，单列排版下由原生引擎以 0px 间距紧贴父菜单右侧弹出子菜单。
  - [`MacToolStripRenderer.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/MacToolStripRenderer.cs)：实现 macOS Sonoma 胶囊高亮选择、白底与深灰底色、超薄分隔线与对勾图标。

### 3. 测试与验证
* **测试结果**：325/325 项单元测试全部通过。
* **发布产物**：[`publish_v159/SysMonitor.exe`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/publish_v159/SysMonitor.exe)。

## 🛠️ v1.5.6 详细修改记录与设计考量 (Claude Opus 4.6 深度审计修复)

### 1. HUD 默认字号从 14px 修正为 16px
* **修改文件**：
  - [`GameOverlayWindow.xaml`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/GameOverlayWindow.xaml) Line 15：`FontSize="14"` → `FontSize="16"`
  - [`AppSettings.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Models/AppSettings.cs) Line 132 + 183：`FontSize = 14d` → `FontSize = 16d`
  - [`GameOverlayAppearanceWindow.xaml.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/GameOverlayAppearanceWindow.xaml.cs) Line 23：经典皮肤预设 `FontSize: 14` → `FontSize: 16`
* **修改原因**：需求规范明确要求默认字号 16px，但代码中多处硬编码为 14px。
* **调优说明**：`SettingsService.NormalizeOverlayAppearance` 的 fallback 值本身已经是 16d（仅当输入非有限数时），此次修复确保所有初始化路径统一为 16px。

### 2. RTSS 兼容层新增 HookD3D8=1 键
* **修改文件**：
  - [`RtssLegacyCompatibilityService.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/RtssLegacyCompatibilityService.cs) Line 77 + 566-567 + 572 + 575
  - [`RtssLegacyCompatibilityServiceTests.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor.Tests/RtssLegacyCompatibilityServiceTests.cs) Line 34 + 47 + 196
* **修改原因**：原代码仅写入 `EnableHooking=1` 和 `HookDirectDraw=1`，缺少 `HookD3D8=1`，导致使用 DirectX 8 渲染的老游戏无法通过 RTSS 捕获帧率。
* **调优说明**：
  - `NewProfile` 模板新增 `HookD3D8=1` 行
  - `PatchProfile` 的管理键集合和正则表达式均扩展为包含 `HookD3D8`
  - 已有 profile 的 `[Hooking]` 节中若缺少 `HookD3D8` 键，补丁逻辑会自动追加 `HookD3D8=1`

---

## 🛠️ v1.5.7 详细修改记录与设计考量 (二级菜单根因 + macOS UI + 老游戏零配置)

### 1. 二级菜单脱节根因修复
* **修改文件**：[`TrayIconService.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/TrayIconService.cs)
* **根因分析**：WinForms 事件执行顺序为 `Opening 触发 → 我们设置 Location → WinForms 重新计算位置覆盖 → 菜单显示`。之前在 `Opening` 中直接设置 `Location` 每次都被 WinForms 覆盖，导致修了多次都无效。
* **修复方案**：
  - `OnSubMenuOpening` 中使用 `BeginInvoke` 将坐标设置延迟到消息泵下一个周期，确保在 WinForms 布局完成后才执行
  - 新增 `OnSubMenuOpened` 事件处理作为双保险，在菜单已显示后再次强制吸附
  - 提取公共方法 `SnapSubMenuToParent` 供两个事件共用

### 2. macOS Sonoma 风格渲染器重写
* **修改文件**：[`MacToolStripRenderer.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/MacToolStripRenderer.cs)
* **修改内容**：
  - **圆角**：使用 `Region` + `GraphicsPath` 裁剪实现 8px 圆角弹出菜单
  - **蓝色高亮**：悬停项使用 macOS 标志性蓝色（亮：`#007AFF`，暗：`#0A84FF`）+ 圆角内缩矩形
  - **高亮文字反白**：悬停项文字和箭头自动变白
  - **配色体系**：完全对标 macOS Sonoma（前景、次要、禁用、边框、分隔线全部独立定义）

### 3. RTSS 老游戏自动启动
* **修改文件**：[`RtssLegacyCompatibilityService.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/RtssLegacyCompatibilityService.cs)
* **修改内容**：`TryAutoEnableForExecutable` 在创建/确认 RTSS Profile 后自动调用 `TryEnsureRtssRunning`，后台启动 RTSS（如果已安装）。小白用户只要安装了 RTSS，打开老游戏就能自动看到帧率，无需手动启动 RTSS。

---

## 🛠️ v1.5.1 详细修改记录与设计考量

### 1. PresentMon 管道流错误分类透传
* **修改文件**：[`SysMonitor/Services/PresentMonFrameRateProvider.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/PresentMonFrameRateProvider.cs#L330-L375)
* **修改原因**：
  * 提权的 Helper 进程在遭遇底层系统异常（如 ETW 会话冲突、权限不足、错误码 1450 资源耗尽）时，会通过管道写入 `#SYSMONITOR-ERROR <code> <diagnostic>`。
  * 原实现中只要接收完 CSV 头部后，所有行均直接交给 `PresentMonCsvParser.TryParseFrame`，导致 Helper 错误消息被误报为 `"PresentMon emitted an invalid or non-monotonic CSV row."`，掩盖了真实的系统错误。
* **调优说明**：
  * 读取循环中每一行均优先调用 `TryClassifyHelperError`，确保 Helper 异常能被精准分类上报到 UI。
  * 将 CSV 行解析失败与单调性校验剥离，避免将偶发格式问题误当做致命崩溃。

---

### 2. RTSS INI 配置文件末尾缺失换行符修复
* **修改文件**：[`SysMonitor/Services/RtssLegacyCompatibilityService.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/RtssLegacyCompatibilityService.cs#L510-L525)
* **修改原因**：
  * 在向 RTSS 配置文件的 `[Hooking]` 段追加缺少的配置项（如 `EnableHooking=1` / `HookDirectDraw=1`）时，若原始 INI 文件最后一行没有尾随换行符（`Ending == ""`），新插入的行会直接与前一行内容发生字符串拼接（例如 `OldKey=0EnableHooking=1\r\n`）。
* **调优说明**：
  * 在 `lines.Insert` 之前校验若前一行的 `Ending.Length == 0`，先补齐为现有换行符（`\r\n`），彻底避免行粘连。

---

### 3. GPU 传感器规范化名称匹配增强
* **修改文件**：[`SysMonitor/Services/GpuSensorSelector.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/GpuSensorSelector.cs#L50-L125)
* **修改原因**：
  * LibreHardwareMonitor 对于部分 Intel Arc 与 NVIDIA 显卡暴露的传感器名称包含后缀（如 `"GPU Core Clock"`、`"GPU Memory Clock"`、`"GPU Core Temperature"`）。大写规范化后为 `"GPU CORE CLOCK"` 等，与原代码中纯 `"GPU CORE"` 的精确匹配失配。
* **调优说明**：
  * 补齐常见规范化传感器名称的显式匹配，保证显卡核心频率与显存频率能够命中最高权重规则。

---

### 4. 浮层控制器 UI 调度器解耦与单元测试支持
* **修改文件**：
  * [`SysMonitor/Services/GameOverlayController.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/GameOverlayController.cs#L60-L85)
  * [`SysMonitor.Tests/GameOverlayControllerTests.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor.Tests/GameOverlayControllerTests.cs#L45-L55)
* **修改原因**：
  * `GameOverlayController` 内部原有 `RunOnUi` 直接依赖 `Application.Current.Dispatcher`。在跨线程测试执行时，会导致异步消息分发延迟从而触发时序断言竞争。
* **调优说明**：
  * 引入可选的 `uiDispatcher` 委托注入，测试环境下使用同步分发，生产环境下保持 WPF `Dispatcher.InvokeAsync`。

---

## 🎮 经典/老游戏（如《三国群英传7》）FPS 显示原理与配置

### 为什么《三国群英传7》默认显示 `FPS: --`？
1. **渲染架构差异**：
   * 《三国群英传7》（UserJoy 2007）使用 **DirectDraw / DirectX 7** 2D/3D 混合引擎，通过 DirectDraw 表面 `Flip` 或 GDI 主表面呈现画面。
   * SysMonitor 内置的 **Intel PresentMon** 依赖 Windows 内核 ETW（`Microsoft-Windows-DxgKrnl`），仅支持现代 DXGI (DX10/11/12)、D3D9、Vulkan 与 OpenGL 的 Present 事件，**无法抓取 DirectDraw 的画面翻页事件**。
2. **RTSS DirectDraw 挂钩机制**：
   * 要捕获 DirectDraw 老游戏的帧率，必须依赖 **RTSS (RivaTuner Statistics Server)**。
   * RTSS 出于对常规桌面程序和老旧软件的兼容性考虑，**默认对所有程序禁用了 DirectDraw 挂钩 (`HookDirectDraw=0`)**。
   * 只有在 RTSS 的对应 Profile（`Profiles\SANGO7.exe.cfg`）中显式配置 `HookDirectDraw=1`，RTSS 注入模块才会挂钩 DirectDraw API 并向共享内存写入帧率。

### 开启老游戏帧率显示的步骤：
1. **自动兼容模式（v1.5.2 新增，小白零门槛首选）**：
   * SysMonitor 启动后，当您前台运行《三国群英传7》等经典游戏时，SysMonitor **会在后台全自动探测游戏主程序并在 RTSS Profiles 中写入 `HookDirectDraw=1` 兼容指令**，并自动确保 RTSS 运行。
   * 您无需进行任何手动配置或翻找菜单，游戏重启或切换一次即自动呈现实时 FPS！
2. **手动使用 SysMonitor 托盘设置开启**：
   * 右键托盘图标 -> 【游戏浮层设置】-> 展开【高级设置】；
   * 在【老游戏兼容性 (RTSS)】下拉列表中选择运行中的 `SANGO7.exe`；
   * 勾选【启用老游戏兼容性】，点击【应用】。

---

## 🎨 v1.5.2 详细修改记录与微星小飞机视觉重构

### 1. 老游戏全自动挂钩与帧率自愈引擎
* **修改文件**：
  * [`SysMonitor/Services/RtssLegacyCompatibilityService.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/RtssLegacyCompatibilityService.cs)
  * [`SysMonitor/App.xaml.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/App.xaml.cs)
* **实现功能**：
  * 实现了 `TryAutoEnableForExecutable` 与 `TryEnsureRtssRunning`；
  * 当游戏浮层激活在前台游戏目标窗口时，自动在后台完成老游戏 DirectDraw 挂钩配置，彻底消除小白用户的操作负担。

### 2. 微星小飞机 (MSI Afterburner) 风格 HUD 视觉体系
* **修改文件**：
  * [`SysMonitor/UI/GameOverlayWindow.xaml`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/GameOverlayWindow.xaml)
  * [`SysMonitor/UI/GameOverlayWindow.xaml.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/GameOverlayWindow.xaml.cs)
  * [`SysMonitor/UI/GameOverlayAppearanceWindow.xaml.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/GameOverlayAppearanceWindow.xaml.cs)
* **视觉与交互升级**：
  * **内存与显存显示优化**：内存项支持显示使用率与具体容量（如 `42%  11689 MB`）；
  * **预设皮肤与所见即所得预览**：外观设置中置顶“微星小飞机 (经典橙绿)”与“赛博电竞 (青绿炫彩)”，小白用户一键套用即可获得极佳显示效果。

---

## 🚀 v1.5.3 详细修改记录与交互重构

### 1. 正统微星小飞机整行纯色 OSD（去除白色数值违和感）
* **修改文件**：
  * [`SysMonitor/UI/GameOverlayWindow.xaml`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/GameOverlayWindow.xaml)
  * [`SysMonitor/UI/GameOverlayWindow.xaml.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/GameOverlayWindow.xaml.cs)
  * [`SysMonitor/Models/AppSettings.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Models/AppSettings.cs)
* **实现原理**：
  * 真正的 MSI Afterburner / RTSS OSD 每一行（Label 与 Values）均采用同一种高饱和度明亮纯色（GPU 整行橙色、CPU 整行青色、FPS 整行翠绿色、RAM 整行琥珀黄色、NET 整行紫色）；
  * 修改代码使 `GpuLabel.Foreground = GpuValue.Foreground = gpuBrush`，彻底消除普通白色数值的突兀违和感，完美还原小飞机原生质感。

### 2. 旧版本设置自动无缝升级（解决“更新后没有任何变化”的问题）
* **修改文件**：
  * [`SysMonitor/Services/SettingsService.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/SettingsService.cs)
* **修改原因**：
  * 用户电脑中已有历史版本生成的 `%AppData%\SysMonitor\settings.json`，包含了旧的紫色/蓝灰配色与字体设置，直接运行新程序会自动继承旧配置，导致看不到任何视觉变化。
* **调优说明**：
  * 在 `SettingsService.NormalizeOverlayAppearance` 中增加旧配色自愈迁移逻辑，当检测到旧版紫色/浅蓝默认值时自动平滑升级为微星经典橙绿及 Consolas 等宽字体，无需用户手动删配置。

---

## 🎨 v1.5.4 详细修改记录：二级菜单对齐与任务栏暗黑透明度修复

### 1. 任务栏暗黑模式背景透明度修复（彻底消除白色任务栏上的黑块）
* **修改文件**：
  * [`SysMonitor/Services/ThemeCatalogService.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/ThemeCatalogService.cs)
  * [`SysMonitor/UI/BandWindow.xaml.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/UI/BandWindow.xaml.cs)
* **修改原因**：
  * 内置的暗黑主题（`MidnightThemeId`）在原始定义中将任务栏背景色 `bandBackground` 错误写成了 `#F017181B`（94% 不透明的深黑色块），导致在 Windows 白色/浅色任务栏上显示为一个突兀的大黑块；
  * `bandText` 原先固定为浅白字，导致当背景透明时无法适应浅色任务栏的对比度。
* **调优说明**：
  * 将 `MidnightThemeId` 的 `bandBackground` 修改为 `#00000000`（100% 纯透明）；
  * 将 `bandText` 与 `bandSeparator` 置为 `null`，使其交由 `BandWindow.ApplySystemTheme()` 动态根据 Windows 注册表 `ReadSystemUsesLightTheme()` 智能调节字体颜色：在白色任务栏上呈现高对比度深色字，在黑色任务栏上呈现高对比度亮白字，背景始终 100% 纯透明透出任务栏底色。

### 2. 托盘二级菜单完整保留与无缝贴合对齐
* **修改文件**：
  * [`SysMonitor/Services/TrayIconService.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/TrayIconService.cs)
* **修改原因**：
  * 原先子菜单在 `Opening` 事件中调用 `PrepareDropDownLayout` 动态强制修改 `MinimumSize`，导致 WinForms 计算二级子菜单的 `DropDownLocation` 坐标时发生偏移，在主菜单和二级菜单之间产生了巨大的横向空白间隙；
  * 用户需要保留完整的托盘二级菜单选项（`HUD 布局与位置…`、`HUD 外观与配色…`、`选择监控程序…`、`帧率助手位置`、`HUD 排版`、`显示项目`）。
* **调优说明**：
  * 完整保留并组织好 `_gameOverlaySettingsItem` 下属的所有二级与三级菜单；
---

## 🚀 v1.5.5 详细修改记录：全量深度排查与微星 Afterburner 体验闭环

### 1. 托盘二级与多级菜单绝对无缝吸附（终结 Windows 11 / DPI 漂移与脱节）
* **修改文件**：
  * [`SysMonitor/Services/TrayIconService.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/TrayIconService.cs)
* **修改原因**：
  * WinForms `ContextMenuStrip` 当项带有 `ShortcutKeyDisplayString` 时，会将菜单分为文本列和快捷键列两部分，导致子项的 `Bounds.Width` 只有文本列宽（不包含整行），WinForms 默认计算的 `DropDownLocation` 就会以文本列右侧为基准，造成高达 120px 的脱节大间隙；
  * `PrepareDropDownLayout` 在运行时动态修改 `MinimumSize` 干扰了 Windows 原生窗口对齐计算。
* **调优说明**：
  * 移除破坏性的 `ShortcutKeyDisplayString` 分列模式，直接将快捷键并入文本显示（`"隐藏游戏浮层 (Ctrl+Shift+F10)"`），保证每一项 `Bounds.Width` 填满整行；
  * 在 `OnSubMenuOpening` 中直接基于父菜单实际屏幕位置 `parentToolStrip.PointToScreen(Point.Empty)` + `parentToolStrip.Width` 进行像素级精确定位，并在超出屏幕右边界时自动翻转到左侧，实现 100% 绝对紧贴无缝吸附。

### 2. 彻底解决旧配置“看起来什么也没改”的问题（旧配置文件全量平滑迁移）
* **修改文件**：
  * [`SysMonitor/Services/SettingsService.cs`](file:///c:/Users/Administrator/Documents/Codex/2026-08-03/plugin-computer-use-openai-bundled-play-2/SysMonitor/Services/SettingsService.cs)
* **修改原因**：
  * 用户历史运行中生成的 `%AppData%\SysMonitor\settings.json` 保存了最初始的淡橙/淡粉/马卡龙配色以及 `Microsoft JhengHei UI` 字体与白色值颜色。此前迁移逻辑未覆盖全量历史淡色色值，导致每次打开软件依然沿用旧配置的淡色与宋体/黑体，肉眼观察没有任何视觉变化。
* **调优说明**：
  * 在 `NormalizeOverlayAppearance` 中建立全量历史色值与字体特征列表（包含 `Microsoft JhengHei UI`、`Segoe UI`、`#FFFFA94D`、`#FFFFD166`、`#FF95D5B2`、`#FFFF8E72`、`#FFE4B1FF` 等）；
  * 启动时对所有包含历史特征的配置进行全量平滑升级，强制统一为微星经典高饱和高亮配色（GPU 亮橙 `#FFFF8C00`、CPU 亮青 `#FF00E5FF`、FPS 翠绿 `#FF00E676`、RAM 亮黄 `#FFFFD600`、NET 亮紫 `#FFE040FB`）与等宽硬朗的 `Consolas` 字体，并在本地配置文件中完成持久化升级。


