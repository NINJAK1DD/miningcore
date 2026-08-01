using System;
using System.IO;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using Xunit;

namespace Miningcore.Tests;

public class ProgramLoggingTests
{
    [Fact]
    public void CreateFileTarget_UsesBoundedNativeRotation()
    {
        const string layout = "${message}";
        var fileName = new SimpleLayout("logs/miningcore.log");

        var target = Program.CreateFileTarget("main-file", fileName, layout);

        Assert.Equal("main-file", target.Name);
        Assert.Same(fileName, target.FileName);
        Assert.Equal(FilePathKind.Unknown, target.FileNameKind);
        Assert.Equal(layout, target.Layout.ToString());
        Assert.Equal(512L * 1024L * 1024L, target.ArchiveAboveSize);
        Assert.Equal(4, target.MaxArchiveFiles);
        Assert.False(target.ArchiveOldFileOnStartup);
    }

    [Fact]
    public void CreateFileTarget_RotatesWithoutRestartAndBoundsArchives()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"miningcore-log-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var activeFile = Path.Combine(directory, "miningcore.log");
            var target = Program.CreateFileTarget("main-file", new SimpleLayout(activeFile), "${message}");
            target.ArchiveAboveSize = 256;
            target.MaxArchiveFiles = 2;

            using var factory = new LogFactory();
            var config = new LoggingConfiguration(factory);
            config.AddRuleForAllLevels(target);

            factory.Configuration = config;
            var logger = factory.GetLogger("rotation-test");

            for(var i = 0; i < 12; i++)
                logger.Info($"entry-{i:D2}-{new string('x', 240)}");

            factory.Flush();
            factory.Shutdown();

            var files = Directory.GetFiles(directory);
            var archives = files.Where(x => !string.Equals(x, activeFile,
                StringComparison.OrdinalIgnoreCase)).ToArray();

            Assert.True(File.Exists(activeFile));
            Assert.NotEmpty(archives);
            Assert.True(archives.Length <= 2);
            Assert.Contains("entry-11", File.ReadAllText(activeFile));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
