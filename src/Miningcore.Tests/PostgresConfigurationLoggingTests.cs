using System.Reflection;
using Autofac;
using Miningcore.Configuration;
using Newtonsoft.Json.Linq;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurePostgres_DebugLoggingDoesNotExposeConnectionSecrets(bool tls)
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
                    Host = "db.example.invalid", Port = 5433, Database = "test\nforged-line", User = "test-user",
                    Password = "database-secret-must-not-be-logged",
                    Tls = tls, TlsPassword = "certificate-secret-must-not-be-logged",
                    TlsCert = "private-certificate-path", TlsKey = "private-key-path",
                },
                new ContainerBuilder(),
            });
            factory.Flush();

            var entry = Assert.Single(target.Logs);
            const string prefix = "Using PostgreSQL persistence ";
            Assert.StartsWith(prefix, entry);
            var metadata = JObject.Parse(entry[prefix.Length..]);
            Assert.Equal(5, metadata.Count);
            Assert.Equal("db.example.invalid", metadata["Host"]?.Value<string>());
            Assert.Equal(5433, metadata["Port"]?.Value<int>());
            Assert.Equal("test\nforged-line", metadata["Database"]?.Value<string>());
            Assert.Equal("test-user", metadata["User"]?.Value<string>());
            Assert.Equal(tls ? "Require" : "DriverDefault", metadata["SslMode"]?.Value<string>());
            Assert.DoesNotContain("\n", entry);
            Assert.DoesNotContain("\r", entry);
            var output = string.Join("\n", target.Logs);
            Assert.DoesNotContain("database-secret-must-not-be-logged", output);
            Assert.DoesNotContain("certificate-secret-must-not-be-logged", output);
            Assert.DoesNotContain("Password=", output);
            Assert.DoesNotContain("private-certificate-path", output);
            Assert.DoesNotContain("private-key-path", output);
        }
        finally
        {
            loggerField.SetValue(null, previous);
        }
    }
}
