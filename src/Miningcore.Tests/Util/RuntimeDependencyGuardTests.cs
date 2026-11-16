using System;
using System.IO;
using System.Threading.Tasks;
using Miningcore.Mining;
using Miningcore.Util;
using Xunit;

namespace Miningcore.Tests.Util;

public class RuntimeDependencyGuardTests
{
    [Theory]
    [InlineData(0, 78)]
    [InlineData(1, 78)]
    [InlineData(74, 74)]
    public async Task BadManagedImageRequiresOperatorWithoutMaskingDurabilityLoss(int previous, int expected)
    {
        var status = new ProcessStatus();
        if(previous != 0)
            status.MarkFailed(previous);
        Exception reported = null;
        var exit = await Program.RunStartupBoundaryAsync(() =>
        {
            Program.ValidateRuntimeDependencies(status, () => throw new BadImageFormatException("invalid test image"));
            return Task.CompletedTask;
        }, error =>
        {
            reported = error;
            return Task.CompletedTask;
        }, () => status.ExitCode);
        Assert.IsType<PoolStartupException>(reported);
        Assert.Contains("Runtime dependency validation failed", reported.Message);
        Assert.Equal(expected, exit);
    }

    [Fact]
    public void RuntimeBoundaryDoesNotReclassifyUnexpectedFailures()
    {
        var status = new ProcessStatus();
        var failure = new IOException("unrelated test failure");
        Assert.Same(failure, Assert.Throws<IOException>(() =>
            Program.ValidateRuntimeDependencies(status, () => throw failure)));
        Assert.Equal(0, status.ExitCode);
    }

    [Theory]
    [InlineData("10.0.14")]
    [InlineData("9.0.99")]
    [InlineData("11.0.0")]
    public void RejectsUnsupportedFramework(string actual)
    {
        Assert.Throws<InvalidOperationException>(() => RuntimeDependencyGuard.RequireVersion(
            "Microsoft.AspNetCore.App", Version.Parse(actual), new Version(10, 0, 15)));
    }

    [Theory]
    [InlineData("10.0.15", "package", "app", true)]
    [InlineData("10.0.16", "package", "framework", true)]
    [InlineData("10.0.15", "package", "framework", true)]
    [InlineData("10.0.14", "package", "app", false)]
    [InlineData("10.0.16", "package", "app", false)]
    [InlineData("10.0.15", "framework", "framework", true)]
    [InlineData("10.0.15", "framework", "app", false)]
    [InlineData("10.0.16", "framework", "other", false)]
    public void ChecksActualBinding(string actual, string mode, string location, bool accepted)
    {
        var root = Path.GetTempPath();
        var error = Record.Exception(() => RuntimeDependencyGuard.ValidateBinding(Version.Parse(actual),
            new Version(10, 0, 15), mode, Path.Combine(root, location),
            Path.Combine(root, "app"), Path.Combine(root, "framework")));
        if(accepted)
            Assert.Null(error);
        else
            Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void WindowsPathsUseFilesystemCasingRules()
    {
        var path = Path.Combine(Path.GetTempPath(), "RuntimeGuardCase");
        Assert.Equal(OperatingSystem.IsWindows(), RuntimeDependencyGuard.SameDirectory(path, path.ToUpperInvariant()));
        Assert.True(RuntimeDependencyGuard.SameDirectory(path, path + Path.DirectorySeparatorChar));
    }
}
