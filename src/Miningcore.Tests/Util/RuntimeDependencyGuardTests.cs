using System;
using System.IO;
using Miningcore.Util;
using Xunit;

namespace Miningcore.Tests.Util;

public class RuntimeDependencyGuardTests
{
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
