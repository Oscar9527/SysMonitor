# SysMonitor v1.0.6 发布验证记录

验证日期：2026-08-22（Asia/Shanghai）

基线：Git tag `v1.0.5`，commit `4272e646e1b61eef8e2799514d4d017d700c53ee`

交付版本：`1.0.6`（从上述 v1.0.5 基线完成审计与修复）

## 打包环境

| 组件 | 实际版本 / 配置 |
| --- | --- |
| 操作系统 | Microsoft Windows 11 企业版，10.0.22631（Build 22631），x64 |
| PowerShell | Windows PowerShell 5.1.22621.4249 |
| .NET SDK | 8.0.424，commit `5cbde90d8f`，RID `win-x64` |
| .NET Host / Runtime | 8.0.30，x64，commit `a83db3e0eb` |
| Windows Desktop Runtime | Microsoft.WindowsDesktop.App 8.0.30 x64 |
| MSBuild | 17.11.48.46605（`dotnet --info` 标识 17.11.48+02bf66295） |
| C# 编译器 | 4.11.0-3.25569.22，commit `3fb752d4` |
| 启动器引用框架 | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319`；注册表 Version `4.8.09032`、Release `533320` |
| NuGet 源 | `https://api.nuget.org/v3/index.json` |
| 仓库 `global.json` | 无；验证时显式调用隔离安装的 SDK 8.0.424 |

发布参数：`Release`、`win-x64`。便携启动器内嵌
`SelfContained=false`、`PublishSingleFile=true` 的核心；自包含验证使用
`SelfContained=true`、`PublishSingleFile=true`，并只在此形态启用单文件压缩。

## 自动化验证

- SDK：.NET SDK 8.0.424；目标框架：`net8.0-windows`。
- 干净 Release 重建：0 警告、0 错误。
- 测试：338 通过、0 失败、0 跳过。
- NuGet 审计：当前源未报告直接或传递依赖的已知漏洞。
- `git diff --check`：无空白错误；Git 仅提示工作区行尾将按配置转换。

## 发布验证

| 形态 | 文件 | 字节 | SHA-256 | 结果 |
| --- | --- | ---: | --- | --- |
| Light | `artifacts/SysMonitor-v1.0.6-Light.exe` | 5,520,384 | `320D947A22A65472902BE84940EF74C4663E8BBB14750CD29274ADC469134F33` | 版本 1.0.6.0；内嵌 `SysMonitor.Core.exe`；缺少 x64 Desktop Runtime 时显示选择提示并打开微软官方直达下载 |
| Standalone | `artifacts/SysMonitor-v1.0.6-Standalone.exe` | 73,551,420 | `2051710DC621D54BC12EA159E5C55F712DB15FD40D10066CC43724150AEFF7EA` | 版本 1.0.6.0；win-x64、自包含、压缩单文件；无需另装 .NET |

首次便携发布验证暴露 `NETSDK1176`：框架依赖单文件不支持压缩。项目文件现仅在 `SelfContained=true` 且 `PublishSingleFile=true` 时启用压缩。`Build-Release.ps1` 已成功一次生成上述 Light 和 Standalone 两个正式文件。

## 覆盖范围

自动化测试覆盖设置并发/损坏恢复/修订号边界、监控服务失败后清理与重启、PresentMon 生命周期、UI 快照调度器及已有模型和服务测试。验证未启动 GUI，也没有修改系统启动项、RTSS 配置或用户设置。

以下结论不能由本次无侵入自动化验证证明：真实硬件传感器准确度、各厂商驱动兼容性、长时间工作集/私有内存曲线、不同 DPI 与任务栏布局、真实游戏 ETW/RTSS 行为、反作弊产品兼容性。发布前应按根目录 `AUDIT-v1.0.5.md` 的真机矩阵补测。
