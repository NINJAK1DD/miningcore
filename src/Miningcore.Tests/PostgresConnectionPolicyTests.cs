using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autofac;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Postgres;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using NLog;
using NLog.Config;
using NLog.Targets;
using Npgsql;
using Xunit;

namespace Miningcore.Tests;

// xUnit 2.4.2 awaits every parallel collection, then runs disabled collections one
// at a time (XunitTestAssemblyRunner.RunTestCollectionsAsync). This isolates all
// process-wide state here from the logging collections as well as ordinary tests.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresPolicyCollection
{
    public const string Name = "PostgreSQL TLS policy";
}

[Collection(PostgresPolicyCollection.Name)]
public class PostgresConnectionPolicyTests
{
    internal static PostgresConfig Config() => new()
    {
        Host = "localhost", Port = 5432, Database = "miningcore", User = "miningcore",
    };

    [Theory]
    [InlineData(null, null, SslMode.Prefer)]
    [InlineData(false, false, SslMode.Prefer)]
    [InlineData(true, false, SslMode.Require)]
    [InlineData(true, true, SslMode.Require)]
    public void LegacyPolicyIsPreserved(bool? tls, bool? noValidate, SslMode expected)
    {
        var config = Config();
        config.Tls = tls;
        config.TlsNoValidate = noValidate;
        var connection = PostgresConnectionPolicy.Build(config, "ignored-legacy-environment-ca");
        Assert.Equal(expected, connection.SslMode);
        Assert.Null(connection.RootCertificate);
        Assert.DoesNotContain("Trust Server Certificate", connection.ConnectionString);
        Assert.Equal(300, connection.CommandTimeout);
    }

    [Theory]
    [InlineData(PostgresSslMode.Disable, SslMode.Disable)]
    [InlineData(PostgresSslMode.Allow, SslMode.Allow)]
    [InlineData(PostgresSslMode.Prefer, SslMode.Prefer)]
    [InlineData(PostgresSslMode.Require, SslMode.Require)]
    [InlineData(PostgresSslMode.VerifyCA, SslMode.VerifyCA)]
    [InlineData(PostgresSslMode.VerifyFull, SslMode.VerifyFull)]
    public void ExplicitModesRoundTrip(PostgresSslMode mode, SslMode expected)
    {
        var config = Config();
        config.SslMode = mode;
        var connection = new NpgsqlConnectionStringBuilder(PostgresConnectionPolicy.Build(config, null).ConnectionString);
        Assert.Equal(expected, connection.SslMode);
        Assert.Equal(mode, JsonConvert.DeserializeObject<PostgresConfig>(
            JsonConvert.SerializeObject(config)).SslMode);
    }

