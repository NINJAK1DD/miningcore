using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Autofac;
using Miningcore.Configuration;
using Miningcore.Persistence;
using Miningcore.Persistence.Postgres;
using Newtonsoft.Json;
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
    private const string DatabasePassword = "database-secret-must-not-be-logged";
    private const string CertificatePassword = "certificate-secret-must-not-be-logged";
    private const string CertificatePath = " private-certificate-path ";
    private const string KeyPath = " private-key-path ";

    public static IEnumerable<object[]> DiagnosticCases()
    {
        foreach(var tls in new[] { false, true })
        foreach(var noValidate in new[] { false, true })
        {
            yield return new object[] { tls, noValidate, null, null, null, null,
                null, false, false, false, false };
            // Zero preserves the existing unlimited-timeout setting; this test does not endorse it.
            yield return new object[] { tls, noValidate, "", "", "", "",
                0, false, false, false, false };
            yield return new object[] { tls, noValidate, " \t ", " \t ", " \t ", " \t ",
                42, true, true, false, false };
            yield return new object[] { tls, noValidate, DatabasePassword, CertificatePassword, CertificatePath, KeyPath,
                600, true, true, true, true };
            // Independent one-field cases reject any swapped or duplicated presence-field mapping.
            yield return new object[] { tls, noValidate, DatabasePassword, null, null, null,
                null, true, false, false, false };
            yield return new object[] { tls, noValidate, null, CertificatePassword, null, null,
                null, false, true, false, false };
            yield return new object[] { tls, noValidate, null, null, CertificatePath, null,
                null, false, false, true, false };
            yield return new object[] { tls, noValidate, null, null, null, KeyPath,
                null, false, false, false, true };
        }
    }

    [Theory]
    [MemberData(nameof(DiagnosticCases))]
    public void ConfigurePostgres_DebugLoggingDoesNotExposeConnectionSecrets(bool tls, bool noValidate,
        string password, string tlsPassword, string tlsCert, string tlsKey, int? timeout,
        bool passwordConfigured, bool tlsPasswordConfigured, bool tlsCertConfigured, bool tlsKeyConfigured)
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
            var builder = new ContainerBuilder();
            configure.Invoke(null, new object[]
            {
                new PostgresConfig
                {
                    Host = "db.example.invalid", Port = 5433,
                    Database = "test\r\n\u0085\u2028\u2029forged-line", User = "test-user",
                    Password = password,
                    Tls = tls, TlsNoValidate = noValidate, CommandTimeout = timeout,
                    TlsPassword = tlsPassword, TlsCert = tlsCert, TlsKey = tlsKey,
                },
                builder,
            });
            factory.Flush();

            // Inspect the actual registered factory without opening a database connection.
            // Keep expected values independent of production defaults and diagnostic construction.
            using var container = builder.Build();
            var connectionFactory = Assert.IsType<PgConnectionFactory>(container.Resolve<IConnectionFactory>());
            var connectionField = typeof(PgConnectionFactory).GetField("connectionString",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(connectionField);
            var expectedConnection = new StringBuilder(
                $"Server=db.example.invalid;Port=5433;Database=test\r\n\u0085\u2028\u2029forged-line;User Id=test-user;Password={password};");
            if(tls)
            {
                expectedConnection.Append("SSL Mode=Require;");
                if(noValidate)
                    expectedConnection.Append("Trust Server Certificate=true;");
                if(tlsCertConfigured)
                    expectedConnection.Append($"SSL Certificate={tlsCert.Trim()};");
                if(tlsKeyConfigured)
                    expectedConnection.Append($"SSL Key={tlsKey.Trim()};");
                if(tlsPasswordConfigured)
                    expectedConnection.Append($"SSL Password={tlsPassword};");
            }
            expectedConnection.Append($"CommandTimeout={timeout ?? 300};");
            Assert.Equal(expectedConnection.ToString(), connectionField.GetValue(connectionFactory));

            const string prefix = "Using PostgreSQL persistence ";
            // Any additional diagnostic requires explicit review, even if it misses our sentinels.
            var entry = Assert.Single(target.Logs);
            Assert.StartsWith(prefix, entry);
            var metadata = JObject.Parse(entry[prefix.Length..]);
            Assert.Equal(prefix + metadata.ToString(Formatting.None), entry);
            Assert.Equal(11, metadata.Count);
            Assert.Equal("db.example.invalid", metadata["Host"]?.Value<string>());
            Assert.Equal(5433, metadata["Port"]?.Value<int>());
            Assert.Equal("test\r\n\u0085\u2028\u2029forged-line", metadata["Database"]?.Value<string>());
            Assert.Equal("test-user", metadata["User"]?.Value<string>());
            Assert.Equal(tls ? "Require" : "<unset>", metadata["SslMode"]?.Value<string>());
            Assert.Equal(noValidate, metadata["TlsNoValidate"]?.Value<bool>());
            Assert.Equal(passwordConfigured, metadata["PasswordConfigured"]?.Value<bool>());
            Assert.Equal(tlsPasswordConfigured, metadata["TlsPasswordConfigured"]?.Value<bool>());
            Assert.Equal(tlsCertConfigured, metadata["TlsCertConfigured"]?.Value<bool>());
            Assert.Equal(tlsKeyConfigured, metadata["TlsKeyConfigured"]?.Value<bool>());
            Assert.Equal(timeout ?? 300, metadata["CommandTimeout"]?.Value<int>());
            foreach(var separator in new[] { "\n", "\r", "\u0085", "\u2028", "\u2029" })
                Assert.DoesNotContain(separator, entry);
            // Inspect every captured message, not just the allowlisted diagnostic, for leaks.
            var output = string.Join("\n", target.Logs);
            Assert.DoesNotContain(DatabasePassword, output);
            Assert.DoesNotContain(CertificatePassword, output);
            Assert.DoesNotContain("Password=", output);
            Assert.DoesNotContain(CertificatePath.Trim(), output);
            Assert.DoesNotContain(KeyPath.Trim(), output);
        }
        finally
        {
            loggerField.SetValue(null, previous);
        }
    }

    [Fact]
    public void ConfigurePostgres_DiagnosticDoesNotConsultGlobalJsonDefaults()
    {
        var previous = JsonConvert.DefaultSettings;
        try
        {
            JsonConvert.DefaultSettings = () => throw new InvalidOperationException("Global defaults must not be consulted");
            ConfigurePostgres_DebugLoggingDoesNotExposeConnectionSecrets(true, true,
                DatabasePassword, CertificatePassword, CertificatePath, KeyPath, null, true, true, true, true);
        }
        finally
        {
            JsonConvert.DefaultSettings = previous;
        }
    }
}
