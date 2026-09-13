using System;
using System.IO;
using Miningcore.Configuration;
using Miningcore.Mining;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using Npgsql;
using Xunit;

namespace Miningcore.Tests;

[Collection(PostgresPolicyCollection.Name)]
public class PostgresCommandTimeoutTests
{
    // Exercise the shipped schema; ConfigurationContractTests separately checks
    // generation parity. Repeated generation also exhausts the library's quota.
    private static readonly Lazy<JSchema> PostgresSchema = new(() => JSchema.Parse(
        JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config.schema.json")))
            ["definitions"]["PostgresConfig"].ToString()));
    [Theory]
    [InlineData(null, 300)]
    [InlineData("null", 300)]
    [InlineData("1", 1)]
    [InlineData("42", 42)]
    [InlineData("300", 300)]
    [InlineData("86400", 86400)]
    public void AcceptedValuesReachConnectionCommandAndSafeDiagnostic(string json, int expected)
    {
        var postgres = PostgresDocument(json);
        Assert.True(postgres.IsValid(Schema()));
        foreach(var recovery in new[] { false, true })
        {
            var config = Read(postgres, recovery).Persistence.Postgres;
            var builder = PostgresConnectionPolicy.Build(config, null, out var diagnostic);
            using var connection = new NpgsqlConnection(builder.ConnectionString);
            using var command = connection.CreateCommand();
            Assert.Equal(expected, connection.CommandTimeout);
            Assert.Equal(expected, command.CommandTimeout);
            Assert.Equal(expected, JObject.Parse(PostgresConnectionPolicy.Diagnostic(diagnostic))["CommandTimeout"].Value<int>());
            // These driver timeouts have different purposes and units; this policy does not change them.
            Assert.Equal(15, builder.Timeout);
            Assert.Equal(2000, builder.CancellationTimeout);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-2147483648")]
    [InlineData("86401")]
    [InlineData("2147483647")]
    [InlineData("2147483648")]
    [InlineData("9999999999999999999999999999999999999999")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("\"42\"")]
    [InlineData("\"SECRET\\r\\nforged\"")]
    [InlineData("{\"SECRET\":\"SECRET\"}")]
    [InlineData("[]")]
    public void InvalidValuesFailSchemaAndBothStartupModesWithoutEcho(string json)
    {
        var postgres = PostgresDocument(json);
        Assert.False(postgres.IsValid(Schema()));
        foreach(var recovery in new[] { false, true })
        foreach(var key in new[] { "commandTimeout", "COMMANDTIMEOUT" })
        {
            postgres.Remove("commandTimeout");
            postgres.Remove("COMMANDTIMEOUT");
            postgres[key] = JToken.Parse(json);
            AssertNamedError(Assert.Throws<PoolStartupException>(() => Read(postgres, recovery)));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(86401)]
    [InlineData(int.MaxValue)]
    public void ProgrammaticConfigurationFailsValidationAndFactoryConstruction(int timeout)
    {
        var postgres = PostgresConnectionPolicyTests.Config();
        postgres.CommandTimeout = timeout;
        AssertNamedError(Assert.Throws<PoolStartupException>(() => PostgresConnectionPolicy.Build(postgres, null)));
        foreach(var recovery in new[] { false, true })
        {
            var config = new ClusterConfig { Persistence = new() { Postgres = postgres }, Pools = Array.Empty<PoolConfig>() };
            var result = new ClusterConfigValidator(recovery).Validate(config);
            Assert.Contains(result.Errors, e => e.PropertyName == "Persistence.Postgres" &&
                e.ErrorMessage.Contains("persistence.postgres.commandTimeout"));
        }
    }

    private static void AssertNamedError(PoolStartupException error)
    {
        Assert.Contains("persistence.postgres.commandTimeout", error.Message);
        Assert.Contains("1 to 86400 seconds", error.Message);
        Assert.Contains("zero (unlimited) is not supported", error.Message);
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.DoesNotContain("forged", error.ToString());
    }

    private static JSchema Schema() => PostgresSchema.Value;

    private static JObject PostgresDocument(string json)
    {
        var result = JObject.Parse("""{"host":"localhost","port":5432,"database":"miningcore","user":"miningcore"}""");
        if(json != null)
            result["commandTimeout"] = JToken.Parse(json);
        return result;
    }

    private static ClusterConfig Read(JObject postgres, bool recovery)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, new JObject
            {
                ["persistence"] = new JObject { ["postgres"] = postgres },
                ["pools"] = new JArray(),
            }.ToString());
            return Program.ReadConfig(path, recovery);
        }
        finally { File.Delete(path); }
    }
}
