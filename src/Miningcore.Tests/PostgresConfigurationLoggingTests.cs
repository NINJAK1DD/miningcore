using System.Reflection;
using Autofac;
using Miningcore.Configuration;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace Miningcore.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresConfigurationLoggingCollection
{
    public const string Name = "PostgreSQL process configuration logging";
}

[Collection(PostgresConfigurationLoggingCollection.Name)]
public class PostgresConfigurationLoggingTests
{
    [Fact]
    public void ConfigurePostgres_DebugLoggingDoesNotExposeConnectionSecrets()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var loggerField = typeof(Program).GetField("logger", flags);
        var configure = typeof(Program).GetMethod("ConfigurePostgres", flags);
        Assert.NotNull(loggerField);
        Assert.NotNull(configure);
        var previous = loggerField.GetValue(null);
        using var factory = new LogFactory();
        var target = new MemoryTarget { Layout = "${message}" };
        var logging = new LoggingConfiguration(factory);
        logging.AddRuleForAllLevels(target);
        factory.Configuration = logging;

        try
        {
            loggerField.SetValue(null, factory.GetLogger("Core"));
            configure.Invoke(null, new object[]
            {
                new PostgresConfig
                {
                    Host = "127.0.0.1", Port = 5432, Database = "test", User = "test",
                    Password = "database-secret-must-not-be-logged",
                    Tls = true, TlsPassword = "certificate-secret-must-not-be-logged",
                },
                new ContainerBuilder(),
            });
            factory.Flush();

            Assert.Contains("Using PostgreSQL persistence", target.Logs);
            var output = string.Join("\n", target.Logs);
            Assert.DoesNotContain("database-secret-must-not-be-logged", output);
            Assert.DoesNotContain("certificate-secret-must-not-be-logged", output);
            Assert.DoesNotContain("Password=", output);
        }
        finally
        {
            loggerField.SetValue(null, previous);
        }
    }
}
