using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;
using System.Windows.Forms;

[assembly: AssemblyTitle("SysMonitor")]
[assembly: AssemblyProduct("SysMonitor")]
[assembly: AssemblyDescription("Portable Windows taskbar system monitor")]

internal static class SysMonitorLauncher
{
    private const string CoreResourceName = "SysMonitor.Core.exe";
    private const string RuntimeDownloadUrl =
        "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";
    private const string LauncherMutexName = @"Local\SysMonitor.Launcher";
    private const string ShowPanelEventName = @"Local\SysMonitor.ShowPanel";
    private const string ExitForUpdateEventName = @"Local\SysMonitor.ExitForUpdate";

    [STAThread]
    private static int Main(string[] args)
    {
        using var launcherMutex = new Mutex(false, LauncherMutexName);
        bool ownsMutex;
        try
        {
            ownsMutex = launcherMutex.WaitOne(TimeSpan.FromSeconds(12));
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            return 0;
        }

        try
        {
            return Run(args);
        }
        finally
        {
            launcherMutex.ReleaseMutex();
        }
    }

    private static int Run(string[] args)
    {
        string runtimeDirectory = GetRuntimeDirectory();
        if (!TryStopExistingRuntimeProcesses(
                runtimeDirectory,
                out bool existingInstanceActivated,
                out string? stopError))
        {
            MessageBox.Show(
                stopError ?? "无法关闭正在运行的旧版 SysMonitor，请从托盘退出后重试。",
                "SysMonitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return 1;
        }

        if (existingInstanceActivated)
        {
            return 0;
        }

        if (!TryExtractCore(runtimeDirectory, out string corePath, out string? extractionError))
        {
            MessageBox.Show(
                extractionError ?? "无法准备 SysMonitor 运行文件。",
                "SysMonitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        if (!HasRequiredWindowsDesktopRuntime())
        {
            DialogResult download = MessageBox.Show(
                "这是轻量版，需要 Microsoft .NET 8 Desktop Runtime（x64）才能运行。\n\n" +
                "点击“是”将打开微软官方安装程序下载。安装完成后，请再次运行 SysMonitor。\n\n" +
                "如果不想安装 .NET，请点击“否”，改用名称带 Standalone 的独立版；独立版已内置运行环境。",
                "SysMonitor Light 需要 .NET 8",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1);
            if (download == DialogResult.Yes)
            {
                OpenRuntimeDownloadPage();
            }

            return 2;
        }

        try
        {
            string launcherPath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            var startInfo = new ProcessStartInfo
            {
                FileName = corePath,
                WorkingDirectory = Path.GetDirectoryName(corePath) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
            };
            var launchArguments = args.ToList();
            if (!string.IsNullOrWhiteSpace(launcherPath))
            {
                launchArguments.Insert(0, "--launcher-path=" + launcherPath);
            }

            startInfo.Arguments = string.Join(" ", launchArguments.Select(QuoteArgument));

            Process.Start(startInfo);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法启动 SysMonitor：{exception.Message}",
                "SysMonitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string GetRuntimeDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysMonitor",
            "runtime");

    private static bool TryStopExistingRuntimeProcesses(
        string runtimeDirectory,
        out bool existingInstanceActivated,
        out string? error)
    {
        existingInstanceActivated = false;
        error = null;
        string normalizedRuntimeDirectory = Path.GetFullPath(runtimeDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int currentProcessId = Process.GetCurrentProcess().Id;

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == currentProcessId)
                {
                    continue;
                }

                string? processPath;
                try
                {
                    processPath = process.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(processPath))
                {
                    continue;
                }

                string fileName = Path.GetFileName(processPath);
                string? processDirectory = Path.GetDirectoryName(Path.GetFullPath(processPath));
                if (processDirectory is null ||
                    !string.Equals(
                        processDirectory.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar),
                        normalizedRuntimeDirectory,
                        StringComparison.OrdinalIgnoreCase) ||
                    !fileName.StartsWith("SysMonitor.Core.", StringComparison.OrdinalIgnoreCase) ||
                    !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (CoreMatchesEmbeddedResource(processPath!))
                {
                    if (!TrySignalControlEvent(ShowPanelEventName, TimeSpan.FromSeconds(8)))
                    {
                        error = "SysMonitor 正在启动，请稍候片刻后再试。";
                        return false;
                    }

                    existingInstanceActivated = true;
                    return true;
                }

                try
                {
                    RequestExistingCoreExit();
                    if (!process.WaitForExit(5000))
                    {
                        error = $"旧版 SysMonitor 仍在运行（PID {process.Id}）。";
                        return false;
                    }
                }
                catch (Exception exception)
                {
                    error = $"无法关闭旧版 SysMonitor：{exception.Message}";
                    return false;
                }
            }
        }

        return true;
    }

    private static void RequestExistingCoreExit()
    {
        if (!TrySignalControlEvent(ExitForUpdateEventName, TimeSpan.FromSeconds(8)))
        {
            throw new InvalidOperationException(
                "SysMonitor 正在启动或版本过旧，无法安全退出。请先从托盘菜单退出后重试。");
        }
    }

    private static bool TrySignalControlEvent(string eventName, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            try
            {
                using EventWaitHandle controlEvent = EventWaitHandle.OpenExisting(eventName);
                return controlEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            Thread.Sleep(100);
        }
        while (stopwatch.Elapsed < timeout);

        return false;
    }

    private static bool TryExtractCore(
        string directory,
        out string corePath,
        out string? error)
    {
        corePath = Path.Combine(directory, GetVersionedCoreFileName());
        error = null;

        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(corePath) && CoreMatchesEmbeddedResource(corePath))
            {
                return true;
            }

            using Stream? source = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(CoreResourceName);
            if (source is null)
            {
                error = "找不到内置的 SysMonitor 核心文件。";
                return false;
            }

            string temporaryPath = corePath + $".new.{Process.GetCurrentProcess().Id}";
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            using (FileStream destination = File.Create(temporaryPath))
            {
                source.CopyTo(destination);
            }

            if (File.Exists(corePath))
            {
                File.Delete(corePath);
            }

            File.Move(temporaryPath, corePath);
            return true;
        }
        catch (Exception exception)
        {
            error = $"无法准备核心文件：{exception.Message}";
            return false;
        }
    }

