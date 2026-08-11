using System.Collections.Immutable;
using System.IO;
using SysMonitor.Models;

namespace SysMonitor.Services;

internal readonly record struct DriveTelemetryObservation(
    string Name,
    bool IsFixed,
    bool IsReady,
    bool PropertiesReadSuccessfully,
    string VolumeLabel,
    long TotalBytes,
    long FreeBytes);

internal sealed class DriveTelemetryCache
{
    private const int FailedReadGraceCycles = 2;
    private readonly string _systemDriveName;
    private readonly Dictionary<string, CachedDrive> _cached =
        new(StringComparer.OrdinalIgnoreCase);

    internal DriveTelemetryCache(string? systemDriveRoot)
    {
        _systemDriveName = NormalizeDriveName(systemDriveRoot) ?? "C:";
    }

    internal ImmutableArray<DriveSnapshot> Current { get; private set; } =
        ImmutableArray<DriveSnapshot>.Empty;

    internal string SystemDriveName => _systemDriveName;

    internal void ApplyGlobalFailure()
    {
        // A failed enumeration says nothing about drives disappearing. Keep the last
        // coherent sample and try again on the next scheduled disk cycle.
    }

    internal void ApplySuccessfulEnumeration(IEnumerable<DriveTelemetryObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var observedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DriveTelemetryObservation observation in observations)
        {
            string? name = NormalizeDriveName(observation.Name);
            if (name is null || !observedNames.Add(name))
            {
                continue;
            }

            if (!observation.IsFixed || !observation.IsReady ||
                (observation.PropertiesReadSuccessfully && observation.TotalBytes <= 0))
            {
                _cached.Remove(name);
                continue;
            }

            if (!observation.PropertiesReadSuccessfully)
            {
                if (_cached.TryGetValue(name, out CachedDrive? previous))
                {
                    previous.FailedReadCycles++;
                    if (previous.FailedReadCycles > FailedReadGraceCycles)
                    {
                        _cached.Remove(name);
                    }
                }

                continue;
            }

            long total = observation.TotalBytes;
            long free = Math.Clamp(observation.FreeBytes, 0, total);
            long used = Math.Max(0, total - free);
            double usage = total > 0
                ? Math.Clamp(used * 100d / total, 0d, 100d)
                : 0d;
            var snapshot = new DriveSnapshot(
                name,
                observation.VolumeLabel?.Trim() ?? string.Empty,
                used,
                total,
                usage,
                string.Equals(name, _systemDriveName, StringComparison.OrdinalIgnoreCase));
            _cached[name] = new CachedDrive(snapshot);
        }

        foreach (string cachedName in _cached.Keys.ToArray())
        {
            if (!observedNames.Contains(cachedName))
            {
                _cached.Remove(cachedName);
            }
        }

        Current = _cached.Values
            .Select(item => item.Snapshot)
            .OrderByDescending(item => item.IsSystemDrive)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    internal static string? NormalizeDriveName(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        string value = root.Trim().Replace('/', '\\');
        string? pathRoot;
        try
        {
            pathRoot = Path.GetPathRoot(value);
        }
        catch
        {
            pathRoot = null;
        }

        value = string.IsNullOrWhiteSpace(pathRoot) ? value : pathRoot;
        value = value.TrimEnd('\\');
        if (value.Length == 1 && char.IsLetter(value[0]))
        {
            value += ":";
        }

        return value.Length == 0 ? null : value.ToUpperInvariant();
    }

    private sealed class CachedDrive
    {
        internal CachedDrive(DriveSnapshot snapshot) => Snapshot = snapshot;

        internal DriveSnapshot Snapshot { get; }
        internal int FailedReadCycles { get; set; }
    }
}
