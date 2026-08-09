using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Mining;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Xunit;

namespace Miningcore.Tests;

public class RecoveryConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryMode_DiscardsUnusedLivePoolSettings(bool omitSettings)
    {
        var document = CreateRecoveryDocument();
        document.Property("paymentProcessing")?.Remove();
        var pool = Assert.IsType<JObject>(document["pools"]?[0]);
        pool["enabled"] = false;
        if(omitSettings)
        {
            pool.Property("address")?.Remove();
            pool.Property("daemons")?.Remove();
            pool.Property("paymentProcessing")?.Remove();
        }
        else
        {
            pool["address"] = new JObject { ["stale"] = true };
            pool["daemons"] = new JObject { ["malformed"] = true };
            pool["paymentProcessing"] = "stale";
        }
        pool["coin"] = "undefined-coin";
        var configFile = WriteTemporaryConfig(document);

        try
        {
            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));

            var config = Program.ReadAndValidateConfig(configFile, true);

            Assert.Null(config.PaymentProcessing);
            Assert.NotNull(config.Persistence?.Postgres);
            var recoveredPool = Assert.Single(config.Pools);
            Assert.False(recoveredPool.Enabled);
            Assert.Null(recoveredPool.Address);
            Assert.Empty(recoveredPool.Daemons);
            Assert.Null(recoveredPool.PaymentProcessing);
            Assert.Equal("undefined-coin", recoveredPool.Coin);

            // Verification and acknowledgement use this non-validating recovery reader and
            // remain independent of normal live-pool validation.
            var verificationConfig = Program.ReadConfig(configFile, true);
            var acknowledgementConfig = Program.ReadConfig(configFile, true);
            Assert.Single(verificationConfig.Pools);
            Assert.Single(acknowledgementConfig.Pools);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void RecoveryMode_RejectsDuplicatePoolIdentity()
    {
        var config = CreateRecoveryConfig();
        config.Pools = new[]
        {
            config.Pools[0],
            new PoolConfig { Id = config.Pools[0].Id },
        };

        var result = new ClusterConfigValidator(true).Validate(config);

        Assert.Contains(result.Errors, error =>
            error.ErrorMessage == "Duplicate pool id 'recovery-pool'");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void RecoveryMode_MissingOrMalformedCoinMetadataWarnsLaterInsteadOfBlockingImport(
        string coinJson)
    {
        var document = CreateRecoveryDocument();
        var pool = Assert.IsType<JObject>(document["pools"]?[0]);

        if(coinJson == null)
            pool.Property("coin")?.Remove();
        else
            pool["coin"] = JToken.Parse(coinJson);

        var configFile = WriteTemporaryConfig(document);

        try
        {
            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));

            var config = Program.ReadAndValidateConfig(configFile, true);
            Assert.Equal(string.Empty, Assert.Single(config.Pools).Coin);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void RecoveryMode_RetainsPoolIdentityAndPostgresValidation()
    {
        var config = CreateRecoveryConfig();
        config.Pools[0].Id = string.Empty;
        config.Persistence.Postgres.Host = string.Empty;
        config.Persistence.Postgres.Port = 0;
        config.Persistence.Postgres.Database = string.Empty;
        config.Persistence.Postgres.User = string.Empty;

        var result = new ClusterConfigValidator(true).Validate(config);
        var messages = result.Errors.Select(error => error.ErrorMessage)
            .ToArray();

        Assert.Contains("Pool: id missing or empty", messages);
        Assert.Contains("Share recovery PostgreSQL host missing or empty",
            messages);
        Assert.Contains("Share recovery PostgreSQL port is invalid", messages);
        Assert.Contains("Share recovery PostgreSQL database missing or empty",
            messages);
        Assert.Contains("Share recovery PostgreSQL user missing or empty",
            messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryMode_RequiresPostgresPersistence(bool omitPersistence)
    {
        var config = CreateRecoveryConfig();
        if(omitPersistence)
            config.Persistence = null;
        else
            config.Persistence.Postgres = null;

        var result = new ClusterConfigValidator(true).Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage ==
            (omitPersistence
                ? "Share recovery requires persistence configuration"
                : "Share recovery requires PostgreSQL persistence"));
    }

    [Fact]
    public void NormalStartup_RemainsStrictForLivePoolConfiguration()
    {
        var config = CreateRecoveryConfig();
        config.PaymentProcessing = null;
        config.Pools[0].Address = null;
        config.Pools[0].Daemons = null;

        var result = new ClusterConfigValidator().Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.PropertyName == nameof(ClusterConfig.PaymentProcessing));
        Assert.Contains(result.Errors, error =>
            error.ErrorMessage == "Pool: Wallet address missing or empty");
        Assert.Contains(result.Errors, error =>
            error.ErrorMessage == "Pool: Daemons missing or empty");

        config.Pools[0].Enabled = false;
        var error = Assert.Throws<PoolStartupException>(() =>
            Program.ValidateConfig(config, false));
        Assert.Equal("No pools are enabled.", error.Message);
    }

    [Fact]
    public async Task RecoveryMode_AssignsTemplatesForDisabledPoolsAndContinuesWhenUnavailable()
    {
        var config = CreateRecoveryConfig();
        config.Pools[0].Enabled = false;
        var bitcoin = new BitcoinTemplate
        {
            Symbol = "BTC",
            Family = CoinFamily.Bitcoin,
        };

        Program.AssignRecoveryPoolTemplates(config,
            new Dictionary<string, CoinTemplate>
            {
                ["bitcoin"] = bitcoin,
            });
        Assert.Same(bitcoin, config.Pools[0].Template);

        config.Pools[0].Coin = "undefined-coin";
        var enrichablePool = new PoolConfig
        {
            Id = "disabled-enrichable-pool",
            Coin = "bitcoin",
            Enabled = false,
        };
        config.Pools = new[] { config.Pools[0], enrichablePool };
        var imported = false;
        Exception warning = null;
        await Program.RecoverSharesWithBestEffortTemplatesAsync(
            () => Program.AssignRecoveryPoolTemplates(config,
                new Dictionary<string, CoinTemplate>
                {
                    ["bitcoin"] = bitcoin,
                }),
            () =>
            {
                imported = true;
                return Task.CompletedTask;
            },
            ex => warning = ex);

        Assert.True(imported);
        Assert.IsType<PoolStartupException>(warning);
        Assert.Same(bitcoin, enrichablePool.Template);
    }

    private static ClusterConfig CreateRecoveryConfig() => new()
    {
        Logging = new ClusterLoggingConfig(),
        PaymentProcessing = new ClusterPaymentProcessingConfig(),
        Persistence = new PersistenceConfig
        {
            Postgres = new PostgresConfig
            {
                Host = "127.0.0.1",
                Port = 5432,
                Database = "miningcore",
                User = "miningcore",
            },
        },
        Pools = new[]
        {
            new PoolConfig
            {
                Id = "recovery-pool",
                Coin = "bitcoin",
                Enabled = true,
                EnableInternalStratum = false,
                Address = "recovery-wallet",
                Ports = new Dictionary<int, PoolEndpoint>(),
                Daemons = new[]
                {
                    new DaemonEndpointConfig
                    {
                        Host = "127.0.0.1",
                        Port = 8332,
                    },
                },
            },
        },
    };

    private static JObject CreateRecoveryDocument() =>
        JObject.Parse(JsonConvert.SerializeObject(CreateRecoveryConfig(),
            new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
            }));

    private static string WriteTemporaryConfig(JObject document)
    {
        var filename = Path.GetTempFileName();
        File.WriteAllText(filename, document.ToString());
        return filename;
    }
}
