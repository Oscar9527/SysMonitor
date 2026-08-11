using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;
using System.Windows.Forms;

[assembly: AssemblyTitle("SysMonitor")]
[assembly: AssemblyProduct("SysMonitor")]
[assembly: AssemblyDescription("Portable Windows taskbar system monitor")]
[assembly: AssemblyVersion("1.2.15.0")]
[assembly: AssemblyFileVersion("1.2.15.0")]
[assembly: AssemblyInformationalVersion("1.2.15")]

internal static class SysMonitorLauncher
{
    private const string CoreResourceName = "SysMonitor.Core.1.2.15.exe";
    private const string RuntimeDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/8.0/runtime";

    [STAThread]
    private static int Main(string[] args)
    {
        string runtimeDirectory = GetRuntimeDirectory();
        if (!TryStopExistingRuntimeProcesses(runtimeDirectory, out string? stopError))
        {
            MessageBox.Show(
                stopError ?? "无法关闭正在运行的旧版 SysMonitor，请从托盘退出后重试。",
                "SysMonitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return 1;
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

        if (!HasWindowsDesktopRuntime8())
        {
            OpenRuntimeDownloadPage();
            MessageBox.Show(
                "这台电脑缺少 Microsoft .NET 8 Desktop Runtime（x64）。\n\n已打开官方下载页面，安装后再次运行 SysMonitor。",
                "需要安装 .NET 运行时",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
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
        out string? error)
    {
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

                try
                {
                    KillProcessTree(process.Id);
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

    private static void KillProcessTree(int processId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "taskkill.exe",
            Arguments = $"/PID {processId} /T /F",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process? taskkill = Process.Start(startInfo);
        if (taskkill is null)
        {
            throw new InvalidOperationException("无法启动旧版进程清理程序。");
        }

        _ = taskkill.StandardOutput.ReadToEnd();
        _ = taskkill.StandardError.ReadToEnd();
        bool completed = taskkill.WaitForExit(5000);
        if (!completed || (taskkill.ExitCode != 0 && IsProcessRunning(processId)))
        {
            throw new InvalidOperationException("无法完整关闭旧版 SysMonitor 进程树。");
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryExtractCore(
        string directory,
        out string corePath,
        out string? error)
    {
        corePath = Path.Combine(directory, CoreResourceName);
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

    private static bool HasWindowsDesktopRuntime8()
    {
        try
        {
            var listInfo = new ProcessStartInfo
            {
                FileName = "dotnet.exe",
                Arguments = "--list-runtimes",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process? listProcess = Process.Start(listInfo);
            if (listProcess is not null)
            {
                string output = listProcess.StandardOutput.ReadToEnd();
                listProcess.WaitForExit(5000);
                if (output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => line.StartsWith("Microsoft.WindowsDesktop.App 8.", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using RegistryKey? runtimeKey = baseKey.OpenSubKey(
                @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
            if (runtimeKey is null)
            {
                return false;
            }

            if (runtimeKey.GetSubKeyNames().Any(name =>
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
            string? configuredRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            string[] roots = string.IsNullOrWhiteSpace(configuredRoot)
                ? new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
                    @"C:\Program Files\dotnet",
                    @"C:\Program Files (x86)\dotnet",
                }
                : new[] { configuredRoot };

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
