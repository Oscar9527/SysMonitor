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
    private static readonly ConcurrentDictionary<string, object> SharedPathPatchGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly AsyncLocal<HashSet<object>?> ActivePatchGates = new();
    private readonly object _sharedPathGate;
    private readonly object _sharedPathPatchGate;
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
        _sharedPathPatchGate = SharedPathPatchGates.GetOrAdd(
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
        if (!TryPatch(patch, out SettingsSnapshot snapshot))
        {
            throw new IOException("Settings could not be committed.");
        }

        return snapshot;
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
        using PatchGateLease patchGate = EnterPatchGate();
        return TryPatchCore(expectedRevision, patch, out snapshot);
    }

    private bool TryPatchCore(long? expectedRevision, Action<AppSettings> patch, out SettingsSnapshot snapshot)
    {

        AppSettings baseline;
        long baseRevision;

        lock (_sharedPathGate)
        lock (_gate)
        {
            EnsureLoadedLocked();
            // A second service instance may have committed since this instance
            // loaded. Refresh the detached state before checking the revision.
            RefreshExternalStateLocked();
            if (expectedRevision is long expected && expected != _revision)
            {
                snapshot = new SettingsSnapshot(_confirmed, _revision);
                return false;
            }

            baseRevision = _revision;
            baseline = Clone(_confirmed);
        }

        // User callbacks run without either the per-instance or shared-path
        // lock. The candidate is detached from all service-owned snapshots.
        AppSettings callbackCandidate = Clone(baseline);
        try
        {
            patch(callbackCandidate);
            Normalize(callbackCandidate);
        }
        catch
        {
            lock (_sharedPathGate)
            lock (_gate)
            {
                EnsureLoadedLocked();
                RefreshExternalStateLocked();
                snapshot = new SettingsSnapshot(_confirmed, _revision);
                return false;
            }
        }

        lock (_sharedPathGate)
        lock (_gate)
        {
            EnsureLoadedLocked();
            // Refresh while reacquiring the locks. If another writer won while
            // the callback was running, fail this transaction without replaying
            // the callback against the newer state.
            RefreshExternalStateLocked();
            if (_revision != baseRevision ||
                (expectedRevision is long expected && expected != _revision))
            {
                snapshot = new SettingsSnapshot(_confirmed, _revision);
                return false;
            }

            // Keep references handed to callbacks detached from all
            // service-owned snapshots, including after the callback returns and
            // mutates them.
            _working = Clone(baseline);
            _candidate = Clone(callbackCandidate);
            bool committed = TryCommitLocked(callbackCandidate, baseRevision);
            snapshot = new SettingsSnapshot(_confirmed, _revision);
            return committed;
        }
    }

    private PatchGateLease EnterPatchGate()
    {
        HashSet<object>? previous = ActivePatchGates.Value;
        bool ownsGate = previous is null || !previous.Contains(_sharedPathPatchGate);
        if (ownsGate)
        {
            Monitor.Enter(_sharedPathPatchGate);
        }

        HashSet<object> active = previous is null
            ? new HashSet<object>()
            : new HashSet<object>(previous);
        active.Add(_sharedPathPatchGate);
        ActivePatchGates.Value = active;
        return new PatchGateLease(_sharedPathPatchGate, previous, ownsGate);
    }

    private readonly struct PatchGateLease : IDisposable
    {
        private readonly object _gate;
        private readonly HashSet<object>? _previous;
        private readonly bool _ownsGate;

        public PatchGateLease(object gate, HashSet<object>? previous, bool ownsGate)
        {
            _gate = gate;
            _previous = previous;
            _ownsGate = ownsGate;
        }

        public void Dispose()
        {
            ActivePatchGates.Value = _previous;
            if (_ownsGate)
            {
                Monitor.Exit(_gate);
            }
        }
    }

    private bool TryCommitLocked(AppSettings candidate, long? expectedRevision)
    {
        if (expectedRevision is long expected && expected != _revision)
        {
            return false;
        }

        AppSettings normalized = Clone(candidate);
        Normalize(normalized);
        long nextRevision = GetNextRevision(_revision);
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
            _revision = NormalizePersistedRevision(revision);
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
                    File.Move(temporaryPath, SettingsPath, overwrite: true);
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
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string json = reader.ReadToEnd();
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
                    revision = NormalizePersistedRevision(persistedRevision);
                }

                return true;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(20);
            }
            catch
            {
                settings = null;
                revision = 0;
                return false;
            }
        }

        settings = null;
        revision = 0;
        return false;
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

            revision = NormalizePersistedRevision(parsed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long NormalizePersistedRevision(long revision)
    {
        // Keep every non-negative persisted value observable by all service
        // instances. In particular, normalizing MaxValue to zero would make
        // one instance disagree with another immediately after MaxValue was
        // legitimately persisted from MaxValue - 1.
        return revision >= 0 ? revision : 0;
    }

    private static long GetNextRevision(long revision)
    {
        // Wrap the terminal value to one instead of overflowing. Zero remains
        // the recovery baseline for missing, corrupt, or negative revisions.
        return revision is >= 0 and < long.MaxValue ? revision + 1 : 1;
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
        settings.PanelLeft = settings.PanelLeft is double left && double.IsFinite(left)
            ? Math.Clamp(Math.Round(left, MidpointRounding.AwayFromZero), -32000, 32000)
            : null;
        settings.PanelTop = settings.PanelTop is double top && double.IsFinite(top)
            ? Math.Clamp(Math.Round(top, MidpointRounding.AwayFromZero), -32000, 32000)
            : null;
        settings.PanelWidth = settings.PanelWidth is double width && double.IsFinite(width)
            ? Math.Clamp(Math.Round(width, MidpointRounding.AwayFromZero), 360, 1600)
            : null;
        settings.PanelHeight = settings.PanelHeight is double height && double.IsFinite(height)
            ? Math.Clamp(Math.Round(height, MidpointRounding.AwayFromZero), 360, 2000)
            : null;
        settings.BandFontFamily = string.IsNullOrWhiteSpace(settings.BandFontFamily)
            ? "Microsoft YaHei UI"
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

    internal static GameOverlayAppearance NormalizeOverlayAppearance(GameOverlayAppearance value)
    {
        string gpu = NormalizeColor(value.GpuColor, "#FFFF8C00");
        string cpu = NormalizeColor(value.CpuColor, "#FF00E5FF");
        string fps = NormalizeColor(value.FpsColor, "#FF00E676");
        string mem = NormalizeColor(value.MemoryColor, "#FFFFD600");
        string net = NormalizeColor(value.NetworkColor, "#FFE040FB");

        // Automatically upgrade older purple/blue/dracula/pastel default schemes to iconic MSI Afterburner colors
        string[] oldGpu = ["#FF7E57C2", "#FF66D9FF", "#FFFFA94D", "#FF7043", "#FFF4F5F7", "#FFFFE6C7"];
        string[] oldCpu = ["#FF1976D2", "#FF8BE9FD", "#FFFFD166", "#FF42A5F5"];
        string[] oldFps = ["#FF1B9A5A", "#FF50FA7B", "#FF95D5B2", "#FF66BB6A"];
        string[] oldMem = ["#FFD97706", "#FFF1FA8C", "#FFFF8E72", "#FFA726"];
        string[] oldNet = ["#FF0097A7", "#FFFFB86C", "#FFE4B1FF", "#AB47BC"];

        if (oldGpu.Any(c => string.Equals(gpu, c, StringComparison.OrdinalIgnoreCase))) gpu = "#FFFF8C00";
        if (oldCpu.Any(c => string.Equals(cpu, c, StringComparison.OrdinalIgnoreCase))) cpu = "#FF00E5FF";
        if (oldFps.Any(c => string.Equals(fps, c, StringComparison.OrdinalIgnoreCase))) fps = "#FF00E676";
        if (oldMem.Any(c => string.Equals(mem, c, StringComparison.OrdinalIgnoreCase))) mem = "#FFFFD600";
        if (oldNet.Any(c => string.Equals(net, c, StringComparison.OrdinalIgnoreCase))) net = "#FFE040FB";

        string font = string.IsNullOrWhiteSpace(value.FontFamily) ||
                      string.Equals(value.FontFamily, "Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(value.FontFamily, "Microsoft JhengHei UI", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(value.FontFamily, "Segoe UI", StringComparison.OrdinalIgnoreCase)
            ? "Consolas"
            : value.FontFamily.Trim();

        return new GameOverlayAppearance(
            font,
            double.IsFinite(value.FontSize) ? Math.Clamp(Math.Round(value.FontSize), 10, 28) : 16d,
            NormalizeColor(value.LabelColor, gpu),
            NormalizeColor(value.ValueColor, gpu),
            NormalizeColor(value.OutlineColor, "#FF000000"),
            double.IsFinite(value.OutlineThickness) ? Math.Clamp(value.OutlineThickness, 0.5d, 4) : 1.5d,
            NormalizeColor(value.ShadowColor, "#CC000000"),
            double.IsFinite(value.ShadowOpacity) ? Math.Clamp(value.ShadowOpacity, 0.35d, 1) : 0.95d,
            double.IsFinite(value.ShadowDepth) ? Math.Clamp(value.ShadowDepth, 0, 8) : 1d,
            gpu,
            cpu,
            fps,
            mem,
            net);
    }

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
