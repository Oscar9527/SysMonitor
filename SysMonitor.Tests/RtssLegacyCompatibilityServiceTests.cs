using System;
using System.IO;
using System.Linq;
using System.Text;
using SysMonitor.Services;
using Xunit;

namespace SysMonitor.Tests;

public sealed class RtssLegacyCompatibilityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rtss-service-" + Guid.NewGuid().ToString("N"));
    private readonly string _profiles;
    private readonly string _backup;
    private readonly string _exe;
    private readonly RtssLegacyCompatibilityService _service;

    public RtssLegacyCompatibilityServiceTests()
    {
        _profiles = Path.Combine(_root, "Profiles");
        _backup = Path.Combine(_root, "Backup");
        Directory.CreateDirectory(_profiles);
        Directory.CreateDirectory(_backup);
        _exe = Path.Combine(_root, "Game.exe");
        File.WriteAllBytes(_exe, new byte[] { 0x4d, 0x5a });
        _service = new RtssLegacyCompatibilityService(_profiles, _backup);
    }

    [Fact]
    public void NewProfileUsesExactAsciiCrLfBytes()
    {
        var result = _service.Enable(_exe);
        Assert.True(result.Success);
        Assert.Equal(Encoding.ASCII.GetBytes("[Hooking]\r\nEnableHooking=1\r\nHookDirectDraw=1\r\n"), File.ReadAllBytes(Path.Combine(_profiles, "Game.exe.cfg")));
    }

    [Fact]
    public void SameBasenameAtAnotherCanonicalPathIsRefused()
    {
        Assert.True(_service.Enable(_exe).Success);
        var otherDirectory = Path.Combine(_root, "OtherGame");
        Directory.CreateDirectory(otherDirectory);
        var otherExe = Path.Combine(otherDirectory, "Game.exe");
        File.WriteAllBytes(otherExe, new byte[] { 0x4d, 0x5a, 0x01 });
        var result = _service.Enable(otherExe);
        Assert.Equal(RtssCompatibilityCode.SameBasenameConflict, result.Code);
        Assert.Equal("[Hooking]\r\nEnableHooking=1\r\nHookDirectDraw=1\r\n", File.ReadAllText(Path.Combine(_profiles, "Game.exe.cfg")));
    }

    [Fact]
    public void QueryDoesNotCreateBackupDirectory()
    {
        Directory.Delete(_backup, true);
        Assert.False(Directory.Exists(_backup));
        var result = _service.Query(_exe);
        Assert.True(result.Success);
        Assert.False(Directory.Exists(_backup));
    }

    [Fact]
    public void ExistingProfilePreservesUnrelatedBytesAndRestoresExactly()
    {
        var original = Encoding.UTF8.GetBytes("; keep\n[Hooking]\nEnableHooking=0\nHookDirectDraw=0\nOther=abc\n[Other]\nX=1\n");
        var path = Path.Combine(_profiles, "Game.exe.cfg");
        File.WriteAllBytes(path, original);
        Assert.True(_service.Enable(_exe).Success);
        Assert.Contains("Other=abc", File.ReadAllText(path));
        Assert.True(_service.Disable(_exe).Success);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void GlobalAndConfigAreNotTouched()
    {
        File.WriteAllText(Path.Combine(_profiles, "Global.cfg"), "[Hooking]\r\nEnableHooking=0\r\n");
        File.WriteAllText(Path.Combine(_profiles, "Config.cfg"), "[Hooking]\r\nEnableHooking=0\r\n");
        var result = _service.Enable(_exe);
        Assert.True(result.Success);
        Assert.Contains("EnableHooking=0", File.ReadAllText(Path.Combine(_profiles, "Global.cfg")));
        Assert.Contains("EnableHooking=0", File.ReadAllText(Path.Combine(_profiles, "Config.cfg")));
    }

    [Fact]
    public void ModifiedProfileProducesConflictAndRetainsManifest()
    {
        Assert.True(_service.Enable(_exe).Success);
        var profile = Path.Combine(_profiles, "Game.exe.cfg");
        File.AppendAllText(profile, "changed\r\n");
        var result = _service.Disable(_exe);
        Assert.False(result.Success);
        Assert.Equal(RtssCompatibilityCode.Conflict, result.Code);
        Assert.NotEmpty(Directory.GetFiles(_backup, "*.json"));
    }

    [Fact]
    public void MissingAppliedProfileProducesConflict()
    {
        Assert.True(_service.Enable(_exe).Success);
        File.Delete(Path.Combine(_profiles, "Game.exe.cfg"));
        var result = _service.Disable(_exe);
        Assert.Equal(RtssCompatibilityCode.Conflict, result.Code);
        Assert.NotEmpty(Directory.GetFiles(_backup, "*.json"));
    }

    [Fact]
    public void ReenableAfterExternalModificationConflictsWithoutOverwrite()
    {
        Assert.True(_service.Enable(_exe).Success);
        var profile = Path.Combine(_profiles, "Game.exe.cfg");
        var changed = Encoding.UTF8.GetBytes("externally changed\r\n");
        File.WriteAllBytes(profile, changed);
        var result = _service.Enable(_exe);
        Assert.Equal(RtssCompatibilityCode.Conflict, result.Code);
        Assert.Equal(changed, File.ReadAllBytes(profile));
    }

    [Fact]
    public void IdempotentEnableAndDeletedExecutableDisable()
    {
        Assert.True(_service.Enable(_exe).Success);
        Assert.True(_service.Enable(_exe).Success);
        File.Delete(_exe);
        var result = _service.Disable(_exe);
        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(_profiles, "Game.exe.cfg")));
    }

    [Fact]
    public void DuplicateHookingIsRejectedWithoutWrite()
    {
        var profile = Path.Combine(_profiles, "Game.exe.cfg");
        var original = "[Hooking]\r\nEnableHooking=0\r\n[Hooking]\r\nHookDirectDraw=0\r\n";
        File.WriteAllText(profile, original);
        var result = _service.Enable(_exe);
        Assert.Equal(RtssCompatibilityCode.AmbiguousIni, result.Code);
        Assert.Equal(original, File.ReadAllText(profile));
    }

    [Fact]
    public void ManagedEnumerationIncludesAppliedTarget()
    {
        Assert.True(_service.Enable(_exe).Success);
        var target = Assert.Single(_service.EnumerateManagedTargets());
        Assert.True(target.Managed);
        Assert.True(target.Enabled);
    }

    [Fact]
    public void PendingAppliedIsFinalizedAndRequiredOriginalMissingConflicts()
    {
        var profile = Path.Combine(_profiles, "Game.exe.cfg");
        File.WriteAllText(profile, "[Hooking]\r\nEnableHooking=0\r\nHookDirectDraw=0\r\n");
        Assert.True(_service.Enable(_exe).Success);
        var manifest = Directory.GetFiles(_backup, "*.json").Single();
        var json = File.ReadAllText(manifest).Replace("\"phase\": \"Applied\"", "\"phase\": \"Pending\"", StringComparison.Ordinal);
        File.WriteAllText(manifest, json);
        // The applied bytes are still present, so reconciliation finalizes Applied.
        Assert.True(_service.Enable(_exe).Success);
        json = File.ReadAllText(manifest).Replace("\"phase\": \"Applied\"", "\"phase\": \"Pending\"", StringComparison.Ordinal);
        File.WriteAllText(manifest, json);
        File.Delete(profile);
        var result = _service.Enable(_exe);
        Assert.Equal(RtssCompatibilityCode.Conflict, result.Code);
        Assert.True(File.Exists(manifest));

        var thirdState = Encoding.UTF8.GetBytes("externally changed\r\n");
        File.WriteAllBytes(profile, thirdState);
        var thirdResult = _service.Enable(_exe);
        Assert.Equal(RtssCompatibilityCode.Conflict, thirdResult.Code);
        Assert.Equal(thirdState, File.ReadAllBytes(profile));
        Assert.True(File.Exists(manifest));
    }

    [Fact]
    public void PendingOriginalAbsentAndMissingProfileIsCleanedUpBeforeReapply()
    {
        Assert.True(_service.Enable(_exe).Success);
        var profile = Path.Combine(_profiles, "Game.exe.cfg");
        var manifest = Directory.GetFiles(_backup, "*.json").Single();
        var json = File.ReadAllText(manifest).Replace("\"phase\": \"Applied\"", "\"phase\": \"Pending\"", StringComparison.Ordinal);
        File.WriteAllText(manifest, json);
        File.Delete(profile);
        var result = _service.Enable(_exe);
        Assert.True(result.Success);
        Assert.True(File.Exists(profile));
        Assert.Single(Directory.GetFiles(_backup, "*.json"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
