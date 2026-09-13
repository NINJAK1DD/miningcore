using System;
using System.Collections.Generic;
using System.Reflection;
using Autofac;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Postgres;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Npgsql;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace Miningcore.Tests;

// xUnit runs this collection separately from parallel-capable collections as well:
// v2.4.2 XunitTestAssemblyRunner.RunTestCollectionsAsync awaits all parallel tasks first,
// then executes non-parallel collections. This protects Program.logger and JsonConvert defaults.
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
    public void ConfigurePostgres_ValidatesLegacyPolicyAndSafeDiagnosticContracts(bool tls, bool noValidate,
        string password, string tlsPassword, string tlsCert, string tlsKey, int? timeout,
        bool passwordConfigured, bool tlsPasswordConfigured, bool tlsCertConfigured, bool tlsKeyConfigured) =>
        ConfigureAndVerify(tls, noValidate, password, tlsPassword, tlsCert, tlsKey, timeout,
            passwordConfigured, tlsPasswordConfigured, tlsCertConfigured, tlsKeyConfigured);

    private static (string ConnectionString, string Diagnostic) ConfigureAndVerify(bool tls, bool noValidate,
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
            Action configureAction = () => configure.Invoke(null, new object[]
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
            // Issue #146 deliberately rejects options which the former raw-string
            // contract silently ignored. Rejected settings must not reach logging or DI.
            if((tlsCert?.Length > 0 && string.IsNullOrWhiteSpace(tlsCert)) ||
               (tlsKey?.Length > 0 && string.IsNullOrWhiteSpace(tlsKey)) ||
               (!tls && (noValidate || tlsPasswordConfigured || tlsCertConfigured || tlsKeyConfigured)))
            {
                var error = Assert.Throws<TargetInvocationException>(configureAction);
                Assert.IsType<PoolStartupException>(error.InnerException);
                Assert.Empty(target.Logs);
                return (null, null);
            }
            configureAction();
            factory.Flush();

            // Inspect the actual registered factory without opening a database connection.
            // Keep expected values independent of production defaults and diagnostic construction.
            using var container = builder.Build();
            var connectionFactory = Assert.IsType<PgConnectionFactory>(container.Resolve<IConnectionFactory>());
            var connectionField = typeof(PgConnectionFactory).GetField("connectionString",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(connectionField);
            var actualConnection = Assert.IsType<string>(connectionField.GetValue(connectionFactory));
            // Compare literal parsed values, not legacy keyword spelling/order or
            // unescaped fragments. Typed construction intentionally changes normalization.
            var connection = new NpgsqlConnectionStringBuilder(actualConnection);
            Assert.Equal("db.example.invalid", connection.Host);
            Assert.Equal(5433, connection.Port);
            Assert.Equal("test\r\n\u0085\u2028\u2029forged-line", connection.Database);
            Assert.Equal("test-user", connection.Username);
            Assert.Equal(string.IsNullOrEmpty(password) ? null : password, connection.Password);
            Assert.Equal(!string.IsNullOrEmpty(password), connection.ShouldSerialize("Password"));
            Assert.Equal(tls ? SslMode.Require : SslMode.Prefer, connection.SslMode);
            Assert.DoesNotContain("Trust Server Certificate", actualConnection);
            Assert.Equal(tls && tlsCertConfigured ? tlsCert.Trim() : null, connection.SslCertificate);
            Assert.Equal(tls && tlsKeyConfigured ? tlsKey.Trim() : null, connection.SslKey);
            Assert.Equal(tls && tlsPasswordConfigured ? tlsPassword : null, connection.SslPassword);
            Assert.Equal(timeout ?? 300, connection.CommandTimeout);

            const string prefix = "Using PostgreSQL persistence ";
            // Any additional diagnostic requires explicit review, even if it misses our sentinels.
            var entry = Assert.Single(target.Logs);
            Assert.StartsWith(prefix, entry);
            var metadata = JObject.Parse(entry[prefix.Length..]);
            Assert.Equal(prefix + metadata.ToString(Formatting.None), entry);
            Assert.Equal(9, metadata.Count);
            Assert.Equal(5433, metadata["Port"]?.Value<int>());
            Assert.Equal(tls ? "Require" : "Prefer", metadata["SslMode"]?.Value<string>());
            Assert.Equal(noValidate, metadata["TlsNoValidate"]?.Value<bool>());
            Assert.Equal(passwordConfigured, metadata["PasswordConfigured"]?.Value<bool>());
            Assert.Equal(tlsPasswordConfigured, metadata["TlsPasswordConfigured"]?.Value<bool>());
            Assert.Equal(tlsCertConfigured, metadata["TlsCertConfigured"]?.Value<bool>());
            Assert.Equal(tlsKeyConfigured, metadata["TlsKeyConfigured"]?.Value<bool>());
            Assert.False(metadata["RootCertificatePathConfigured"]?.Value<bool>());
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
            return (actualConnection, entry);
        }
        finally
        {
            loggerField.SetValue(null, previous);
        }
    }

    [Fact]
    public void ConfigurePostgres_FullyConfiguredTlsMatchesGoldenConnectionAndDiagnostic()
    {
        // Unconditional references, independent of the theory data and reconstruction logic.
        var actual = ConfigureAndVerify(true, true, DatabasePassword, CertificatePassword,
            CertificatePath, KeyPath, 600, true, true, true, true);
        const string goldenConnection = "Server=db.example.invalid;Port=5433;Database=test\r\n\u0085\u2028\u2029forged-line;" +
            "User Id=test-user;Password=database-secret-must-not-be-logged;SSL Mode=Require;" +
            "SSL Certificate=private-certificate-path;SSL Key=private-key-path;" +
            "SSL Password=certificate-secret-must-not-be-logged;CommandTimeout=600;";
        const string goldenDiagnostic = "Using PostgreSQL persistence {\"Port\":5433,\"SslMode\":\"Require\"," +
            "\"TlsNoValidate\":true,\"PasswordConfigured\":true,\"TlsCertConfigured\":true,\"TlsKeyConfigured\":true," +
            "\"TlsPasswordConfigured\":true,\"RootCertificatePathConfigured\":false,\"CommandTimeout\":600}";
        Assert.True(new NpgsqlConnectionStringBuilder(goldenConnection).EquivalentTo(
            new NpgsqlConnectionStringBuilder(actual.ConnectionString)));
        Assert.Equal(goldenDiagnostic, actual.Diagnostic);
    }

    [Fact]
    public void ConfigurePostgres_DiagnosticIgnoresGlobalJsonFormattingAndNaming()
    {
        var previous = JsonConvert.DefaultSettings;
        try
        {
            // Valid application-wide settings may be used by unrelated dependencies; only the
            // diagnostic must retain its own PascalCase, compact format and complete field set.
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Formatting = Formatting.Indented,
                DefaultValueHandling = DefaultValueHandling.Ignore,
            };
            var control = JsonConvert.SerializeObject(new { ExampleField = true });
            Assert.Contains("\"exampleField\"", control);
            Assert.Contains("\n", control);
            Assert.Empty(JObject.Parse(JsonConvert.SerializeObject(new { Flag = false })));
            // False presence flags must survive in the isolated diagnostic despite the active
            // global default-value omission policy. Do not rely only on the all-true TLS case.
            ConfigureAndVerify(false, false, DatabasePassword, null, null, null,
                null, true, false, false, false);
        }
        finally
        {
            JsonConvert.DefaultSettings = previous;
        }
    }
}
