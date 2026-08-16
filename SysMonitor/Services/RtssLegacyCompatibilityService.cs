using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SysMonitor.Services;

/// <summary>Result codes returned by the RTSS legacy compatibility service.</summary>
public enum RtssCompatibilityCode
{
    Success,
    AlreadyEnabled,
    Disabled,
    PendingNoChange,
    PendingApplied,
    InvalidExecutable,
    UnsafePath,
    ProfilesUnavailable,
    ProfileNotFound,
    ProfileAmbiguous,
    AmbiguousIni,
    SameBasenameConflict,
    Conflict,
    CorruptManifest,
    InvalidManifest,
    IoError
}

public enum RtssManifestPhase { Pending, Applied }

/// <summary>A small, UI-friendly snapshot of a target and its RTSS profile.</summary>
public sealed record RtssCompatibilityResult(
    bool Success,
    bool Enabled,
    bool Managed,
    bool CanEnable,
    bool CanDisable,
    RtssCompatibilityCode Code,
    string Diagnostic,
    string? ExecutablePath,
    string? ExecutableName,
    string? ProfilePath,
    bool RestartRequired = false)
{
    public RtssCompatibilityCode Status => Code;
}

public sealed record RtssManagedTargetSummary(
    bool Managed,
    bool Enabled,
    bool CanEnable,
    bool CanDisable,
    RtssCompatibilityCode Code,
    string Diagnostic,
    string? ExecutablePath,
    string? ExecutableName,
    string? ProfilePath,
    RtssManifestPhase? Phase,
    bool OriginalExisted,
    string? OriginalHash,
    string? AppliedHash)
{
    public RtssCompatibilityCode Status => Code;
}

