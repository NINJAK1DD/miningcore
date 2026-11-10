using System.Linq;
using Miningcore.Configuration;
using Miningcore.Payments;
using NLog;
using NLog.Targets;
using Xunit;

namespace Miningcore.Tests;

public class RecoveryLoggingTests
{
    [Theory]
    [InlineData("info", false)]
    [InlineData("info", true)]
    [InlineData("error", false)]
    [InlineData("error", true)]
    [InlineData("fatal", false)]
    [InlineData("fatal", true)]
    [InlineData("off", false)]
    [InlineData("off", true)]
    public void RecoveryAuditReachesConsoleExactlyOnceRegardlessOfServiceThreshold(string level, bool colors)
    {
        var config = Program.CreateLoggingConfiguration(new ClusterLoggingConfig
        {
            Level = level,
            EnableConsoleColors = colors,
            EnableConsoleLog = false,
            LogFile = "service.log",
            ApiLogFile = "api.log",
            PerPoolLogFile = true,
        }, true, new[] { new PoolConfig { Id = "pool" } });
        Assert.DoesNotContain(config.AllTargets, target => target is FileTarget);
        var console = config.FindTargetByName("console");
        Assert.NotNull(console);
        // Exercise NLog's real filtering without redirecting the process-global console.
        var capture = new MemoryTarget("capture") { Layout = "${message}" };
        config.AddTarget(capture);
        foreach(var rule in config.LoggingRules.Where(rule => rule.Targets.Contains(console)))
        {
            rule.Targets.Remove(console);
            rule.Targets.Add(capture);
        }
        using var factory = new LogFactory(config);
        var audit = factory.GetLogger(typeof(PayoutOwnershipRecovery).FullName);
        audit.Warn("release receipt");
        audit.Error("audit failure");
        factory.GetLogger("OtherCategory").Info("ordinary info");
        factory.Flush();
        Assert.Equal(1, capture.Logs.Count(message => message == "release receipt"));
        Assert.Equal(1, capture.Logs.Count(message => message == "audit failure"));
        Assert.Equal(level == "info", capture.Logs.Contains("ordinary info"));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("fatal")]
    [InlineData("off")]
    public void ServiceModeRetainsConfiguredAuditThreshold(string level)
    {
        var config = Program.CreateLoggingConfiguration(new ClusterLoggingConfig
        {
            Level = level, EnableConsoleLog = true,
        }, false, null);
        Assert.DoesNotContain(config.LoggingRules, rule =>
            rule.NameMatches(typeof(PayoutOwnershipRecovery).FullName) && rule.IsLoggingEnabledForLevel(LogLevel.Warn));
    }
}
