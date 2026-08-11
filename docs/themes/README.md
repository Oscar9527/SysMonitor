# SysMonitor 主题包

SysMonitor 1.3.0 起支持导入 `.smonitor-theme` 主题包。主题只描述颜色、圆角、阴影和经过校验的图片资源，不加载 DLL、脚本、XAML 或字体，因此主题不能执行代码。

## 导入主题

1. 打开托盘菜单中的“任务栏外观”。
2. 在“主题”区域选择“导入主题”。
3. 选择一个 `.smonitor-theme` 文件。
4. 导入成功后会立即预览；点击“应用”才会将它设为当前主题。

关闭设置窗口会恢复最后一次已经应用的主题，但已导入的主题仍会安全保存在：

```text
%APPDATA%\SysMonitor\Themes\<theme-id>
```

## 包结构

主题包本质上是 ZIP 文件，扩展名必须为 `.smonitor-theme`。只允许以下文件：

```text
manifest.json                         必需
theme.json                            必需
assets/preview.png                    可选
assets/band-background.png            可选
assets/tray-icon.ico                  可选
LICENSE.txt                           可选
README.md                             可选
```

不允许子目录、其他文件、可执行文件、软链接、脚本和重复路径。

### manifest.json

```json
{
  "schemaVersion": 1,
  "id": "ocean-night",
  "name": "Ocean Night",
  "author": "Your Name",
  "version": "1.0.0",
  "minSysMonitorVersion": "1.3.0",
  "preview": "assets/preview.png"
}
```

- `id`：小写字母、数字和连字符，最长 64 个字符；安装后不可与已有主题重复。
- `name`、`author`：非空文本，最长 128 个字符。
- `version`、`minSysMonitorVersion`：标准版本号。
- `preview`：可省略；填写时只能是 `assets/preview.png`。

### theme.json

```json
{
  "colors": {
    "appBackground": "#111214",
    "surface": "#1C1D20",
    "text": "#F5F5F7",
    "secondary": "#B0B0B5",
    "tertiary": "#85858B",
    "separator": "#3A3B40",
    "control": "#292A2E",
    "accent": "#0A84FF"
  },
  "metrics": {
    "cpu": "#0A84FF",
    "memory": "#BF5AF2",
    "gpu": "#30D158",
    "warning": "#FF9F0A",
    "critical": "#FF453A"
  },
  "shape": {
    "groupCornerRadius": 16,
    "shadowOpacity": 0.08
  },
  "band": {
    "backgroundColor": "#CC111214",
    "cornerRadius": 8,
    "textColor": "#F5F5F7",
    "separatorColor": "#66FFFFFF",
    "backgroundImage": "assets/band-background.png"
  },
  "trayIcon": "assets/tray-icon.ico"
}
```

- 颜色只能使用 `#RRGGBB` 或 `#AARRGGBB`。
- `groupCornerRadius`、`band.cornerRadius` 范围为 `0–32`。
- `shadowOpacity` 范围为 `0–1`。
- `textColor`、`separatorColor` 和所有资源路径都可以省略。
- `backgroundImage` 和 `trayIcon` 填写时只能指向上面列出的固定资源路径。

## 图片限制

- 预览图：PNG，最大 `2048 × 2048`，总像素不超过 4,194,304。
- Band 背景：PNG，最大 `4096 × 256`，总像素不超过 1,048,576。
- 托盘图标：ICO，最多 10 帧；每帧不超过 `256 × 256`。

程序会检查文件签名、实际解码结果、尺寸、压缩率和解压后大小。整个包最大 5 MiB，单个文件最大 2 MiB。

## 制作示例

[`docs/themes/example`](example) 提供了一个不含图片的完整示例。在该目录运行：

```powershell
.\Build-Theme.ps1
```

即可在同目录生成 `ocean-night.smonitor-theme`。

## 当前边界

1.3.0 首版只支持本地导入和两个内建主题，不提供在线主题商店、自动下载、任意代码插件或主题覆盖更新。要更新一个已安装主题，请为新包使用新的主题 ID；这能避免导入过程悄悄覆盖现有内容。
