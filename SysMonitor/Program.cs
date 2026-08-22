using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SysMonitor.Services;

namespace SysMonitor;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        EnvironmentBootstrap.EnsureWindowsDirectoryEnvironment();

        Task<int>? helperRun = HelperProcessDispatcher.TryRunAsync(args);
        return helperRun is not null
            ? await helperRun.ConfigureAwait(false)
            : RunWpfApplicationOnStaThread();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWpfApplicationOnStaThread()
    {
        int exitCode = 1;
        ExceptionDispatchInfo? failure = null;
        var uiThread = new Thread(() =>
        {
            try
            {
                exitCode = RunWpfApplication();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        })
        {
            IsBackground = false,
            Name = "SysMonitor UI",
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        uiThread.Join();
        failure?.Throw();
        return exitCode;
    }

    // Keep every WPF type reference outside the helper-path JIT body. The
    // elevated sensor and PresentMon bridges can then use this same executable
    // without paying for a second WPF/WinForms application instance.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWpfApplication()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
