using System.Globalization;

namespace SysMonitor.Services;

internal readonly record struct PresentMonFrame(
    int ProcessId,
    ulong SwapChainAddress,
    double TimeInSeconds,
    double MillisecondsBetweenPresents,
    string Application);

internal static class PresentMonCsvParser
{
    internal const int MaximumLineLength = 4096;
    internal const string ExpectedHeader =
        "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped," +
        "TimeInSeconds,msInPresentAPI,msBetweenPresents";

    internal static bool IsExpectedHeader(string line) =>
        string.Equals(line, ExpectedHeader, StringComparison.Ordinal);

    internal static bool TryParseFrame(
        string line,
        int targetProcessId,
        out PresentMonFrame frame)
    {
        frame = default;
        if (targetProcessId <= 0 || line.Length == 0 || line.Length > MaximumLineLength)
        {
            return false;
        }

        string anchor = $",{targetProcessId.ToString(CultureInfo.InvariantCulture)},0x";
        int anchorIndex = line.LastIndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex <= 0)
        {
            return false;
        }

        string application = line[..anchorIndex];
        string[] fields = line[(anchorIndex + 1)..].Split(',');
        if (fields.Length != 9 ||
            !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int processId) ||
            processId != targetProcessId ||
            !TryParseHexAddress(fields[1], out ulong swapChainAddress) ||
            !IsExpectedRuntime(fields[2]) ||
            !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            !IsExpectedDropped(fields[5]) ||
            !TryNonNegativeFinite(fields[6], out double timeInSeconds) ||
            !TryNonNegativeFinite(fields[7], out _) ||
            !TryNonNegativeFinite(fields[8], out double millisecondsBetweenPresents))
        {
            return false;
        }

        frame = new PresentMonFrame(
            processId,
            swapChainAddress,
            timeInSeconds,
            millisecondsBetweenPresents,
            application);
        return true;
    }

    private static bool TryParseHexAddress(string value, out ulong address)
    {
        address = 0;
        return value.Length > 2 &&
               value.StartsWith("0x", StringComparison.Ordinal) &&
               ulong.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
    }

    private static bool IsExpectedRuntime(string value) =>
        value is "DXGI" or "D3D9" or "Other";

    private static bool IsExpectedDropped(string value) =>
        value is "0" or "1" or "NA";

    private static bool TryNonNegativeFinite(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
        double.IsFinite(result) &&
        result >= 0d;
}
