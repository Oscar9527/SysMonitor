using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class ProcessExecutablePathResolverTests
{
    [Fact]
    public void ResolvesCurrentProcessToExistingExecutable()
    {
        string? path = ProcessExecutablePathResolver.TryResolve(Environment.ProcessId);

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsInvalidProcessIdentifiers(int processId)
    {
        Assert.Null(ProcessExecutablePathResolver.TryResolve(processId));
    }
}
