# 旧版窗口游戏 FPS 兼容性修复

## 现象

窗口模式运行《三国群英传 7》时，HUD 能显示 CPU、GPU 和内存，但 FPS 显示
`-- 未捕获到帧`。

## 根因

只读检查确认目标是 32 位进程，并加载了：

- `DDRAW.dll`
- `D3DIM700.dll`（Direct3D 7）
- `RTSSHooks.dll`

SysMonitor 启动的 PresentMon 2.5.1 使用了正确的目标 PID，但没有收到该旧版
DirectDraw/Direct3D 7 路径的 Present 事件。替换快照中虽然保留了
`AdaptiveFrameRateProvider` 和 `RtssSharedMemoryReader`，生产启动代码却绕过它们，
直接创建 `PresentMonFrameRateProvider`，因此 RTSS 已经采集到的数据也不会被 HUD 使用。

## 修复

生产 FPS 链路现在固定为：

1. 每 250 ms 只读查询已经存在的 `RTSSSharedMemoryV2`。
2. 找到目标 PID 的新鲜样本时，直接显示 RTSS FPS。
3. 连续约 1 秒没有可用 RTSS 样本时，启动 SysMonitor 自己的 PresentMon 回退。
4. PresentMon 运行期间重新获得两个连续 RTSS 样本后，切回 RTSS 并停止回退采集器。

新增工厂和测试把这条生产策略锁定，避免以后再次出现“读取器存在但没有接入”。

## 实机证据和限制

诊断时，RTSS 共享内存中存在目标 PID 的条目，但用户已经切离游戏，条目停止更新并被正确判定为过期。因此本次没有把旧 FPS 当成实时数据，也没有声明后台状态下已经获得有效 FPS。

新版本需要在游戏处于前台并持续渲染时验证。若 RTSS 的目标配置禁用了检测、应用检测级别过低，或 RTSS 本身不更新该游戏条目，HUD 仍会诚实显示 `--`，不会用桌面刷新率、GPU 占用或画面变化推算 FPS。

SysMonitor 的默认访问只有只读共享内存，不会启动、配置或写入 RTSS，也不会向游戏注入代码。用户可在设置中对明确选择的单个旧版游戏开启兼容配置；这只会备份并修改该应用的 RTSS profile，不修改 Global/Config，也不会由 SysMonitor 注入。RTSS 自身的 Hook、反作弊与具体游戏兼容性仍由 RTSS 和游戏决定。

## 验证结果

- 自动化测试：245/245 通过。
- Release 构建：0 警告，0 错误。
- 单文件：`artifacts\SysMonitor.exe`。
- 大小：8,604,672 字节（约 8.21 MiB）。
- SHA-256：`6088470112E589AE1D7267524C2E3FB8B00D230DA6DFC29FD7BA140907F0B76A`。
- 构建和诊断过程中没有切换前台、发送鼠标键盘输入或重启正在运行的软件。
