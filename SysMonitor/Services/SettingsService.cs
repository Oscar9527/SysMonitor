using System.IO;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using SysMonitor.Models;

namespace SysMonitor.Services;

public sealed class SettingsService
{
    private const string RevisionPropertyName = "__SettingsRevision";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private static readonly ConcurrentDictionary<string, object> SharedPathGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sharedPathGate;
    private bool _loaded;
    private long _revision;
    private AppSettings _confirmed = new();
    private AppSettings _working = new();
    private AppSettings _candidate = new();

    public SettingsService(string? settingsDirectory = null)
    {
        SettingsDirectory = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysMonitor");
        SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
        _sharedPathGate = SharedPathGates.GetOrAdd(
            Path.GetFullPath(SettingsPath),
            static _ => new object());
    }

    public string SettingsDirectory { get; }

    public string SettingsPath { get; }

    public string SettingsBackupPath => SettingsPath + ".bak";

    /// <summary>Current serialized revision. Reads are synchronized with writes.</summary>
    public long Revision
    {
        get
        {
            lock (_sharedPathGate)
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _revision;
            }
        }
    }

    /// <summary>A detached confirmed snapshot. Mutating it never mutates service state.</summary>
    public AppSettings Confirmed => GetConfirmedSnapshot();

    /// <summary>A detached working snapshot. Mutating it never mutates service state.</summary>
    public AppSettings Working => GetWorkingSnapshot();

    /// <summary>A detached candidate snapshot. Mutating it never mutates service state.</summary>
    public AppSettings Candidate => GetCandidateSnapshot();

    public SettingsSnapshot Snapshot
    {
        get
        {
            lock (_sharedPathGate)
            lock (_gate)
            {
                EnsureLoadedLocked();
                return new SettingsSnapshot(_confirmed, _revision);
            }
        }
    }

    public SettingsSnapshot GetSnapshot() => Snapshot;

    public AppSettings Load()
    {
        lock (_sharedPathGate)
        lock (_gate)
        {
            LoadFromDiskLocked(preserveRevision: false);
            return Clone(_confirmed);
        }
    }

    [Obsolete("Use TryPatch or TrySave with an expected revision for conflict detection.")]
    public void Save(AppSettings settings)
    {
        _ = TrySave(settings);
    }

    [Obsolete("Use TryPatch or TrySave with an expected revision for conflict detection.")]
    public bool TrySave(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sharedPathGate)
        lock (_gate)
        {
            EnsureLoadedLocked();
            Normalize(settings);
            return TryCommitLocked(Clone(settings), expectedRevision: null);
        }
    }

    public bool TrySave(AppSettings settings, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sharedPathGate)
        lock (_gate)
        {
            EnsureLoadedLocked();
            RefreshExternalStateLocked();
            Normalize(settings);
            return TryCommitLocked(Clone(settings), expectedRevision);
        }
    }

    public AppSettings GetConfirmedSnapshot()
    {
        lock (_sharedPathGate)
        lock (_gate)
        {
            EnsureLoadedLocked();
            return Clone(_confirmed);
        }
    }

    public AppSettings GetWorkingSnapshot()
    {
        lock (_sharedPathGate)
        lock (_gate)
        {
            EnsureLoadedLocked();
            return Clone(_working);
        }
    }

    public AppSettings GetCandidateSnapshot()
    {
        lock (_sharedPathGate)
        lock (_gate)
        {
            EnsureLoadedLocked();
            return Clone(_candidate);
        }
    }

    public SettingsSnapshot Patch(Action<AppSettings> patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        lock (_sharedPathGate)
        lock (_gate)
        {
            EnsureLoadedLocked();
            SettingsSnapshot snapshot = PatchLocked(patch, null, out bool committed);
            if (!committed)
            {
                throw new IOException("Settings could not be committed.");
            }

            return snapshot;
        }
    }

    public bool TryPatch(Action<AppSettings> patch, out SettingsSnapshot snapshot)
    {
        return TryPatch((long?)null, patch, out snapshot);
    }

    public bool TryPatch(Action<AppSettings> patch)
    {
        return TryPatch(patch, out _);
    }

    public bool TryPatch(long expectedRevision, Action<AppSettings> patch, out SettingsSnapshot snapshot)
    {
        return TryPatch((long?)expectedRevision, patch, out snapshot);
    }

    public bool TryPatch(long expectedRevision, Action<AppSettings> patch)
    {
        return TryPatch(expectedRevision, patch, out _);
    }

    public bool TryPatch(Action<AppSettings> patch, long expectedRevision, out SettingsSnapshot snapshot)
    {
        return TryPatch((long?)expectedRevision, patch, out snapshot);
    }

    private bool TryPatch(long? expectedRevision, Action<AppSettings> patch, out SettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(patch);
        lock (_sharedPathGate)
        lock (_gate)
        {
            EnsureLoadedLocked();
            snapshot = PatchLocked(patch, expectedRevision, out bool committed);
            return committed;
        }
    }

    private SettingsSnapshot PatchLocked(
        Action<AppSettings> patch,
        long? expectedRevision,
        out bool committed)
    {
        // A second service instance may have committed since this instance
        // loaded. Refresh the detached state before checking the revision.
        RefreshExternalStateLocked();
        if (expectedRevision is long expected && expected != _revision)
        {
            committed = false;
            return new SettingsSnapshot(_confirmed, _revision);
        }

        AppSettings working = Clone(_confirmed);
        AppSettings callbackCandidate = Clone(working);
        try
        {
            patch(callbackCandidate);
            Normalize(callbackCandidate);
        }
        catch
        {
            committed = false;
            return new SettingsSnapshot(_confirmed, _revision);
        }

        // Keep references handed to callbacks detached from all service-owned
        // snapshots, including after the callback returns and mutates them.
        _working = Clone(working);
        _candidate = Clone(callbackCandidate);
        committed = TryCommitLocked(callbackCandidate, expectedRevision);
        return new SettingsSnapshot(_confirmed, _revision);
    }

    private bool TryCommitLocked(AppSettings candidate, long? expectedRevision)
    {
        if (expectedRevision is long expected && expected != _revision)
        {
            return false;
        }

        AppSettings normalized = Clone(candidate);
        Normalize(normalized);
        long nextRevision = checked(_revision + 1);
        if (!TryPersistLocked(normalized, nextRevision))
        {
            // Confirmed remains untouched on serialization or I/O failure.
            return false;
        }

        _confirmed = Clone(normalized);
        _working = Clone(normalized);
        _candidate = Clone(normalized);
        _revision = nextRevision;
        _loaded = true;
        return true;
    }

    private void EnsureLoadedLocked()
    {
        if (!_loaded)
        {
            LoadFromDiskLocked(preserveRevision: false);
        }
    }

    private void LoadFromDiskLocked(bool preserveRevision)
    {
        (AppSettings settings, long revision) = ReadSettingsWithBackup();
        Normalize(settings);
        _confirmed = Clone(settings);
        _working = Clone(settings);
        _candidate = Clone(settings);
        if (!preserveRevision)
        {
            _revision = Math.Max(0, revision);
        }

        _loaded = true;
    }

    private void RefreshExternalStateLocked()
    {
        if (!TryReadPersistedRevision(SettingsPath, out long diskRevision))
        {
            return;
        }

        if (diskRevision != _revision)
        {
            LoadFromDiskLocked(preserveRevision: false);
        }
    }

    private (AppSettings Settings, long Revision) ReadSettingsWithBackup()
    {
        if (TryReadSettingsFile(SettingsPath, out AppSettings? settings, out long revision))
        {
            return (settings!, revision);
        }

        if (TryReadSettingsFile(BackupPath, out settings, out revision))
        {
            return (settings!, revision);
        }

        // A missing or damaged main and backup file both recover to the safe
        // defaults. In particular, GameSafeMode's initializer remains true.
        return (new AppSettings(), 0);
    }

    private string BackupPath => SettingsBackupPath;

    private bool TryPersistLocked(AppSettings settings, long revision)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            string json = SerializeForDisk(settings, revision);
            temporaryPath = CreateFlushedTemporaryFile(json);

            bool mainValid = TryReadSettingsFile(SettingsPath, out _, out _);
            if (mainValid)
            {
                File.Replace(temporaryPath, SettingsPath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                // Preserve the last confirmed state as the recovery copy when
                // the main file is missing or corrupt; never use that corrupt
                // file as File.Replace's backup source.
                WriteBackupSnapshotLocked();
                if (File.Exists(SettingsPath))
                {
                    File.Replace(temporaryPath, SettingsPath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, SettingsPath);
                }
            }

            temporaryPath = null;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    private void WriteBackupSnapshotLocked()
    {
        string? temporaryPath = null;
        try
        {
            temporaryPath = CreateFlushedTemporaryFile(
                SerializeForDisk(_confirmed, _revision));
            if (File.Exists(BackupPath))
            {
                File.Replace(temporaryPath, BackupPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, BackupPath);
            }

            temporaryPath = null;
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    private string CreateFlushedTemporaryFile(string json)
    {
        string path = Path.Combine(
            SettingsDirectory,
            $".settings.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
            return path;
        }
        catch
        {
            DeleteTemporaryFile(path);
            throw;
        }
    }

    private static void DeleteTemporaryFile(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static string SerializeForDisk(AppSettings settings, long revision)
    {
        JsonObject root = JsonSerializer.SerializeToNode(settings, JsonOptions)?.AsObject()
            ?? new JsonObject();
        root[RevisionPropertyName] = revision;
        return root.ToJsonString(JsonOptions);
    }

    private static bool TryReadSettingsFile(
        string path,
        out AppSettings? settings,
        out long revision)
    {
        settings = null;
        revision = 0;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            string json = File.ReadAllText(path);
            using JsonDocument document = JsonDocument.Parse(json);
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                return false;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(RevisionPropertyName, out JsonElement revisionElement) &&
                revisionElement.TryGetInt64(out long persistedRevision))
            {
                revision = Math.Max(0, persistedRevision);
            }

            return true;
        }
        catch
        {
            settings = null;
            revision = 0;
            return false;
        }
    }

    private static bool TryReadPersistedRevision(string path, out long revision)
    {
        revision = 0;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(RevisionPropertyName, out JsonElement value) ||
                !value.TryGetInt64(out long parsed))
            {
                return false;
            }

            revision = Math.Max(0, parsed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AppSettings Clone(AppSettings source)
    {
        string json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    internal static void Normalize(AppSettings settings)
    {
        settings.UiCulture = LocalizationService.NormalizeCulturePreference(settings.UiCulture);
        settings.ActiveThemeId = string.IsNullOrWhiteSpace(settings.ActiveThemeId)
            ? AppSettings.DefaultThemeId
            : settings.ActiveThemeId.Trim();
        settings.BandFontFamily = string.IsNullOrWhiteSpace(settings.BandFontFamily)
            ? "Segoe UI Variable Text"
            : settings.BandFontFamily.Trim();
        settings.BandFontSize = double.IsFinite(settings.BandFontSize)
            ? Math.Clamp(Math.Round(settings.BandFontSize), 9, 20)
            : 13;
        settings.BandItemSpacingDip = double.IsFinite(settings.BandItemSpacingDip)
            ? Math.Clamp(
                Math.Round(settings.BandItemSpacingDip, MidpointRounding.AwayFromZero),
                0,
                18)
            : 10;
        settings.BandHorizontalOffsetDip = double.IsFinite(settings.BandHorizontalOffsetDip)
            ? Math.Clamp(
                Math.Round(settings.BandHorizontalOffsetDip, MidpointRounding.AwayFromZero),
                -100,
                100)
            : 0;
        settings.BandHorizontalPositionPercent =
            settings.BandHorizontalPositionPercent is double position &&
            double.IsFinite(position)
                ? Math.Clamp(
                    Math.Round(position, 2, MidpointRounding.AwayFromZero),
                    0,
                    100)
                : null;
        settings.GameOverlayHorizontalPositionPercent = double.IsFinite(settings.GameOverlayHorizontalPositionPercent)
            ? Math.Clamp(
                Math.Round(settings.GameOverlayHorizontalPositionPercent, 2, MidpointRounding.AwayFromZero),
                0,
                100)
            : 50d;
        settings.GameOverlayPreset = settings.GameOverlayPreset?.Trim().ToLowerInvariant() switch
        {
            "compact" or "detailed" or "rivatuner" => settings.GameOverlayPreset.Trim().ToLowerInvariant(),
            _ => "rivatuner"
        };
        settings.GameOverlayLayoutMode = string.Equals(
            settings.GameOverlayLayoutMode?.Trim(),
            "horizontal",
            StringComparison.OrdinalIgnoreCase)
                ? "horizontal"
                : "vertical";
        settings.GameOverlayMonitorPositions = NormalizeOverlayMonitorPositions(
            settings.GameOverlayMonitorPositions);
        settings.GameOverlaySampling = settings.GameOverlaySampling?.Trim().ToLowerInvariant() switch
        {
            "low" or "standard" or "high" => settings.GameOverlaySampling.Trim().ToLowerInvariant(),
            _ => "standard"
        };
        GameOverlayMetricVisibility overlayMetrics =
            (settings.GameOverlayMetrics ?? new GameOverlayMetricVisibilitySettings()).ToEffective();
        settings.GameOverlayMetrics = GameOverlayMetricVisibilitySettings.FromEffective(overlayMetrics);
        GameOverlayAppearance appearance = NormalizeOverlayAppearance(
            (settings.GameOverlayAppearance ?? new GameOverlayAppearanceSettings()).ToEffective());
        settings.GameOverlayAppearance = GameOverlayAppearanceSettings.FromEffective(appearance);
        BandMetricVisibility effective =
            (settings.BandMetricVisibility ?? new BandMetricVisibilitySettings()).ToEffective();
        settings.BandMetricVisibility = BandMetricVisibilitySettings.FromEffective(effective);
    }

    internal static GameOverlayAppearance NormalizeOverlayAppearance(GameOverlayAppearance value) => new(
        string.IsNullOrWhiteSpace(value.FontFamily) ? "Consolas" : value.FontFamily.Trim(),
        double.IsFinite(value.FontSize) ? Math.Clamp(Math.Round(value.FontSize), 10, 28) : 13d,
        NormalizeColor(value.LabelColor, "#FF66D9FF"),
        NormalizeColor(value.ValueColor, "#FFFFFFFF"),
        NormalizeColor(value.OutlineColor, "#FF000000"),
        double.IsFinite(value.OutlineThickness) ? Math.Clamp(value.OutlineThickness, 0.5d, 4) : 1.5d,
        NormalizeColor(value.ShadowColor, "#CC000000"),
        double.IsFinite(value.ShadowOpacity) ? Math.Clamp(value.ShadowOpacity, 0.35d, 1) : 0.85d,
        double.IsFinite(value.ShadowDepth) ? Math.Clamp(value.ShadowDepth, 0, 8) : 1d,
        NormalizeColor(value.GpuColor, "#FF66D9FF"),
        NormalizeColor(value.CpuColor, "#FF8BE9FD"),
        NormalizeColor(value.FpsColor, "#FF50FA7B"),
        NormalizeColor(value.MemoryColor, "#FFF1FA8C"),
        NormalizeColor(value.NetworkColor, "#FFFFB86C"));

    internal static List<GameOverlayMonitorPositionSettings> NormalizeOverlayMonitorPositions(
        IEnumerable<GameOverlayMonitorPositionSettings>? positions)
    {
        const int CoordinateLimit = 1_000_000;
        var normalized = new List<GameOverlayMonitorPositionSettings>();
        foreach (GameOverlayMonitorPositionSettings? value in positions ?? [])
        {
            if (value is null)
            {
                continue;
            }

            string stableId = value.StableMonitorId?.Trim().ToUpperInvariant() ?? string.Empty;
            string gdiName = value.GdiDeviceName?.Trim().ToUpperInvariant() ?? string.Empty;
            int left = Math.Clamp(value.Left, -CoordinateLimit, CoordinateLimit);
            int top = Math.Clamp(value.Top, -CoordinateLimit, CoordinateLimit);
            int right = Math.Clamp(value.Right, -CoordinateLimit, CoordinateLimit);
            int bottom = Math.Clamp(value.Bottom, -CoordinateLimit, CoordinateLimit);
            if (stableId.Length is 0 or > 1024 ||
                gdiName.Length is 0 or > 128 ||
                right <= left ||
                bottom <= top)
            {
                continue;
            }

            normalized.Add(new GameOverlayMonitorPositionSettings
            {
                StableMonitorId = stableId,
                GdiDeviceName = gdiName,
                IsFallbackIdentity = value.IsFallbackIdentity,
                Left = left,
                Top = top,
                Right = right,
                Bottom = bottom,
                X = Math.Clamp(value.X, -CoordinateLimit, CoordinateLimit),
                Y = Math.Clamp(value.Y, -CoordinateLimit, CoordinateLimit)
            });
        }

        return normalized;
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        try
        {
            MediaColor color = (MediaColor)MediaColorConverter.ConvertFromString(value ?? fallback)!;
            return color.ToString();
        }
        catch
        {
            return fallback;
        }
    }
}

/// <summary>
/// Detached settings state returned from an atomic transaction. Every access
/// returns a fresh deep copy, so retaining a callback reference or mutating a
/// returned value cannot alter confirmed service state.
/// </summary>
public sealed class SettingsSnapshot
{
    private readonly AppSettings _settings;

    internal SettingsSnapshot(AppSettings settings, long revision)
    {
        _settings = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSettings();
        Revision = revision;
    }

    public long Revision { get; }

    public AppSettings Settings => Clone();

    public AppSettings Value => Clone();

    private AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(
        JsonSerializer.Serialize(_settings),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSettings();
}