/// <summary>
/// Safely manages the two RTSS compatibility keys for one game executable at a time.
/// The service never starts RTSS, loads its DLL, or writes global/config profiles.
/// </summary>
public sealed class RtssLegacyCompatibilityService
{
    private const string NewProfile = "[Hooking]\r\nEnableHooking=1\r\nHookDirectDraw=1\r\n";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = false
    };

    private readonly string _profilesDirectory;
    private readonly string _backupDirectory;

    public RtssLegacyCompatibilityService(string profilesDirectory, string backupDirectory)
    {
        _profilesDirectory = Path.GetFullPath(profilesDirectory ?? throw new ArgumentNullException(nameof(profilesDirectory)));
        _backupDirectory = Path.GetFullPath(backupDirectory ?? throw new ArgumentNullException(nameof(backupDirectory)));
    }

    // The optional RTSS root is accepted for callers that already have a locator;
    // profile operations intentionally use only the explicit Profiles directory.
    public RtssLegacyCompatibilityService(string profilesDirectory, string backupDirectory, string? rtssRootDirectory)
        : this(profilesDirectory, backupDirectory) { }

    /// <summary>Creates a best-effort production locator without touching disk.</summary>
    public static RtssLegacyCompatibilityService CreateDefault(string? backupDirectory = null)
    {
        string? root = null;
        try
        {
            foreach (var process in Process.GetProcessesByName("RTSS"))
            {
                try
                {
                    var image = ProcessExecutablePathResolver.TryResolve(process.Id);
                    if (!string.IsNullOrWhiteSpace(image) && File.Exists(image)) { root = Path.GetDirectoryName(image); if (root is not null) break; }
                }
                catch { }
                finally { process.Dispose(); }
            }
            foreach (var p in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RivaTuner Statistics Server")
            })
            {
                if (root is null && File.Exists(Path.Combine(p, "RTSS.exe"))) { root = p; break; }
            }
        }
        catch { }
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server");
        return new RtssLegacyCompatibilityService(Path.Combine(root, "Profiles"),
            backupDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysMonitor", "RtssCompatibility"));
    }

    public string ProfilesDirectory => _profilesDirectory;
    public string BackupDirectory => _backupDirectory;

    public RtssCompatibilityResult Query(string executablePath) => GetStatus(executablePath);
    public RtssCompatibilityResult Inspect(string executablePath) => GetStatus(executablePath);
    public RtssCompatibilityResult GetExecutableStatus(string executablePath) => GetStatus(executablePath);
    public RtssCompatibilityResult GetStatus(string executablePath)
    {
        if (!TryValidateExecutable(executablePath, requireExisting: true, out var exe, out var name, out var error))
            return Fail(error.code, error.message, executablePath, name);
        try
        {
            var profile = ResolveProfile(name!, allowMissing: true, out var profileCode, out var profileDiagnostic);
            var manifest = LoadManifest(name!, out var manifestCode, out var manifestDiagnostic);
            if (manifestCode == RtssCompatibilityCode.InvalidManifest)
                return Fail(RtssCompatibilityCode.CorruptManifest, manifestDiagnostic, exe, name, profile?.Path);
            if (manifestCode != RtssCompatibilityCode.Success)
                return Fail(manifestCode, manifestDiagnostic, exe, name, profile?.Path);
            if (manifest is not null)
            {
                if (!TryValidateManifest(manifest, out var mError))
                    return Fail(RtssCompatibilityCode.CorruptManifest, mError, exe, name, profile?.Path);
                if (!PathEquals(manifest.ExecutablePath, exe!))
                    return Fail(RtssCompatibilityCode.SameBasenameConflict, "A different executable already owns this basename.", exe, name, manifest.ProfilePath);
                var current = File.Exists(manifest.ProfilePath) ? HashFile(manifest.ProfilePath) : null;
                bool enabled = manifest.Phase == RtssManifestPhase.Applied && current == manifest.AppliedHash;
                return Result(true, enabled, true, false, enabled, enabled ? RtssCompatibilityCode.Success : RtssCompatibilityCode.Conflict,
                    enabled ? "Compatibility is enabled." : "Managed profile requires reconciliation.", exe, name, manifest.ProfilePath);
            }
            if (profileCode != RtssCompatibilityCode.Success && profileCode != RtssCompatibilityCode.ProfileNotFound)
                return Fail(profileCode, profileDiagnostic, exe, name, profile?.Path);
            bool existingEnabled = false;
            if (profile is not null)
            {
                try { existingEnabled = PatchProfile(File.ReadAllBytes(profile.Path)).SequenceEqual(File.ReadAllBytes(profile.Path)); }
                catch (IniAmbiguousException ex) { return Fail(RtssCompatibilityCode.AmbiguousIni, ex.Message, exe, name, profile.Path); }
            }
            return Result(true, existingEnabled, false, true, false,
                existingEnabled ? RtssCompatibilityCode.Success : RtssCompatibilityCode.Success,
                existingEnabled ? "Profile already contains compatibility keys." : "Profile can be enabled.", exe, name, profile?.Path);
        }
        catch (Exception ex) { return Fail(RtssCompatibilityCode.IoError, ex.Message, exe, name); }
    }

    public RtssCompatibilityResult SetEnabled(string executablePath, bool enabled) => enabled ? Enable(executablePath) : Disable(executablePath);
    public RtssCompatibilityResult EnableCompatibility(string executablePath) => Enable(executablePath);
    public RtssCompatibilityResult DisableCompatibility(string executablePath) => Disable(executablePath);

    public RtssCompatibilityResult Enable(string executablePath)
    {
        if (!TryValidateExecutable(executablePath, requireExisting: true, out var exe, out var name, out var error))
            return Fail(error.code, error.message, executablePath, name);
        try
        {
            if (!Directory.Exists(_profilesDirectory)) return Fail(RtssCompatibilityCode.ProfilesUnavailable, "RTSS Profiles directory does not exist.", exe, name);
            if (!ValidateDirectory(_profilesDirectory, out var dirError)) return Fail(RtssCompatibilityCode.UnsafePath, dirError, exe, name);
            ReconcilePending();
            var existingManifest = LoadManifest(name!, out var manifestCode, out var manifestDiagnostic);
            if (manifestCode == RtssCompatibilityCode.InvalidManifest || existingManifest is not null && !TryValidateManifest(existingManifest, out _))
                return Fail(RtssCompatibilityCode.CorruptManifest, manifestDiagnostic.Length == 0 ? "Corrupt manifest." : manifestDiagnostic, exe, name);
            if (existingManifest is not null)
            {
                if (!PathEquals(existingManifest.ExecutablePath, exe!))
                    return Fail(RtssCompatibilityCode.SameBasenameConflict, "A different executable already owns this basename.", exe, name, existingManifest.ProfilePath);
                if (existingManifest.Phase == RtssManifestPhase.Pending)
                    return Fail(RtssCompatibilityCode.Conflict, "A pending compatibility operation requires reconciliation.", exe, name, existingManifest.ProfilePath);
                if (existingManifest.Phase == RtssManifestPhase.Applied)
                {
                    if (!File.Exists(existingManifest.ProfilePath) || HashFile(existingManifest.ProfilePath) != existingManifest.AppliedHash)
                        return Fail(RtssCompatibilityCode.Conflict, "The managed profile was modified or removed externally.", exe, name, existingManifest.ProfilePath);
                    return Result(true, true, true, false, true, RtssCompatibilityCode.AlreadyEnabled, "Compatibility is already enabled.", exe, name, existingManifest.ProfilePath);
                }
            }
            foreach (var m in ReadValidManifests())
                if (!string.Equals(m.ExecutableName, name, StringComparison.OrdinalIgnoreCase) || PathEquals(m.ExecutablePath, exe!)) continue;
                else return Fail(RtssCompatibilityCode.SameBasenameConflict, "The basename is already managed for another executable.", exe, name);

            var profile = ResolveProfile(name!, allowMissing: true, out var profileCode, out var profileDiagnostic);
            if (profileCode is RtssCompatibilityCode.ProfileAmbiguous or RtssCompatibilityCode.UnsafePath)
                return Fail(profileCode, profileDiagnostic, exe, name);
            byte[] original = profile is null ? Array.Empty<byte>() : File.ReadAllBytes(profile.Path);
            bool originalExists = profile is not null;
            byte[] applied;
            try { applied = originalExists ? PatchProfile(original) : Encoding.ASCII.GetBytes(NewProfile); }
            catch (IniAmbiguousException ex) { return Fail(RtssCompatibilityCode.AmbiguousIni, ex.Message, exe, name, profile?.Path); }
            var manifest = new RtssManifest
            {
                Version = 1, ExecutablePath = exe!, ExecutableName = name!, ProfilesDirectory = _profilesDirectory,
                ProfilePath = profile?.Path ?? Path.Combine(_profilesDirectory, name + ".cfg"),
                OriginalExisted = originalExists, OriginalBytes = Convert.ToBase64String(original),
                OriginalHash = HashBytes(original), AppliedHash = HashBytes(applied), Phase = RtssManifestPhase.Pending
            };
            WriteManifestAtomic(manifest);
            // The pending marker protects against a crash, but never permits us to
            // overwrite a profile that changed between the initial read and write.
            var beforeApply = ResolveProfile(name!, allowMissing: true, out var beforeCode, out _);
            if (beforeCode is RtssCompatibilityCode.ProfileAmbiguous or RtssCompatibilityCode.UnsafePath ||
                (originalExists && (beforeApply is null || HashFile(beforeApply.Path) != manifest.OriginalHash)) ||
                (!originalExists && beforeApply is not null))
                return Fail(RtssCompatibilityCode.Conflict, "Profile changed while preparing compatibility.", exe, name, manifest.ProfilePath);
            if (!ValidateManifestMutationPaths(manifest, out var mutationError))
                return Fail(RtssCompatibilityCode.UnsafePath, mutationError, exe, name, manifest.ProfilePath);
            WriteProfileAtomic(manifest.ProfilePath, applied);
            if (HashFile(manifest.ProfilePath) != manifest.AppliedHash) throw new IOException("Applied profile hash verification failed.");
            manifest.Phase = RtssManifestPhase.Applied;
            WriteManifestAtomic(manifest);
            return Result(true, true, true, false, true, RtssCompatibilityCode.Success, "Compatibility enabled.", exe, name, manifest.ProfilePath, true);
        }
        catch (Exception ex) { return Fail(RtssCompatibilityCode.IoError, ex.Message, exe, name); }
    }

    public RtssCompatibilityResult Disable(string executablePath)
    {
        if (!TryValidateExecutable(executablePath, requireExisting: false, out var exe, out var name, out var error))
            return Fail(error.code, error.message, executablePath, name);
        try
        {
            var manifest = LoadManifest(name!, out var code, out var diagnostic);
            if (manifest is null) return Fail(RtssCompatibilityCode.Success, "No managed profile exists.", exe, name);
            string mError;
            bool manifestValid = TryValidateManifest(manifest, out mError);
            if (code == RtssCompatibilityCode.InvalidManifest || !manifestValid)
                return Fail(RtssCompatibilityCode.CorruptManifest, string.IsNullOrEmpty(mError) ? diagnostic : mError, exe, name);
            if (!PathEquals(manifest.ExecutablePath, exe!)) return Fail(RtssCompatibilityCode.SameBasenameConflict, "Executable does not match managed target.", exe, name, manifest.ProfilePath);
            if (!File.Exists(manifest.ProfilePath) || HashFile(manifest.ProfilePath) != manifest.AppliedHash)
                return Fail(RtssCompatibilityCode.Conflict, "Current profile is missing or has been modified.", exe, name, manifest.ProfilePath);
            if (!ValidateManifestMutationPaths(manifest, out var mutationError))
                return Fail(RtssCompatibilityCode.UnsafePath, mutationError, exe, name, manifest.ProfilePath);
            if (manifest.OriginalExisted)
                WriteProfileAtomic(manifest.ProfilePath, Convert.FromBase64String(manifest.OriginalBytes));
            else
                File.Delete(manifest.ProfilePath);
            if (manifest.OriginalExisted && (!File.Exists(manifest.ProfilePath) || HashFile(manifest.ProfilePath) != manifest.OriginalHash) || !manifest.OriginalExisted && File.Exists(manifest.ProfilePath))
                throw new IOException("Restore verification failed.");
            if (!DeleteManifest(name!))
                return Fail(RtssCompatibilityCode.IoError, "Profile was restored but the manifest could not be removed.", exe, name, manifest.ProfilePath);
            return Result(true, false, false, true, false, RtssCompatibilityCode.Disabled, "Compatibility disabled and original profile restored.", exe, name, manifest.ProfilePath, true);
        }
        catch (Exception ex) { return Fail(RtssCompatibilityCode.IoError, ex.Message, exe, name); }
    }

    public IReadOnlyList<RtssManagedTargetSummary> EnumerateManagedTargets()
    {
        var result = new List<RtssManagedTargetSummary>();
        if (!Directory.Exists(_backupDirectory) || !ValidateDirectoryMetadata(_backupDirectory)) return result;
        foreach (var path in Directory.EnumerateFiles(_backupDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!IsSafeDirectChild(path, _backupDirectory, out var unsafeManifestError))
            {
                result.Add(new(false, false, false, false, RtssCompatibilityCode.CorruptManifest, unsafeManifestError, null, Path.GetFileNameWithoutExtension(path), null, null, false, null, null));
                continue;
            }
            RtssManifest? m = null;
            string err = string.Empty;
            try { m = JsonSerializer.Deserialize<RtssManifest>(File.ReadAllText(path), JsonOptions); }
            catch { }
            if (m is null || !TryValidateManifest(m, out err))
            {
                result.Add(new(false, false, false, false, RtssCompatibilityCode.CorruptManifest, err.Length == 0 ? "Corrupt manifest." : err, m?.ExecutablePath, m?.ExecutableName, m?.ProfilePath, m?.Phase, m?.OriginalExisted ?? false, m?.OriginalHash, m?.AppliedHash));
                continue;
            }
            bool enabled = m.Phase == RtssManifestPhase.Applied && File.Exists(m.ProfilePath) && HashFile(m.ProfilePath) == m.AppliedHash;
            result.Add(new(true, enabled, !enabled, enabled, enabled ? RtssCompatibilityCode.Success : RtssCompatibilityCode.Conflict, enabled ? "Applied." : "Conflict or pending.", m.ExecutablePath, m.ExecutableName, m.ProfilePath, m.Phase, m.OriginalExisted, m.OriginalHash, m.AppliedHash));
        }
        return result;
    }

    public IReadOnlyList<RtssManagedTargetSummary> GetManagedTargets() => EnumerateManagedTargets();

    private void ReconcilePending()
    {
        if (!Directory.Exists(_backupDirectory) || !ValidateDirectoryMetadata(_backupDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(_backupDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!IsSafeDirectChild(path, _backupDirectory, out _)) continue;
            RtssManifest? m;
            try { m = JsonSerializer.Deserialize<RtssManifest>(File.ReadAllText(path), JsonOptions); } catch { continue; }
            if (m is null || m.Phase != RtssManifestPhase.Pending || !TryValidateManifest(m, out _)) continue;
            bool exists = File.Exists(m.ProfilePath);
            string? current = exists ? HashFile(m.ProfilePath) : null;
            bool original = m.OriginalExisted ? exists && current == m.OriginalHash : !exists;
            bool applied = exists && current == m.AppliedHash;
            if (original) _ = DeleteManifest(m.ExecutableName);
            else if (applied) { m.Phase = RtssManifestPhase.Applied; WriteManifestAtomic(m); }
            // Any other state is retained as an explicit conflict; no profile write occurs.
        }
    }

    private IEnumerable<RtssManifest> ReadValidManifests()
    {
        if (!Directory.Exists(_backupDirectory) || !ValidateDirectoryMetadata(_backupDirectory)) yield break;
        foreach (var path in Directory.EnumerateFiles(_backupDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!IsSafeDirectChild(path, _backupDirectory, out _)) continue;
            RtssManifest? m; try { m = JsonSerializer.Deserialize<RtssManifest>(File.ReadAllText(path), JsonOptions); } catch { continue; }
            if (m is not null && TryValidateManifest(m, out _)) yield return m;
        }
    }

    private ProfileCandidate? ResolveProfile(string basename, bool allowMissing, out RtssCompatibilityCode code, out string diagnostic)
    {
        code = RtssCompatibilityCode.Success; diagnostic = string.Empty;
        if (!Directory.Exists(_profilesDirectory)) { code = RtssCompatibilityCode.ProfilesUnavailable; diagnostic = "Profiles directory does not exist."; return null; }
        if (!ValidateDirectory(_profilesDirectory, out diagnostic)) { code = RtssCompatibilityCode.UnsafePath; return null; }
        string expected = basename + ".cfg";
        var matches = new List<string>();
        foreach (var p in Directory.EnumerateFileSystemEntries(_profilesDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var file = Path.GetFileName(p);
            if (!string.Equals(file, expected, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(p)) { code = RtssCompatibilityCode.UnsafePath; diagnostic = "Profile path is a directory."; return null; }
            if (!IsSafeDirectChild(p, _profilesDirectory, out _)) { code = RtssCompatibilityCode.UnsafePath; diagnostic = "Profile path is unsafe."; return null; }
            matches.Add(p);
        }
        if (matches.Count > 1) { code = RtssCompatibilityCode.ProfileAmbiguous; diagnostic = "Multiple profiles match this basename."; return null; }
        if (matches.Count == 0) { code = allowMissing ? RtssCompatibilityCode.ProfileNotFound : RtssCompatibilityCode.ProfileNotFound; diagnostic = "Profile does not exist."; return null; }
        return new ProfileCandidate(matches[0]);
    }

    private RtssManifest? LoadManifest(string basename, out RtssCompatibilityCode code, out string diagnostic)
    {
        code = RtssCompatibilityCode.Success; diagnostic = string.Empty;
        string path = ManifestPath(basename);
        if (!File.Exists(path)) return null;
        if (!IsSafeDirectChild(path, _backupDirectory, out var pathError))
        {
            code = RtssCompatibilityCode.InvalidManifest;
            diagnostic = pathError;
            return null;
        }
        try
        {
            var m = JsonSerializer.Deserialize<RtssManifest>(File.ReadAllText(path), JsonOptions);
            if (m is null) { code = RtssCompatibilityCode.InvalidManifest; diagnostic = "Manifest is empty."; return null; }
            return m;
        }
        catch (Exception ex) { code = RtssCompatibilityCode.InvalidManifest; diagnostic = ex.Message; return null; }
    }

    private bool TryValidateManifest(RtssManifest m, out string error)
    {
        try { return TryValidateManifestCore(m, out error); }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private bool TryValidateManifestCore(RtssManifest m, out string error)
    {
        error = string.Empty;
        if (m.Version != 1 || string.IsNullOrWhiteSpace(m.ExecutablePath) || string.IsNullOrWhiteSpace(m.ExecutableName) || string.IsNullOrWhiteSpace(m.ProfilesDirectory) || string.IsNullOrWhiteSpace(m.ProfilePath) || !string.Equals(Path.GetFileName(m.ExecutablePath), m.ExecutableName, StringComparison.OrdinalIgnoreCase)) { error = "Manifest executable metadata is invalid."; return false; }
        if (!TryValidateExecutable(m.ExecutablePath, false, out var exe, out var name, out var e)) { error = e.message; return false; }
        if (!string.Equals(name, m.ExecutableName, StringComparison.OrdinalIgnoreCase) || !PathEquals(exe!, m.ExecutablePath)) { error = "Manifest executable mismatch."; return false; }
        if (!PathEquals(m.ProfilesDirectory, _profilesDirectory) || !ValidateDirectoryMetadata(m.ProfilesDirectory)) { error = "Manifest profiles directory is invalid."; return false; }
        if (!IsSafeDirectChildMetadata(m.ProfilePath, m.ProfilesDirectory, out error) || !string.Equals(Path.GetFileName(m.ProfilePath), m.ExecutableName + ".cfg", StringComparison.OrdinalIgnoreCase)) return false;
        if (HasAmbiguousProfileMatch(m.ProfilesDirectory, m.ExecutableName + ".cfg")) { error = "Multiple profiles match this basename."; return false; }
        if (string.Equals(Path.GetFileNameWithoutExtension(m.ProfilePath), "Global", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileNameWithoutExtension(m.ProfilePath), "Config", StringComparison.OrdinalIgnoreCase)) { error = "Global and Config profiles are not managed."; return false; }
        if (m.Phase is not RtssManifestPhase.Pending and not RtssManifestPhase.Applied || string.IsNullOrEmpty(m.AppliedHash) || string.IsNullOrEmpty(m.OriginalHash)) { error = "Manifest phase or hashes are invalid."; return false; }
        try { _ = Convert.FromBase64String(m.OriginalBytes ?? string.Empty); } catch { error = "Manifest original bytes are invalid."; return false; }
        return true;
    }

    private bool ValidateManifestMutationPaths(RtssManifest m, out string error)
    {
        error = string.Empty;
        if (!Directory.Exists(_backupDirectory) || !ValidateDirectoryMetadata(_backupDirectory))
        {
            error = "Backup directory is missing or unsafe.";
            return false;
        }
        if (!PathEquals(m.ProfilesDirectory, _profilesDirectory) || !ValidateDirectoryMetadata(_profilesDirectory))
        {
            error = "Profiles directory is missing or unsafe.";
            return false;
        }
        if (!IsSafeDirectChildMetadata(m.ProfilePath, _profilesDirectory, out error)) return false;
        if (!string.Equals(Path.GetFileName(m.ProfilePath), m.ExecutableName + ".cfg", StringComparison.OrdinalIgnoreCase))
        {
            error = "Manifest profile name is invalid.";
            return false;
        }
        if (string.Equals(Path.GetFileNameWithoutExtension(m.ProfilePath), "Global", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(m.ProfilePath), "Config", StringComparison.OrdinalIgnoreCase))
        {
            error = "Global and Config profiles are not managed.";
            return false;
        }
        if (HasAmbiguousProfileMatch(_profilesDirectory, m.ExecutableName + ".cfg"))
        {
            error = "Multiple profiles match this basename.";
            return false;
        }
        return true;
    }

    private void WriteManifestAtomic(RtssManifest m)
    {
        Directory.CreateDirectory(_backupDirectory);
        if (!ValidateDirectoryMetadata(_backupDirectory)) throw new IOException("Backup directory is unsafe.");
        string path = ManifestPath(m.ExecutableName), temp = Path.Combine(_backupDirectory, $".{m.ExecutableName}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(m, JsonOptions));
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { fs.Write(bytes); fs.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private bool DeleteManifest(string basename)
    {
        try
        {
            if (!Directory.Exists(_backupDirectory) || !ValidateDirectoryMetadata(_backupDirectory)) return false;
            var p = ManifestPath(basename);
            if (File.Exists(p)) File.Delete(p);
            return !File.Exists(p);
        }
        catch { return false; }
    }
    private string ManifestPath(string basename) => Path.Combine(_backupDirectory, basename + ".json");

    private static void WriteProfileAtomic(string path, byte[] bytes)
    {
        string? dir = Path.GetDirectoryName(path); if (dir is null) throw new IOException("Invalid profile path.");
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { fs.Write(bytes); fs.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static byte[] PatchProfile(byte[] bytes)
    {
        if ((bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) || (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff))
            throw new IniAmbiguousException("UTF-16 profiles are not safely patchable.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new IniAmbiguousException("Profile encoding is not UTF-8/ASCII: " + ex.Message); }
        var lines = SplitLines(text);
        var sections = lines.Select((line, i) => (line, i)).Where(x => IsSection(x.line.Content, "Hooking")).ToList();
        if (sections.Count > 1) throw new IniAmbiguousException("Duplicate Hooking sections.");
        if (sections.Count == 0)
        {
            string nl = lines.FirstOrDefault(x => x.Ending.Length > 0)?.Ending ?? string.Empty; if (nl.Length == 0) nl = "\r\n";
            if (lines.Count > 0 && lines[^1].Content.Length > 0) lines.Add(new LineSegment(string.Empty, nl));
            lines.Add(new LineSegment("[Hooking]", nl)); lines.Add(new LineSegment("EnableHooking=1", nl)); lines.Add(new LineSegment("HookDirectDraw=1", string.Empty));
            return Encoding.UTF8.GetBytes(JoinLines(lines));
        }
        int start = sections[0].i, end = lines.Count;
        for (int i = start + 1; i < lines.Count; i++) if (IsAnySection(lines[i].Content)) { end = i; break; }
        var keyIndexes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "EnableHooking", "HookDirectDraw" }) keyIndexes[key] = new List<int>();
        for (int i = start + 1; i < end; i++)
        {
            var match = System.Text.RegularExpressions.Regex.Match(lines[i].Content, @"^\s*(EnableHooking|HookDirectDraw)\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success) keyIndexes[match.Groups[1].Value].Add(i);
        }
        if (keyIndexes.Any(k => k.Value.Count > 1)) throw new IniAmbiguousException("Duplicate managed keys in Hooking section.");
        string nlExisting = lines[start].Ending.Length > 0 ? lines[start].Ending : "\r\n";
        if (lines[start].Ending.Length == 0 && keyIndexes.Any(k => k.Value.Count == 0))
            lines[start] = lines[start] with { Ending = nlExisting };
        foreach (var kv in keyIndexes)
            if (kv.Value.Count == 1)
            {
                int index = kv.Value[0];
                string content = lines[index].Content;
                int equals = content.IndexOf('=');
                int comment = content.IndexOfAny(new[] { ';', '#' }, equals + 1);
                int suffixStart;
                if (comment >= 0)
                {
                    suffixStart = comment;
                    while (suffixStart > equals + 1 && char.IsWhiteSpace(content[suffixStart - 1])) suffixStart--;
                }
                else
                {
                    suffixStart = content.Length;
                    while (suffixStart > equals + 1 && char.IsWhiteSpace(content[suffixStart - 1])) suffixStart--;
                }
                string suffix = content[suffixStart..];
                lines[index] = lines[index] with { Content = content[..(equals + 1)] + "1" + suffix };
            }
            else lines.Insert(end++, new LineSegment(kv.Key + "=1", nlExisting));
        return Encoding.UTF8.GetBytes(JoinLines(lines));
    }

    private static bool IsAlreadyEnabled(byte[] bytes)
    {
        try { return PatchProfile(bytes).SequenceEqual(bytes); } catch { return false; }
    }
    private static bool IsSection(string s, string name)
    {
        var t = s.TrimStart('\uFEFF', ' ', '\t');
        int comment = t.IndexOfAny(new[] { ';', '#' });
        if (comment >= 0) t = t[..comment];
        return t.TrimEnd().Equals("[" + name + "]", StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsAnySection(string s)
    {
        var t = s.TrimStart('\uFEFF', ' ', '\t');
        int comment = t.IndexOfAny(new[] { ';', '#' });
        if (comment >= 0) t = t[..comment];
        t = t.Trim(); return t.StartsWith("[") && t.EndsWith("]");
    }

    private static List<LineSegment> SplitLines(string text)
    {
        var result = new List<LineSegment>(); int pos = 0;
        while (pos < text.Length)
        {
            int i = pos; while (i < text.Length && text[i] != '\r' && text[i] != '\n') i++;
            string ending = string.Empty;
            if (i < text.Length) { if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') ending = "\r\n"; else ending = text[i].ToString(); }
            result.Add(new LineSegment(text[pos..i], ending)); pos = i + ending.Length;
        }
        return result;
    }
    private static string JoinLines(IEnumerable<LineSegment> lines) => string.Concat(lines.Select(x => x.Content + x.Ending));

    private static bool TryValidateExecutable(string? path, bool requireExisting, out string? fullPath, out string? basename, out (RtssCompatibilityCode code, string message) error)
    {
        fullPath = basename = null; error = (RtssCompatibilityCode.InvalidExecutable, "Executable path is invalid.");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || IsUnsafePathSyntax(path)) { error = (RtssCompatibilityCode.UnsafePath, "Executable path must be a rooted local path without ADS or device syntax."); return false; }
        try { fullPath = Path.GetFullPath(path); } catch { error = (RtssCompatibilityCode.InvalidExecutable, "Executable path cannot be canonicalized."); return false; }
        basename = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(basename) || !basename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || basename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || basename.Any(c => c < 0x20 || c is '<' or '>' or '|' or '*' or '?') || string.Equals(Path.GetFileNameWithoutExtension(basename), "Global", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileNameWithoutExtension(basename), "Config", StringComparison.OrdinalIgnoreCase)) { error = (RtssCompatibilityCode.InvalidExecutable, "Executable name is invalid."); return false; }
        if (requireExisting)
        {
            if (!File.Exists(fullPath)) { error = (RtssCompatibilityCode.InvalidExecutable, "Executable does not exist."); return false; }
            try { var a = File.GetAttributes(fullPath); if ((a & FileAttributes.Directory) != 0 || (a & FileAttributes.ReparsePoint) != 0) { error = (RtssCompatibilityCode.UnsafePath, "Executable must be a regular non-reparse file."); return false; } }
            catch { error = (RtssCompatibilityCode.InvalidExecutable, "Executable cannot be inspected."); return false; }
        }
        if (!ValidateParentChain(fullPath)) { error = (RtssCompatibilityCode.UnsafePath, "Executable parent directory is a reparse point."); return false; }
        return true;
    }

    private static bool ValidateDirectory(string path, out string error) { error = string.Empty; if (!ValidateDirectoryMetadata(path)) { error = "Directory is missing or reparse-point protected."; return false; } return true; }
    private static bool ValidateDirectoryMetadata(string path) { try { if (!Path.IsPathRooted(path) || IsUnsafePathSyntax(path) || !Directory.Exists(path)) return false; return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && ValidateParentChain(path); } catch { return false; } }
    private static bool ValidateParentChain(string path)
    {
        try
        {
            var current = Directory.GetParent(path);
            while (current is not null)
            {
                if (Directory.Exists(current.FullName) && (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0) return false;
                current = current.Parent;
            }
            return true;
        }
        catch { return false; }
    }
    private static bool IsSafeDirectChild(string path, string parent, out string error)
    {
        error = string.Empty; try { if (!Path.IsPathRooted(path) || IsUnsafePathSyntax(path) || !File.Exists(path) && !Directory.Exists(path)) { error = "Path is missing or unsafe."; return false; } var f = Path.GetFullPath(path); var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar; if (!f.StartsWith(p, StringComparison.OrdinalIgnoreCase) || f[p.Length..].Contains(Path.DirectorySeparatorChar) || f[p.Length..].Contains(Path.AltDirectorySeparatorChar)) { error = "Path is not a direct child."; return false; } var a = File.GetAttributes(f); if ((a & FileAttributes.ReparsePoint) != 0 || (a & FileAttributes.Directory) != 0) { error = "Path is a reparse point or directory."; return false; } return true; } catch (Exception ex) { error = ex.Message; return false; }
    }
    private static bool IsSafeDirectChildMetadata(string path, string parent, out string error)
    {
        error = string.Empty;
        try
        {
            if (!Path.IsPathRooted(path) || IsUnsafePathSyntax(path)) { error = "Path is missing or unsafe."; return false; }
            var f = Path.GetFullPath(path);
            var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!f.StartsWith(p, StringComparison.OrdinalIgnoreCase) || f[p.Length..].Contains(Path.DirectorySeparatorChar) || f[p.Length..].Contains(Path.AltDirectorySeparatorChar)) { error = "Path is not a direct child."; return false; }
            if (File.Exists(f) || Directory.Exists(f))
            {
                var a = File.GetAttributes(f);
                if ((a & FileAttributes.ReparsePoint) != 0 || (a & FileAttributes.Directory) != 0) { error = "Path is a reparse point or directory."; return false; }
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
    private static bool HasAmbiguousProfileMatch(string parent, string expected)
    {
        try
        {
            if (!Directory.Exists(parent)) return false;
            return Directory.EnumerateFiles(parent, "*", SearchOption.TopDirectoryOnly)
                .Count(p => string.Equals(Path.GetFileName(p), expected, StringComparison.OrdinalIgnoreCase)) > 1;
        }
        catch { return true; }
    }
    private static bool IsUnsafePathSyntax(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) || (path.IndexOf(':', 2) >= 0);
    private static bool PathEquals(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string HashFile(string path) => HashBytes(File.ReadAllBytes(path));

    private static RtssCompatibilityResult Result(bool success, bool enabled, bool managed, bool canEnable, bool canDisable, RtssCompatibilityCode code, string diagnostic, string? exe, string? name, string? profile, bool restart = false) => new(success, enabled, managed, canEnable, canDisable, code, diagnostic, exe, name, profile, restart);
    private static RtssCompatibilityResult Fail(RtssCompatibilityCode code, string diagnostic, string? exe, string? name, string? profile = null) => Result(false, false, false, false, false, code, diagnostic, exe, name, profile);

    private sealed record ProfileCandidate(string Path);
    private sealed record LineSegment(string Content, string Ending);
    private sealed class IniAmbiguousException : Exception { public IniAmbiguousException(string message) : base(message) { } }
    private sealed class RtssManifest
    {
        public int Version { get; set; }
        public string ExecutablePath { get; set; } = string.Empty;
        public string ExecutableName { get; set; } = string.Empty;
        public string ProfilesDirectory { get; set; } = string.Empty;
        public string ProfilePath { get; set; } = string.Empty;
        public bool OriginalExisted { get; set; }
        public string OriginalBytes { get; set; } = string.Empty;
        public string OriginalHash { get; set; } = string.Empty;
        public string AppliedHash { get; set; } = string.Empty;
        [JsonConverter(typeof(JsonStringEnumConverter))] public RtssManifestPhase Phase { get; set; }
    }
}
