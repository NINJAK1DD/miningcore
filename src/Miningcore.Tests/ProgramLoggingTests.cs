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
            var archiveContents = archives.Select(File.ReadAllText).ToArray();

            Assert.True(File.Exists(activeFile));
            Assert.Equal(2, archives.Length);
            Assert.Contains(archiveContents, x => x.Contains("entry-09"));
            Assert.Contains(archiveContents, x => x.Contains("entry-10"));
            Assert.Contains("entry-11", File.ReadAllText(activeFile));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CreateFileTarget_RestartContinuesExistingActiveFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"miningcore-log-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var activeFile = Path.Combine(directory, "miningcore.log");

            WriteRecordSession(activeFile, "before-restart");
            var lengthBeforeRestart = new FileInfo(activeFile).Length;

            WriteRecordSession(activeFile, "after-restart");

            var files = Directory.GetFiles(directory);
            var contents = File.ReadAllText(activeFile);

            Assert.Single(files);
            Assert.True(string.Equals(activeFile, files[0], StringComparison.OrdinalIgnoreCase));
            Assert.True(new FileInfo(activeFile).Length > lengthBeforeRestart);
            Assert.Contains("before-restart", contents);
            Assert.Contains("after-restart", contents);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void PackagedSystemdUnit_DoesNotRestartDurabilityLossExitStatus()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while(directory != null &&
            !File.Exists(Path.Combine(directory.FullName, "packaging", "systemd",
                "miningcore.service")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var unit = File.ReadAllText(Path.Combine(directory.FullName, "packaging",
            "systemd", "miningcore.service"));

        Assert.Contains("Restart=on-failure", unit);
        Assert.Contains(
            $"RestartPreventExitStatus={ProcessExitCodes.UnreconciledShareDurabilityLoss}",
            unit);
        Assert.Contains("TimeoutStopSec=90", unit);
    }

    [Fact]
    public void ProcessStatus_DurabilityLossExitCodeCannotBeDowngraded()
    {
        var status = new ProcessStatus();

        status.MarkFailed(ProcessExitCodes.UnreconciledShareDurabilityLoss);
        status.MarkFailed();

        Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
            status.ExitCode);
    }

    private static void WriteRecordSession(string activeFile, string record)
    {
        using var factory = new LogFactory();
        var target = Program.CreateFileTarget("main-file", new SimpleLayout(activeFile), "${message}");
        var config = new LoggingConfiguration(factory);
        config.AddRuleForAllLevels(target);

        factory.Configuration = config;
        factory.GetLogger("restart-test").Info(record);
        factory.Flush();
        factory.Shutdown();
    }
}
