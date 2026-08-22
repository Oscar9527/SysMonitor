namespace SysMonitor.Services;

internal enum HelperProcessKind
{
    None,
    CpuTemperature,
    PresentMon,
}

internal static class HelperProcessDispatcher
{
    internal static HelperProcessKind Classify(IReadOnlyList<string> arguments)
    {
        if (CpuTemperatureHelperHost.TryGetPipeName(arguments, out _))
        {
            return HelperProcessKind.CpuTemperature;
        }

        return PresentMonHelperHost.TryGetRequest(arguments, out _, out _, out _)
            ? HelperProcessKind.PresentMon
            : HelperProcessKind.None;
    }

    internal static Task<int>? TryRunAsync(IReadOnlyList<string> arguments)
    {
        if (CpuTemperatureHelperHost.TryGetPipeName(arguments, out string helperPipeName))
        {
            return RunCpuTemperatureAsync(helperPipeName);
        }

        if (PresentMonHelperHost.TryGetRequest(
                arguments,
                out string presentMonPipeName,
                out int presentMonProcessId,
                out string presentMonSessionName))
        {
            return RunPresentMonAsync(
                presentMonPipeName,
                presentMonProcessId,
                presentMonSessionName);
        }

        return null;
    }

    private static async Task<int> RunCpuTemperatureAsync(string pipeName)
    {
        try
        {
            await CpuTemperatureHelperHost.RunAsync(pipeName).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Parent exit, UAC policy, driver access and pipe teardown are all
            // non-fatal helper outcomes, matching the previous App startup path.
            BandDiagnostics.Log(
                $"CPU temperature helper host failed type={exception.GetType().Name}");
        }

        return 0;
    }

    private static async Task<int> RunPresentMonAsync(
        string pipeName,
        int processId,
        string sessionName)
    {
        try
        {
            await PresentMonHelperHost.RunAsync(pipeName, processId, sessionName)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            BandDiagnostics.Log(
                $"PresentMon helper host failed type={exception.GetType().Name}");
        }

        return 0;
    }
}