    private static string GetVersionedCoreFileName()
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        string productVersion = version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{version.Build}";
        return $"SysMonitor.Core.{productVersion}.exe";
    }

    private static bool CoreMatchesEmbeddedResource(string corePath)
    {
        try
        {
            using Stream? embedded = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(CoreResourceName);
            if (embedded is null)
            {
                return false;
            }

            using FileStream existing = File.OpenRead(corePath);
            if (existing.Length != embedded.Length)
            {
                return false;
            }

            using SHA256 algorithm = SHA256.Create();
            byte[] embeddedHash = algorithm.ComputeHash(embedded);
            byte[] existingHash = algorithm.ComputeHash(existing);
            return embeddedHash.SequenceEqual(existingHash);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasRequiredWindowsDesktopRuntime()
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using RegistryKey? runtimeKey = baseKey.OpenSubKey(
                @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
            if (runtimeKey is not null && runtimeKey.GetSubKeyNames().Any(name =>
                    Version.TryParse(name, out Version? version) && version.Major == 8))
            {
                return true;
            }
        }
        catch
        {
        }

        try
        {
            string? configuredRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT_X64");
            string[] roots = string.IsNullOrWhiteSpace(configuredRoot)
                ? new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
                    @"C:\Program Files\dotnet",
                }
                : new[]
                {
                    configuredRoot,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
                    @"C:\Program Files\dotnet",
                };

            return roots.Any(dotnetRoot =>
            {
                string runtimeDirectory = Path.Combine(
                    dotnetRoot,
                    "shared",
                    "Microsoft.WindowsDesktop.App");
                return Directory.Exists(runtimeDirectory) &&
                    Directory.GetDirectories(runtimeDirectory).Any(directory =>
                        Version.TryParse(Path.GetFileName(directory), out Version? version) &&
                        version.Major == 8);
            });
        }
        catch
        {
            return false;
        }
    }

    private static void OpenRuntimeDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = RuntimeDownloadUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // The message box still explains the required runtime if a browser
            // association is unavailable.
        }
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        return argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0
            ? argument
            : "\"" + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
