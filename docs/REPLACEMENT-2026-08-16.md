# 2026-08-16 跨电脑源码完整替换记录

## 结果

本分支把以下快照作为权威项目源码，而不是从中选择功能合并：

`C:\Users\Administrator\Desktop\SysMonitor-完整源码与开发进度-1.5.0-20260816-1122`

- 替换前 Git 基线：`e0af0ac`（`v1.5.0-beta.2`）
- 完整替换分支：`codex/replace-20260816-snapshot`
- 原样替换提交：`b375617`
- 打包缓存修复提交：`ce35311`
- 原先误做的选择性合并保留在
  `codex/merge-20260816-snapshot`，没有混入本分支。

## 替换边界

快照共发现 124 个文件。123 个文件作为源码、资源、测试或项目文档逐字节复制；仅排除快照内旧的生成成品
`artifacts\SysMonitor.exe`。没有发现凭据、访问令牌、私钥或密码文件。

精确排除规则为路径段 `.git`、`bin`、`obj`、`work`、`outputs`、
`release`、`.vs`、`artifacts`，以及 `.user`、`.suo`、`.tmp`、
`.log`、`desktop.ini`、`Thumbs.db`。嵌入的 PresentMon EXE、图标和图片属于项目输入，因此没有按扩展名排除。

目标仓库原有而快照不存在的源码、文档、图片和旧发行包均已删除。仅保留根目录
`LICENSE`，因为仓库已明确采用 MIT 许可证；Git 元数据当然继续保留。

审计清单：

- [来源快照 SHA-256 清单](manifests/source-snapshot-sha256.tsv)：123 个接收文件。
- [替换后仓库载荷 SHA-256 清单](manifests/repository-replacement-payload-sha256.tsv)：123 个快照路径加保留的 `LICENSE`，并反映下面的独立构建脚本修复。

清单不包含清单自身、本记录、Git 元数据、构建缓存和最终生成成品，避免自引用哈希。

## 最小修复

快照代码在清洁构建目录中无需业务代码修复即可通过。首次验证遇到的
`FpsLabel`/`FpsValue` 编译错误来自工作区内其他分支遗留的 WPF 生成缓存，清理普通和 `win-x64` 的 `obj` 缓存后消失。

为了让以后切换版本、回退或重新打包时不再读取旧 XAML 生成字段，
`Launcher\Build-Portable.ps1` 在发布前增加了对应配置和 RID 的
`dotnet clean`。这是原样替换提交之后唯一的代码差异，并单独提交。

## 验证

- SDK：.NET SDK 8.0.423。
- 测试：242/242 通过，0 失败，0 跳过。
- Release 构建：0 警告，0 错误。
- 打包：`win-x64`、framework-dependent、单文件便携启动器。
- 成品：`artifacts\SysMonitor.exe`，版本 1.5.0.0。
- 大小：8,604,672 字节（约 8.21 MiB）。
- SHA-256：`7ADD4C95C98BD9A544CC82801239354CCF9350BB098FE77EBB936D1D018E5D0E`。
- 构建和检查过程中没有启动 SysMonitor UI，也没有控制前台窗口。

作为来源证据，快照自带旧成品为 8,595,968 字节，SHA-256 为
`21E02726A58D39B9E5419923277BAB695FAB05F4EE059A7C4D241C9A336BF04E`；它没有复制进仓库，最终成品由替换后的源码重新生成。

## 已知安全边界

按“完整替换”的要求，本分支保留快照中的 PresentMon 提权辅助进程和跨进程窗口嵌入实现，没有把旧仓库的 RTSS/MAHM 路径重新合并回来。
静态审查发现该辅助进程会按可执行文件路径清理 PresentMon，且管道协议没有验证连接方就是父进程刚启动的精确辅助 PID。验证过程没有启动或执行这条提权路径。

后续若要发布给更多用户，建议把安全加固作为新的独立提交：使用每次启动随机管道、当前用户限制、精确 PID/会话验证、每实例 Job Object、超时退出，并取消按路径批量结束进程。
