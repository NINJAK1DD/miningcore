using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autofac;
using Miningcore.Configuration;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace Miningcore.Tests;

// xUnit runs this collection separately from parallel-capable collections as well:
// temporarily replacing Program.logger must not overlap another test's use of it.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresConfigurationLoggingCollection
{
    public const string Name = "PostgreSQL process configuration logging";
}

[Collection(PostgresConfigurationLoggingCollection.Name)]
public class PostgresConfigurationLoggingTests
{
    public static IEnumerable<object[]> DiagnosticCases()
    {
        foreach(var tls in new[] { false, true })
        foreach(var noValidate in new[] { false, true })
        {
            yield return new object[] { tls, noValidate, null, null, false, false };
            yield return new object[] { tls, noValidate, "", 0, false, false };
            yield return new object[] { tls, noValidate, " \t ", 42, true, false };
            yield return new object[] { tls, noValidate, "configured", 600, true, true };
        }
    }

    [Theory]
    [MemberData(nameof(DiagnosticCases))]
    public void ConfigurePostgres_DebugLoggingDoesNotExposeConnectionSecrets(bool tls, bool noValidate,
        string credentialState, int? timeout, bool passwordConfigured, bool pathConfigured)
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
                    Host = "db.example.invalid", Port = 5433,
                    Database = "test\r\n\u0085\u2028\u2029forged-line", User = "test-user",
                    Password = pathConfigured ? "database-secret-must-not-be-logged" : credentialState,
                    Tls = tls, TlsNoValidate = noValidate, CommandTimeout = timeout,
                    TlsPassword = pathConfigured ? "certificate-secret-must-not-be-logged" : credentialState,
                    TlsCert = pathConfigured ? " private-certificate-path " : credentialState,
                    TlsKey = pathConfigured ? " private-key-path " : credentialState,
                },
                new ContainerBuilder(),
            });
            factory.Flush();

            const string prefix = "Using PostgreSQL persistence ";
            var entry = Assert.Single(target.Logs.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)));
            var metadata = JObject.Parse(entry[prefix.Length..]);
            Assert.Equal(11, metadata.Count);
            Assert.Equal("db.example.invalid", metadata["Host"]?.Value<string>());
            Assert.Equal(5433, metadata["Port"]?.Value<int>());
            Assert.Equal("test\r\n\u0085\u2028\u2029forged-line", metadata["Database"]?.Value<string>());
            Assert.Equal("test-user", metadata["User"]?.Value<string>());
            Assert.Equal(tls ? "Require" : "<unset>", metadata["SslMode"]?.Value<string>());
            Assert.Equal(noValidate, metadata["TlsNoValidate"]?.Value<bool>());
            Assert.Equal(passwordConfigured, metadata["PasswordConfigured"]?.Value<bool>());
            Assert.Equal(passwordConfigured, metadata["TlsPasswordConfigured"]?.Value<bool>());
            Assert.Equal(pathConfigured, metadata["TlsCertConfigured"]?.Value<bool>());
            Assert.Equal(pathConfigured, metadata["TlsKeyConfigured"]?.Value<bool>());
            Assert.Equal(timeout ?? 300, metadata["CommandTimeout"]?.Value<int>());
            foreach(var separator in new[] { "\n", "\r", "\u0085", "\u2028", "\u2029" })
                Assert.DoesNotContain(separator, entry);
            // Inspect every captured message, not just the allowlisted diagnostic, for leaks.
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
