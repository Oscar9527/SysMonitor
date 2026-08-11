using System.Diagnostics;
using System.IO;
using System.Text;

namespace SysMonitor.Services;

internal static class PresentMonProcessSupport
{
    internal const int MaximumDiagnosticCharacters = 64 * 1024;

    internal static ProcessStartInfo CreateCollectorStartInfo(
        string executablePath,
        int processId,
        string sessionName)
    {
        var startInfo = CreateBaseStartInfo(executablePath);
        Add(startInfo,
            "--process_id", processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--session_name", sessionName,
            "--output_stdout",
            "--v1_metrics",
            "--no_track_gpu",
            "--no_track_input",
            "--no_track_display",
            "--terminate_on_proc_exit");
        return startInfo;
    }

    internal static ProcessStartInfo CreateTerminateStartInfo(
        string executablePath,
        string sessionName)
    {
        var startInfo = CreateBaseStartInfo(executablePath);
        Add(startInfo,
            "--session_name", sessionName,
            "--terminate_existing_session");
        return startInfo;
    }

    internal static async Task<string> CaptureBoundedAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(MaximumDiagnosticCharacters, 4096));
        var buffer = new char[1024];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            int remaining = MaximumDiagnosticCharacters - result.Length;
            if (remaining > 0)
            {
                result.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return result.ToString();
    }

    private static ProcessStartInfo CreateBaseStartInfo(string executablePath) => new()
    {
        FileName = executablePath,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };

    private static void Add(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }
}

internal sealed class PresentMonBoundedLineReader
{
    private readonly TextReader _reader;
    private readonly char[] _buffer = new char[1024];
    private int _bufferIndex;
    private int _bufferLength;

    internal PresentMonBoundedLineReader(TextReader reader) => _reader = reader;

    internal async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder(256);
        while (true)
        {
            if (_bufferIndex >= _bufferLength)
            {
                _bufferLength = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _bufferIndex = 0;
                if (_bufferLength == 0)
                {
                    return line.Length == 0 ? null : line.ToString();
                }
            }

            int newline = Array.IndexOf(_buffer, '\n', _bufferIndex, _bufferLength - _bufferIndex);
            int end = newline >= 0 ? newline : _bufferLength;
            int count = end - _bufferIndex;
            if (line.Length + count > PresentMonCsvParser.MaximumLineLength + 1)
            {
                throw new InvalidDataException("PresentMon emitted an overlong CSV row.");
            }

            line.Append(_buffer, _bufferIndex, count);
            _bufferIndex = newline >= 0 ? newline + 1 : _bufferLength;
            if (newline >= 0)
            {
                if (line.Length > 0 && line[^1] == '\r')
                {
                    line.Length--;
                }

                if (line.Length > PresentMonCsvParser.MaximumLineLength)
                {
                    throw new InvalidDataException("PresentMon emitted an overlong CSV row.");
                }

                return line.ToString();
            }
        }
    }
}
