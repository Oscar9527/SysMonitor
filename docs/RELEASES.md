# 版本留存与回退

SysMonitor 从 v1.3.0 开始为每个完成验证的正式版本保留 Git 提交和带说明的版本标签。标签不会在验证后移动，因此可以稳定地查看、构建或回退到任意已发布版本。

## 发布顺序

1. 在独立功能分支完成开发和自动测试。
2. 提交候选版本，确认工作区干净。
3. 从该提交重新构建单文件，并验证版本号、SHA-256、CPU 占用、进程唯一性和任务栏窗口稳定性。
4. 验证通过后创建与版本号相同的 annotated tag，例如 `v1.4.0`。
5. 记录提交 SHA、单文件 SHA-256 和上一个可回退标签。

验证失败时必须新增修复提交并重新验证，不能移动已经创建的正式版本标签。

## 查看历史版本

```powershell
git tag --list "v*"
git show v1.3.0
```

## 临时查看旧版代码

```powershell
git switch --detach v1.3.0
```

查看完成后返回当前开发分支：

```powershell
git switch codex/metric-history-charts
```

## 从旧版建立修复分支

```powershell
git switch -c codex/hotfix-1.3 v1.3.0
```

这种方式不会删除新版本代码，也不会改动原有标签。
