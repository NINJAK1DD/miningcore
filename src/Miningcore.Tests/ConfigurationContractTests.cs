using System;
using System.IO;
using System.Linq;
using Miningcore.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests;

public class ConfigurationContractTests
{
    [Fact]
    public void CommittedConfigSchema_MatchesGenerator()
    {
        // The build-output schema is the runtime-shipped artifact. MSBuild refreshes
        // it from src/Miningcore/config.schema.json before this test executes.
        var path = Path.Combine(AppContext.BaseDirectory,
            "config.schema.json");
        var committed = JObject.Parse(File.ReadAllText(path));
        var generated = Program.GenerateJsonConfigSchemaDocument();

        Assert.True(JToken.DeepEquals(generated, committed),
            "src/Miningcore/config.schema.json is stale; regenerate it with Miningcore -gcs");

        foreach(var itemTypePath in new[]
                {
                    "definitions.ApiConfig.properties.adminIpWhitelist.items.type",
                    "definitions.ApiConfig.properties.metricsIpWhitelist.items.type",
                    "definitions.ApiRateLimitConfig.properties.ipWhitelist.items.type",
                    "definitions.TcpProxyProtocolConfig.properties.proxyAddresses.items.type",
                    "properties.coinTemplates.items.type",
                })
            Assert.Equal("string",
                committed.SelectToken(itemTypePath)?.Value<string>());
    }

    [Fact]
    public void ConfigSchema_KeepsPoolEndpointNullable_ForDeferredValidation()
    {
        // Miningcore's generated structural schema does not encode the runtime
        // listener predicate: disabled, relay-only and recovery pools deliberately
        // defer endpoint validation. Enforce non-null endpoints in
        // PoolConfigValidator only when a listener will be started.
        var path = Path.Combine(AppContext.BaseDirectory,
            "config.schema.json");
        var committed = JObject.Parse(File.ReadAllText(path));

        Assert.Equal(new[] { "object", "null" }, committed.SelectToken(
                "definitions.PoolEndpoint.type")
            ?.Values<string>());
    }

    [Fact]
    public void ConfigSchema_KeepsPoolPaymentProcessingOptional_ForRecovery()
    {
        // Normal startup enforces this live-service contract through the
        // mode-aware validator. Keeping it out of the structural schema lets
        // recovery sanitize damaged payout settings before binding.
        var path = Path.Combine(AppContext.BaseDirectory,
            "config.schema.json");
        var committed = JObject.Parse(File.ReadAllText(path));
        var required = committed.SelectToken(
                "definitions.PoolConfig.required")?
            .Values<string>()
            .ToArray();

        Assert.NotNull(committed.SelectToken(
            "definitions.PoolConfig.properties.paymentProcessing"));
        Assert.DoesNotContain("paymentProcessing",
            required ?? Array.Empty<string>());
        Assert.Equal(new[] { "object", "null" }, committed.SelectToken(
                "definitions.PoolPaymentProcessingConfig.type")?
            .Values<string>());
    }

    [Fact]
    public void ShippedExampleConfig_PassesNormalStartupValidation()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "config.example.json");
        var config = Program.ReadConfig(path, false);
        var result = new ClusterConfigValidator().Validate(config);

        // Keep enabled internal listeners on wildcard or loopback addresses. A
        // future specific IPv4 address would make this test depend on the CI
        // runner's active subnet table.
        Assert.True(result.IsValid, string.Join(Environment.NewLine,
            result.Errors.Select(error =>
                $"{error.PropertyName}: {error.ErrorMessage}")));
        Assert.All(config.Pools,
            pool => Assert.NotNull(pool.PaymentProcessing));

        // Exercise the actual live-startup boundary as well as the diagnostic-rich
        // direct result above. This applies live defaults, merged-mining checks,
        // API/Stratum conflict detection and the enabled-pool requirement.
        Program.ValidateConfig(config, false);
    }
}
