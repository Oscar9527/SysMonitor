# SysMonitor v1.0.5

SysMonitor 是一款 Windows 系统硬件与状态监视工具。支持在任务栏和游戏内悬浮窗（HUD）中实时查看 CPU、GPU、内存、网速和磁盘状态。

## 主要功能

### 1. 任务栏监控条
- 嵌入在任务栏空白区域，随任务栏自动隐藏和移动。
- 支持深色/浅色任务栏文字对比度自适应。
- 支持自定义各项指标子项：
  - **CPU**：可分别开启或关闭【使用率 %】、【温度 °C】、【功耗 W】
  - **GPU**：可分别开启或关闭【使用率 %】、【温度 °C】、【功耗 W】
  - **内存**：可分别开启或关闭【使用率 %】、【已用容量 GB】
  - **IO**：可分别开启或关闭【下载速度】、【上传速度】、【系统盘占用】
- 支持调整字体、字号、间距与左右对齐位置。
- 点击监控条可打开详情面板，查看 60 秒硬件历史曲线。

### 2. 游戏浮层 HUD
- 快捷键 `Ctrl+Shift+F10` 随时开启/关闭。
- 鼠标点击穿透，不抢占游戏焦点。
- 支持游戏窗口前台检测与自动吸附跟随（当游戏不在最前台或最小化时自动隐藏 HUD）。
- 支持水平 / 垂直两种排版，支持在屏幕任意位置拖动定位与微调。
- 支持自定义显示项目：FPS、CPU（含功耗）、GPU（含功耗）、内存、网络。
- 颜色与字体完全可自定义（支持一键套用 MSI Afterburner 风格配色）。

## 权限与 CPU 温度说明

- **日常使用无需管理员权限**：常规监控、网速、内存、GPU、磁盘等均无需提权。
- **关于 CPU 温度与功耗读取**：
  - 如果未以管理员身份运行，软件首次读取 CPU 硬件传感器时可能需要几秒钟的初始化等待。
  - 如果希望刚启动就立刻读取到 CPU 温度和功耗，可选择“以管理员身份运行”。
  - 不使用管理员权限仅影响启动后前几秒的 CPU 温度与功耗初始化，并非必须开启管理员权限。

## 版本与下载

- **Standalone 独立版**：单文件打包，内置完整运行环境，双击即可直接运行，适合没有安装 .NET 环境的电脑。
- **Light 轻量版**：体积小巧（约 5MB），需要系统已安装 .NET 7/8 Desktop Runtime。

## 构建方式

```powershell
# 编译 Standalone 独立版
dotnet publish .\SysMonitor.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o bin\PublishStandalone

# 编译 Light 轻量版
dotnet publish .\SysMonitor.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o bin\PublishLight
```
