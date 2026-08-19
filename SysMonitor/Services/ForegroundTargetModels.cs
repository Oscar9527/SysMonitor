using System.IO;

namespace SysMonitor.Services;

public enum ForegroundTargetState
{
    Idle,
    WaitingForTarget,
    Ready
}

public sealed record ForegroundTarget(
    nint WindowHandle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset QualifiedAt,
    string? ExecutablePath = null)
{
    public bool SameIdentity(ForegroundTarget? other) =>
        other is not null &&
        WindowHandle == other.WindowHandle &&
        ProcessId == other.ProcessId &&
        ProcessStartedAt == other.ProcessStartedAt;
}

public sealed record GameOverlayTargetOption(
    nint WindowHandle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    string ProcessName,
    string WindowTitle,
    string? ExecutablePath = null);

public sealed record ForegroundWindowCandidate(
    nint WindowHandle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    string ProcessName,
    string WindowClass,
    bool IsWindow,
    bool IsVisible,
    bool HasExited,
    string WindowTitle = "",
    string? ExecutablePath = null);

public interface IForegroundWindowSource
{
    ForegroundWindowCandidate? Capture();
    bool IsCurrentIdentity(ForegroundTarget target);
}

public static class ForegroundTargetPolicy
{
    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // System & Shell
        "explorer",
        "dwm",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "LockApp",
        "TextInputHost",
        "ApplicationFrameHost",
        "SystemSettings",
        "Taskmgr",
        "cmd",
        "powershell",
        "pwsh",
        "conhost",
        "WindowsTerminal",
        "Antigravity",
        "SysMonitor",

        // Web Browsers (never track as game targets)
        "chrome",
        "msedge",
        "firefox",
        "opera",
        "opera_gx",
        "brave",
        "qqbrowser",
        "360chrome",
        "360se",
        "sogouexplorer",
        "liebao",
        "maxthon",
        "vivaldi",
        "arc",
        "waterfox",
        "torbrowser",
        "browser",

        // IDEs & Dev Tools
        "Code",
        "devenv",
        "idea64",
        "cursor",
        "pycharm64",
        "webstorm64",
        "rider64",
        "studio64",
        "git-bash",

        // Office, Text & PDF
        "notepad",
        "notepad++",
        "wordpad",
        "WINWORD",
        "EXCEL",
        "POWERPNT",
        "wps",
        "wpp",
        "et",
        "AcroRd32",
        "Acrobat",
        "FoxitPDFEditor",
        "FoxitReader",

        // Chat & Communication
        "WeChat",
        "WeChatAppEx",
        "Weixin",
        "QQ",
        "DingTalk",
        "Feishu",
        "Lark",
        "Slack",
        "Teams",
        "Telegram",
        "Discord",

        // Media & Utilities
        "vlc",
        "potplayer",
        "potplayermini64",
        "mpc-hc",
        "mpc-hc64",
        "foobar2000",
        "cloudmusic",
        "QQMusic",
        "Kugou",
        "Spotify",
        "Everything",
        "Bandizip",
        "WinRAR",
        "7zFM",
        "7zG",
        "Snipaste",
        "PixPin"
    };

    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
        "DV2ControlHost",
        "Windows.UI.Core.CoreWindow"
    };

    public static bool IsQualified(ForegroundWindowCandidate? candidate, int currentProcessId)
    {
        if (candidate is null ||
            candidate.WindowHandle == nint.Zero ||
            candidate.ProcessId <= 0 ||
            candidate.ProcessId == currentProcessId ||
            !candidate.IsWindow ||
            !candidate.IsVisible ||
            candidate.HasExited ||
            candidate.ProcessStartedAt == default)
        {
            return false;
        }

        string processName = Path.GetFileNameWithoutExtension(candidate.ProcessName ?? string.Empty);
        return !ExcludedProcesses.Contains(processName) &&
            !ExcludedClasses.Contains(candidate.WindowClass ?? string.Empty);
    }
}