    [Theory]
    [InlineData("\"bogus-secret\"")]
    [InlineData("\"verifyfull\"")]
    [InlineData("123")]
    [InlineData("\"4\"")]
    [InlineData("\"VerifyCA, VerifyFull\"")]
    [InlineData("\" VerifyFull \"")]
    [InlineData("true")]
    [InlineData("{}")]
    public void MalformedEnumsFailWithoutEcho(string value)
    {
        var error = Assert.Throws<JsonSerializationException>(() =>
            JsonConvert.DeserializeObject<PostgresConfig>("{\"sslMode\":" + value + "}"));
        Assert.DoesNotContain("bogus-secret", error.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitModeRejectsEitherLegacyFlagEvenFalse(bool value)
    {
        foreach(var mode in Enum.GetValues<PostgresSslMode>())
        {
            var config = Config();
            config.SslMode = mode;
            config.Tls = value;
            Assert.Throws<PoolStartupException>(() => PostgresConnectionPolicy.Build(config, null));
            config.Tls = null;
            config.TlsNoValidate = value;
            Assert.Throws<PoolStartupException>(() => PostgresConnectionPolicy.Build(config, null));
        }
    }

    [Fact]
    public void InvalidPoliciesFailClosed()
    {
        var changes = new Action<PostgresConfig>[]
        {
            c => c.SslMode = (PostgresSslMode) 123,
            c => c.TlsNoValidate = true,
            c => c.TlsRootCert = "secret-path",
            c => { c.SslMode = PostgresSslMode.VerifyCA; c.TlsRootCert = " "; },
            c => c.TlsCert = "secret-path",
            c => c.TlsKey = "secret-path",
            c => c.TlsPassword = "secret-password",
            c => c.CommandTimeout = -1,
            c => c.Port = 65536,
        };
        foreach(var change in changes)
        {
            var config = Config();
            change(config);
            var error = Assert.Throws<PoolStartupException>(() => PostgresConnectionPolicy.Build(config, null));
            Assert.DoesNotContain("secret-", error.ToString());
        }
    }

    [Fact]
    public void CaPrecedenceIsExplicitAndDoesNotChangeMode()
    {
        var config = Config();
        config.SslMode = PostgresSslMode.VerifyFull;
        Assert.Equal("environment-ca", PostgresConnectionPolicy.Build(config, "environment-ca").RootCertificate);
        config.TlsRootCert = " explicit-ca ";
        var connection = PostgresConnectionPolicy.Build(config, "environment-ca");
        Assert.Equal(" explicit-ca ", connection.RootCertificate);
        Assert.Equal(SslMode.VerifyFull, connection.SslMode);
        config.TlsRootCert = null;
        Assert.Null(PostgresConnectionPolicy.Build(config, null).RootCertificate);
        Assert.Throws<PoolStartupException>(() => PostgresConnectionPolicy.Build(config, " "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("secret;literal password")]
    public void PasswordSourcesPreserveAbsentEmptyAndExplicitValues(string password)
    {
        var config = Config();
        config.Password = password;
        var builder = new NpgsqlConnectionStringBuilder(PostgresConnectionPolicy.Build(config, null).ConnectionString);
        if(string.IsNullOrEmpty(password))
        {
            Assert.Null(builder.Password);
            Assert.False(builder.ShouldSerialize("Password"));
            Assert.Null(new NpgsqlConnectionStringBuilder("Host=localhost;Password=;").Password);
        }
        else
        {
            Assert.Equal(password, builder.Password);
            Assert.True(builder.ShouldSerialize("Password"));
        }
    }

    [Theory]
    [InlineData("tlsCert")]
    [InlineData("tlsKey")]
    public void WhitespaceClientPathsFailBeforeNormalAndRecoveryStartup(string field)
    {
        foreach(var mode in Enum.GetValues<PostgresSslMode>())
        foreach(var recovery in new[] { false, true })
        {
            var postgres = Config();
            postgres.SslMode = mode;
            if(field == "tlsCert") postgres.TlsCert = " \t\r\n";
            else postgres.TlsKey = " \t\r\n";
            var cluster = new ClusterConfig { Persistence = new() { Postgres = postgres }, Pools = Array.Empty<PoolConfig>() };
            var result = new ClusterConfigValidator(recovery).Validate(cluster);
            Assert.Contains(result.Errors, x => x.PropertyName == "Persistence.Postgres" && x.ErrorMessage.Contains(field));
        }
    }

    [Theory]
    [InlineData("sslMode")]
    [InlineData("tls")]
    [InlineData("tlsNoValidate")]
    [InlineData("tlsCert")]
    [InlineData("tlsKey")]
    [InlineData("tlsPassword")]
    [InlineData("tlsRootCert")]
    public void InvalidSecurityFieldsNameOnlyTheReviewedKey(string field)
    {
        var document = new JObject { ["persistence"] = new JObject { ["postgres"] =
            new JObject { [field.ToUpperInvariant()] = new JObject { ["SECRET"] = "SECRET" } } } };
        var error = Assert.Throws<PoolStartupException>(() => PostgresConnectionPolicy.ValidateSyntax(document));
        Assert.Contains("'" + field.ToLowerInvariant() + "'", error.Message);
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Fact]
    public void DiagnosticBoundaryContainsOnlyReviewedValueTypes()
    {
        Assert.All(typeof(PostgresConnectionDiagnostic).GetProperties(), property =>
            Assert.Contains(property.PropertyType, new[] { typeof(bool), typeof(int), typeof(SslMode) }));
        var config = Config();
        config.SslMode = PostgresSslMode.VerifyFull;
        PostgresConnectionPolicy.Build(config, null, out var fallback);
        Assert.False(fallback.RootCertificatePathConfigured);
        PostgresConnectionPolicy.Build(config, "SECRET_ROOT", out var copied);
        Assert.True(copied.RootCertificatePathConfigured);
        Assert.DoesNotContain("SECRET", PostgresConnectionPolicy.Diagnostic(copied));
    }

    [Fact]
    public void SchemaRejectsExplicitLegacyMixesAndNonVerifyingRoots()
    {
        var schema = JSchema.Parse(Program.GenerateJsonConfigSchemaDocument()["definitions"]["PostgresConfig"].ToString());
        Assert.True(JValue.CreateNull().IsValid(schema));
        Assert.True(new JObject().IsValid(schema));
        foreach(var mode in Enum.GetNames<PostgresSslMode>())
        {
            var document = new JObject { ["sslMode"] = mode };
            Assert.True(document.IsValid(schema));
            foreach(var field in new[] { "tls", "tlsNoValidate" })
            {
                document[field] = false;
                Assert.False(document.IsValid(schema));
                document[field] = true;
                Assert.False(document.IsValid(schema));
                document[field] = null;
                Assert.True(document.IsValid(schema));
                document.Remove(field);
            }
            document["tlsRootCert"] = "SECRET";
            Assert.Equal(mode is "VerifyCA" or "VerifyFull", document.IsValid(schema));
        }
        Assert.True(new JObject { ["tls"] = true, ["tlsRootCert"] = null }.IsValid(schema));
        Assert.False(new JObject { ["tlsRootCert"] = "SECRET" }.IsValid(schema));
    }

    [Theory]
    [InlineData(" semi;SSL Mode=Disable;Timeout=1; ")]
    [InlineData(" equals=value ")]
    [InlineData(" quotes\"and'quotes ")]
    [InlineData(" tabs\tnew\r\nline ")]
    public void OperatorValuesCannotBecomeConnectionOptions(string value)
    {
        var config = Config();
        config.SslMode = PostgresSslMode.VerifyFull;
        config.Host = config.Database = config.User = config.Password = value;
        config.TlsCert = config.TlsKey = config.TlsPassword = config.TlsRootCert = value;
        config.CommandTimeout = 42;
        var result = new NpgsqlConnectionStringBuilder(PostgresConnectionPolicy.Build(config, null).ConnectionString);
        Assert.Equal(value, result.Host);
        Assert.Equal(value, result.Database);
        Assert.Equal(value, result.Username);
        Assert.Equal(value, result.Password);
        Assert.Equal(value.Trim(), result.SslCertificate);
        Assert.Equal(value.Trim(), result.SslKey);
        Assert.Equal(value, result.SslPassword);
        Assert.Equal(value, result.RootCertificate);
        Assert.Equal(SslMode.VerifyFull, result.SslMode);
        Assert.Equal(42, result.CommandTimeout);
        Assert.Equal(15, result.Timeout);
        Assert.False(result.IncludeErrorDetail);
        Assert.False(result.LogParameters);
        Assert.False(result.PersistSecurityInfo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecurityPolicyIsValidatedInNormalAndRecoveryStartup(bool recovery)
    {
        var config = new ClusterConfig
        {
            Persistence = new PersistenceConfig { Postgres = Config() },
            Pools = Array.Empty<PoolConfig>(),
        };
        config.Persistence.Postgres.SslMode = PostgresSslMode.VerifyFull;
        config.Persistence.Postgres.TlsNoValidate = true;
        var validation = new ClusterConfigValidator(recovery).Validate(config);
        Assert.Contains(validation.Errors, e => e.PropertyName == "Persistence.Postgres" &&
            e.ErrorMessage.Contains("omit legacy flags"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileLoaderRejectsUnknownModesBeforeSchemaCanEchoThem(bool recovery)
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "{\"persistence\":{\"postgres\":{\"sslMode\":\"SECRET_INVALID_MODE\"}}}");
            var error = Assert.Throws<PoolStartupException>(() => Program.ReadAndValidateConfig(file, recovery));
            Assert.DoesNotContain("SECRET_INVALID_MODE", error.ToString());
            Assert.Contains("invalid TLS setting", error.Message);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task PublicStartupRejectsConflictsBeforeStartingServices(bool recovery)
    {
        var file = Path.GetTempFileName();
        try
        {
            var document = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config.example.json")));
            document["persistence"]["postgres"]["sslMode"] = "VerifyFull";
            document["persistence"]["postgres"]["tlsNoValidate"] = true;
            document["persistence"]["postgres"]["tlsRootCert"] = "SECRET_ROOT_PATH";
            File.WriteAllText(file, document.ToString());
            var start = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach(var argument in new[]
            {
                "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.runtimeconfig.json"),
                "--depsfile", Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.deps.json"),
                Path.Combine(AppContext.BaseDirectory, "Miningcore.dll"), "-c", file,
            })
                start.ArgumentList.Add(argument);
            if(recovery)
            {
                start.ArgumentList.Add("-rs");
                start.ArgumentList.Add("unused-recovery-file");
            }
            using var process = System.Diagnostics.Process.Start(start);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); throw; }
            var output = await stdout + await stderr;
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("omit legacy flags", output);
            Assert.DoesNotContain("SECRET_ROOT_PATH", output);
            Assert.DoesNotContain("Using PostgreSQL persistence", output);
            Assert.DoesNotContain("Now listening", output);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async System.Threading.Tasks.Task ConnectionConstructorFailuresDoNotExposeInput()
    {
        var factory = new PgConnectionFactory("SECRET_OPTION=SECRET_VALUE");
        var error = await Assert.ThrowsAsync<PostgresConnectionException>(() => factory.OpenConnectionAsync());
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.False(error.IsTransient);
    }

    [Theory]
    [InlineData("password", "PasswordConfigured")]
    [InlineData("cert", "TlsCertConfigured")]
    [InlineData("key", "TlsKeyConfigured")]
    [InlineData("tlsPassword", "TlsPasswordConfigured")]
    [InlineData("root", "RootCertificatePathConfigured")]
    public void DiagnosticPresenceFlagsAreIndependent(string setting, string expected)
    {
        var config = Config();
        config.SslMode = PostgresSslMode.VerifyFull;
        switch(setting)
        {
            case "password": config.Password = "SECRET"; break;
            case "cert": config.TlsCert = "SECRET"; break;
            case "key": config.TlsKey = "SECRET"; break;
            case "tlsPassword": config.TlsPassword = "SECRET"; break;
            case "root": config.TlsRootCert = "SECRET"; break;
        }
        PostgresConnectionPolicy.Build(config, null, out var snapshot);
        var diagnostic = PostgresConnectionPolicy.Diagnostic(snapshot);
        var metadata = JObject.Parse(diagnostic);
        Assert.Equal(new[] { expected }, metadata.Properties()
            .Where(p => p.Name.EndsWith("Configured") && p.Value.Value<bool>()).Select(p => p.Name));
        Assert.DoesNotContain("SECRET", diagnostic);
    }

    [Fact]
    public void RegisteredFactoryAndLoggingUseReviewedPolicy()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var field = typeof(Program).GetField("logger", flags);
        var previous = field.GetValue(null);
        var defaults = JsonConvert.DefaultSettings;
        using var factory = new LogFactory();
        var target = new MemoryTarget { Layout = "${message}|${exception:format=ToString}" };
        var logging = new LoggingConfiguration(factory);
        logging.AddRuleForAllLevels(target);
        factory.Configuration = logging;
        try
        {
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            { Formatting = Formatting.Indented, DefaultValueHandling = DefaultValueHandling.Ignore };
            field.SetValue(null, factory.GetLogger("Core"));
            var config = Config();
            config.SslMode = PostgresSslMode.VerifyFull;
            config.Host = config.Database = config.User = config.Password = "SECRET;\r\nSslMode=Disable";
            config.TlsCert = config.TlsKey = config.TlsPassword = config.TlsRootCert = "SECRET";
            var builder = new ContainerBuilder();
            typeof(Program).GetMethod("ConfigurePostgres", flags).Invoke(null, new object[] { config, builder });
            using var container = builder.Build();
            var registered = Assert.IsType<PgConnectionFactory>(container.Resolve<IConnectionFactory>());
            var raw = (string) typeof(PgConnectionFactory).GetField("connectionString",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(registered);
            var parsed = new NpgsqlConnectionStringBuilder(raw);
            Assert.Equal(SslMode.VerifyFull, parsed.SslMode);
            Assert.Equal(config.Password, parsed.Password);
            factory.Flush();
            var output = Assert.Single(target.Logs);
            Assert.Equal("Using PostgreSQL persistence {\"Port\":5432,\"SslMode\":\"VerifyFull\"," +
                "\"TlsNoValidate\":false,\"PasswordConfigured\":true,\"TlsCertConfigured\":true," +
                "\"TlsKeyConfigured\":true,\"TlsPasswordConfigured\":true," +
                "\"RootCertificatePathConfigured\":true,\"CommandTimeout\":300}|", output);
            Assert.DoesNotContain("SECRET", output);
            Assert.DoesNotContain("\n", output);
        }
        finally
        {
            field.SetValue(null, previous);
            JsonConvert.DefaultSettings = defaults;
        }
    }
}
