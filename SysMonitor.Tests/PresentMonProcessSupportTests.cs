using System.Diagnostics;
using System.Security.Cryptography;
using SysMonitor.Models;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class PresentMonProcessSupportTests
{
    [Fact]
    public void CollectorArgumentsAreExactAndUnquotedArgumentListEntries()
    {
        var startInfo = PresentMonProcessSupport.CreateCollectorStartInfo(
            "C:\\runtime\\PresentMon.exe",
            1234,
            "SysMonitor-owned");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(
            new[]
            {
                "--process_id", "1234",
                "--session_name", "SysMonitor-owned",
                "--output_stdout",
                "--v1_metrics",
                "--no_track_gpu",
                "--no_track_input",
                "--no_track_display",
                "--terminate_on_proc_exit",
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public void TerminationArgumentsCanOnlyNameTheOwnedSession()
    {
        var startInfo = PresentMonProcessSupport.CreateTerminateStartInfo(
            "C:\\runtime\\PresentMon.exe",
            "SysMonitor-owned");

        Assert.Equal(
            new[] { "--session_name", "SysMonitor-owned", "--terminate_existing_session" },
            startInfo.ArgumentList);
    }

    [Fact]
    public void CollectorDoesNotRequestSelfElevationByDefault()
    {
        var startInfo = PresentMonProcessSupport.CreateCollectorStartInfo(
            "C:\\runtime\\PresentMon.exe",
            1234,
            "SysMonitor-owned");

        Assert.DoesNotContain("--restart_as_admin", startInfo.ArgumentList);
    }

    [Fact]
    public void ElevatedHelperIsHiddenAndReceivesOnlyValidatedIdentifiers()
    {
        var startInfo = PresentMonProcessSupport.CreateElevatedHelperStartInfo(
            "C:\\runtime\\SysMonitor.exe",
            "SysMonitor.PresentMon.0123456789abcdef0123456789abcdef",
            1234,
            "SysMonitor-123-0123456789abcdef0123456789abcdef");

        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.Equal(
            new[]
            {
                "--presentmon-helper",
                "SysMonitor.PresentMon.0123456789abcdef0123456789abcdef",
                "1234",
                "SysMonitor-123-0123456789abcdef0123456789abcdef"
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public void PresentMonHelperRequestRejectsUntrustedArguments()
    {
        string pipe = "SysMonitor.PresentMon.0123456789abcdef0123456789abcdef";
        string session = "SysMonitor-123-0123456789abcdef0123456789abcdef";
        Assert.True(PresentMonHelperHost.TryGetRequest(
            new[] { "--presentmon-helper", pipe, "1234", session },
            out string parsedPipe,
            out int processId,
            out string parsedSession));
        Assert.Equal(pipe, parsedPipe);
        Assert.Equal(1234, processId);
        Assert.Equal(session, parsedSession);

        Assert.False(PresentMonHelperHost.TryGetRequest(
            new[] { "--presentmon-helper", "bad", "1234", session },
            out _, out _, out _));
    }

    [Fact]
    public async Task DiagnosticCaptureIsBoundedButDrainsInput()
    {
        string input = new('x', PresentMonProcessSupport.MaximumDiagnosticCharacters + 5000);
        string result = await PresentMonProcessSupport.CaptureBoundedAsync(
            new StringReader(input),
            CancellationToken.None);

        Assert.Equal(PresentMonProcessSupport.MaximumDiagnosticCharacters, result.Length);
    }

    [Fact]
    public void EmbeddedBinaryHasPinnedHash()
    {
        using Stream stream = typeof(PresentMonBinaryManager).Assembly
            .GetManifestResourceStream(PresentMonBinaryManager.ResourceName)!;
        Assert.NotNull(stream);
        Assert.Equal(PresentMonBinaryManager.Sha256, Convert.ToHexString(SHA256.HashData(stream)));
    }

    [Fact]
    public async Task MissingTargetAndStopAreTruthfulAndIdempotent()
    {
        await using var provider = new PresentMonFrameRateProvider();
        await provider.StartAsync(int.MaxValue);
        Assert.Equal(FrameRateStatus.NoTarget, provider.Latest.Status);
        Assert.Null(provider.Latest.PresentFps);

        await provider.StopAsync();
        await provider.StopAsync();
        Assert.Equal(FrameRateStatus.Disabled, provider.Latest.Status);
    }

    [Theory]
    [InlineData("SysMonitor-123-0123456789abcdef0123456789abcdef", true)]
    [InlineData("PresentMon", false)]
    [InlineData("SysMonitor-owned", false)]
    [InlineData("SysMonitor-0-0123456789abcdef0123456789abcdef", false)]
    [InlineData("SysMonitor-123-not-a-guid", false)]
    public void PersistedSessionCleanupAcceptsOnlyOwnedNames(string value, bool expected)
    {
        Assert.Equal(expected, PresentMonSessionState.IsOwnedName(value));
    }
}
