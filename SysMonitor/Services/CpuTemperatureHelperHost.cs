using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace SysMonitor.Services;

/// <summary>
/// Minimal elevated process mode used only when the ordinary process cannot
/// access the CPU temperature driver.
/// </summary>
internal static class CpuTemperatureHelperHost
{
    private const string HelperArgument = "--cpu-temperature-helper";
    private const string PipePrefix = "SysMonitor.CpuTemperature.";

    internal static bool TryGetPipeName(IReadOnlyList<string> arguments, out string pipeName)
    {
        pipeName = string.Empty;
        if (arguments.Count != 2 ||
            !string.Equals(arguments[0], HelperArgument, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string candidate = arguments[1];
        string suffix = candidate.StartsWith(PipePrefix, StringComparison.Ordinal)
            ? candidate[PipePrefix.Length..]
            : string.Empty;
        if (suffix.Length != 32 || !Guid.TryParseExact(suffix, "N", out _))
        {
            return false;
        }

        pipeName = candidate;
        return true;
    }

    internal static async Task RunAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(connectionTimeout.Token).ConfigureAwait(false);

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsMotherboardEnabled = true,
        };
        try
        {
            computer.Open();

            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 128,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (true)
            {
                string payload;
                try
                {
                    double? temperature = CpuTemperatureReader.ReadTemperature(computer);
                    payload = temperature is double value
                        ? value.ToString("R", CultureInfo.InvariantCulture)
                        : "NA";
                }
                catch
                {
                    payload = "NA";
                }

                await writer.WriteLineAsync(payload).ConfigureAwait(false);
                if (!await timer.WaitForNextTickAsync().ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        finally
        {
            try
            {
                computer.Close();
            }
            catch
            {
            }
        }
    }
}
