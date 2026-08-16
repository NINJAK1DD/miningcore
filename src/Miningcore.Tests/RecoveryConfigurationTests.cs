using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests;

public class RecoveryConfigurationTests
{
    [Theory]
    [InlineData("statistics", "\"stale\"")]
    [InlineData("nicehash", "[]")]
    [InlineData("banning", "\"stale\"")]
    [InlineData("shareRelay", "false")]
    [InlineData("shareRelays", "{}")]
    [InlineData("notifications", "\"stale\"")]
    [InlineData("memory", "[]")]
    [InlineData("equihashMaxThreads", "\"stale\"")]
    [InlineData("cryptonightMaxThreads", "{}")]
    [InlineData("clusterName", "[]")]
    public void RecoveryMode_DiscardsMalformedUnusedClusterSettings(
        string propertyName, string valueJson)
    {
        var document = CreateRecoveryDocument();
        document[propertyName] = JToken.Parse(valueJson);
        var configFile = WriteTemporaryConfig(document);

        try
        {
            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));

            var config = Program.ReadAndValidateConfig(configFile, true);

            Assert.Null(config.Statistics);
            Assert.Null(config.Nicehash);
            Assert.Null(config.Banning);
            Assert.Null(config.ShareRelay);
            Assert.Null(config.ShareRelays);
            Assert.Null(config.Notifications);
            Assert.Null(config.Memory);
            Assert.Null(config.EquihashMaxThreads);
            Assert.Null(config.CryptonightMaxThreads);
            Assert.Null(config.ClusterName);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void RecoveryMode_DiscardsExactDuplicateUnusedClusterSettings()
    {
        var document = CreateRecoveryDocument();
        var pools = document["pools"].ToString(Formatting.None);
        var persistence = document["persistence"].ToString(Formatting.None);
        var rawConfig = "{" +
            $"\"persistence\":{persistence}," +
            "\"statistics\":\"first-stale-value\"," +
            "\"statistics\":[]," +
            $"\"pools\":{pools}" +
            "}";
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, rawConfig);

            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));
            var config = Program.ReadAndValidateConfig(configFile, true);

            Assert.Null(config.Statistics);
            Assert.Single(config.Pools);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Theory]
    [InlineData("persistence", "\"stale\"")]
    [InlineData("pools", "\"stale\"")]
    [InlineData("shareRecoveryFile", "{}")]
    [InlineData("shareRecoveryStateDirectory", "[]")]
    public void RecoveryMode_RetainsStrictValidationForConsumedClusterSettings(
        string propertyName, string valueJson)
    {
        var document = CreateRecoveryDocument();
        document[propertyName] = JToken.Parse(valueJson);
        var configFile = WriteTemporaryConfig(document);

        try
        {
            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, true));
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("malformed")]
    public void RecoveryMode_SynthesizesDefaultConsoleLogging(
        string loggingShape)
    {
        var document = CreateRecoveryDocument();

        if(loggingShape == "absent")
            document.Property("logging")?.Remove();
        else if(loggingShape == "null")
            document["logging"] = JValue.CreateNull();
        else
            document["logging"] = "stale";

        var configFile = WriteTemporaryConfig(document);

        try
        {
            if(loggingShape == "malformed")
                Assert.Throws<PoolStartupException>(() =>
                    Program.ReadAndValidateConfig(configFile, false));

            var config = Program.ReadAndValidateConfig(configFile, true);

            Assert.NotNull(config.Logging);
            Assert.Null(config.Logging.Level);
            Assert.False(config.Logging.EnableConsoleColors);
            Assert.False(config.Logging.EnableConsoleLog);
            Assert.Null(config.Logging.LogFile);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void RecoveryMode_RetainsOnlyConsumedConsoleLoggingSettings()
    {
        var document = CreateRecoveryDocument();
        document["logging"] = new JObject
        {
            ["level"] = "Warn",
            ["enableConsoleColors"] = true,
            ["enableConsoleLog"] = new JObject(),
            ["logFile"] = new JArray(),
            ["apiLogFile"] = false,
            ["perPoolLogFile"] = "stale",
            ["logBaseDirectory"] = new JObject(),
            ["gpdrCompliant"] = new JArray(),
        };
        var configFile = WriteTemporaryConfig(document);

        try
        {
            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));

            var config = Program.ReadAndValidateConfig(configFile, true);

            Assert.Equal("Warn", config.Logging.Level);
            Assert.True(config.Logging.EnableConsoleColors);
            Assert.False(config.Logging.EnableConsoleLog);
            Assert.Null(config.Logging.LogFile);
            Assert.Null(config.Logging.ApiLogFile);
            Assert.False(config.Logging.PerPoolLogFile);
            Assert.Null(config.Logging.LogBaseDirectory);
            Assert.False(config.Logging.GPDRCompliant);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Theory]
    [InlineData("level", "{}")]
    [InlineData("enableConsoleColors", "\"stale\"")]
    public void RecoveryMode_StrictlyValidatesConsumedConsoleLoggingSettings(
        string propertyName, string valueJson)
    {
        var document = CreateRecoveryDocument();
        var logging = Assert.IsType<JObject>(document["logging"]);
        logging[propertyName] = JToken.Parse(valueJson);
        var configFile = WriteTemporaryConfig(document);

        try
        {
            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, true));
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidLoggingLevel_FailsValidationBeforeLoggingSetup(
        bool recoveryMode)
    {
        var config = CreateRecoveryConfig();
        config.Logging.Level = "verbose";

        var result = new ClusterConfigValidator(recoveryMode).Validate(config);
        var error = Assert.Single(result.Errors.Where(error =>
            error.PropertyName == "Logging.Level"));

        Assert.Equal(
            "Logging: level 'verbose' is invalid; use trace, debug, info/information, warn/warning, error, fatal, off/none, or omit it for info",
            error.ErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TRACE")]
    [InlineData("information")]
    [InlineData("warning")]
    [InlineData("none")]
    public void LoggingLevel_AcceptsNLogNamesAliasesAndDefault(
        string level)
    {
        var config = CreateRecoveryConfig();
        config.Logging.Level = level;

        var result = new ClusterConfigValidator(true).Validate(config);

        Assert.DoesNotContain(result.Errors, error =>
            error.PropertyName == "Logging.Level");
    }

    [Fact]
    public void NormalStartup_NullPoolEntryUsesConfigurationValidationBoundary()
    {
        var config = CreateRecoveryConfig();
        config.Pools = new PoolConfig[] { null };

        Assert.Throws<PoolStartupException>(() =>
            Program.ValidateConfig(config, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryMode_DiscardsMissingOrMalformedUnusedLivePoolSettings(
        bool omitSettings)
    {
        var document = CreateRecoveryDocument();
        document.Property("paymentProcessing")?.Remove();
        var pool = Assert.IsType<JObject>(document["pools"]?[0]);
        var unusedSettings = new[]
        {
            "address",
            "banning",
            "blockRefreshInterval",
            "clientConnectionTimeout",
            "daemons",
            "enableAsicBoost",
            "enabled",
            "enableInternalStratum",
            "jobRebroadcastTimeout",
            "paymentProcessing",
            "ports",
            "pubKey",
            "rewardRecipients",
            "vardiffIdleSweepInterval",
        };

        if(omitSettings)
        {
            foreach(var propertyName in unusedSettings)
                pool.Property(propertyName)?.Remove();
        }
        else
        {
            pool["address"] = new JObject { ["stale"] = true };
            pool["banning"] = "stale";
            pool["blockRefreshInterval"] = "stale";
            pool["clientConnectionTimeout"] = new JObject();
            pool["daemons"] = new JObject { ["malformed"] = true };
            pool["enableAsicBoost"] = "stale";
            pool["enabled"] = "false";
            pool["enableInternalStratum"] = new JArray();
            pool["jobRebroadcastTimeout"] = false;
            pool["paymentProcessing"] = "stale";
            pool["ports"] = "stale";
            pool["pubKey"] = new JObject();
            pool["rewardRecipients"] = new JObject();
            pool["vardiffIdleSweepInterval"] = "stale";
            pool["staleExtensionSetting"] = new JObject
            {
                ["malformed"] = true,
            };
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
            Assert.Empty(recoveredPool.Ports);
            Assert.Null(recoveredPool.PaymentProcessing);
            Assert.Null(recoveredPool.Banning);
            Assert.Empty(recoveredPool.RewardRecipients);
            Assert.Equal(0, recoveredPool.BlockRefreshInterval);
            Assert.Equal(0, recoveredPool.ClientConnectionTimeout);
            Assert.Equal(0, recoveredPool.JobRebroadcastTimeout);
            Assert.Null(recoveredPool.EnableAsicBoost);
            Assert.Null(recoveredPool.EnableInternalStratum);
            Assert.Null(recoveredPool.PubKey);
            Assert.Null(recoveredPool.VardiffIdleSweepInterval);
            Assert.Null(recoveredPool.Extra);
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

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    public void RecoveryMode_MissingOrMalformedPoolIdentityStillBlocksImport(
        string idJson)
    {
        var document = CreateRecoveryDocument();
        var pool = Assert.IsType<JObject>(document["pools"]?[0]);

        if(idJson == null)
            pool.Property("id")?.Remove();
        else
            pool["id"] = JToken.Parse(idJson);

        var configFile = WriteTemporaryConfig(document);

        try
        {
            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, true));
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void RecoveryMode_RejectsCaseVariantPoolIdentityBeforeSanitization()
    {
        var document = CreateRecoveryDocument();
        var pool = Assert.IsType<JObject>(document["pools"]?[0]);
        pool["Id"] = pool.Value<string>("id");
        var configFile = WriteTemporaryConfig(document);

        try
        {
            var error = Assert.Throws<PoolStartupException>(() =>
                Program.ReadConfig(configFile, true));

            Assert.Contains("Properties 'id', 'Id'", error.Message,
                StringComparison.Ordinal);
            Assert.Contains("differ only by case", error.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public async Task RecoveryMode_SanitizedDisabledPoolsStillRequireSharePartitions()
    {
        var document = CreateRecoveryDocument();
        var configFile = WriteTemporaryConfig(document);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var shareRepository = Substitute.For<IShareRepository>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        shareRepository.GetMissingSharePartitionsAsync(connection,
                Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { "recovery-pool" });

        try
        {
            var config = Program.ReadAndValidateConfig(configFile, true);
            Assert.All(config.Pools, pool => Assert.False(pool.Enabled));

            var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
                Program.EnsureSharePartitionsAsync(true, config,
                    connectionFactory, shareRepository,
                    CancellationToken.None));

            Assert.Contains("configured recovery pool ID(s)", error.Message,
                StringComparison.Ordinal);
            await shareRepository.Received(1).GetMissingSharePartitionsAsync(
                connection,
                Arg.Is<IEnumerable<string>>(ids =>
                    ids.SequenceEqual(new[] { "recovery-pool" })),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void RecoveryMode_DiscardsUnusedInstanceIdentityWhileNormalStartupRejectsIt()
    {
        var document = CreateRecoveryDocument();
        document["instanceId"] = 0;
        var configFile = WriteTemporaryConfig(document);

        try
        {
            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));

            var config = Program.ReadAndValidateConfig(configFile, true);
            Assert.Null(config.InstanceId);

            var directConfig = CreateRecoveryConfig();
            directConfig.InstanceId = 0;
            Assert.False(new ClusterConfigValidator().Validate(directConfig)
                .IsValid);
            Assert.True(new ClusterConfigValidator(true).Validate(directConfig)
                .IsValid);
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
        config.Pools[0].PaymentProcessing = null;
        config.Pools[0].Address = null;
        config.Pools[0].Daemons = null;

        var result = new ClusterConfigValidator().Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.PropertyName == nameof(ClusterConfig.PaymentProcessing));
        Assert.Contains(result.Errors, error =>
            error.PropertyName == "Pools[0].PaymentProcessing" &&
            error.ErrorMessage ==
            "Pool 'recovery-pool': paymentProcessing configuration missing");
        Assert.Contains(result.Errors, error =>
            error.ErrorMessage == "Pool: Wallet address missing or empty");
        Assert.Contains(result.Errors, error =>
            error.ErrorMessage == "Pool: Daemons missing or empty");

        config.Pools[0].Enabled = false;
        var error = Assert.Throws<PoolStartupException>(() =>
            Program.ValidateConfig(config, false));
        Assert.Equal("No pools are enabled.", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalStartup_MissingPoolPaymentProcessingIdentifiesPool(
        bool explicitNull)
    {
        var document = CreateRecoveryDocument();
        var pool = Assert.IsType<JObject>(document["pools"]?[0]);

        if(explicitNull)
            pool["paymentProcessing"] = JValue.CreateNull();
        else
            pool.Property("paymentProcessing")?.Remove();

        var configFile = WriteTemporaryConfig(document);

        try
        {
            var config = Program.ReadConfig(configFile, false);
            var result = new ClusterConfigValidator().Validate(config);
            var error = Assert.Single(result.Errors, failure =>
                failure.PropertyName == "Pools[0].PaymentProcessing");

            Assert.Equal(
                "Pool 'recovery-pool': paymentProcessing configuration missing",
                error.ErrorMessage);
            Assert.Throws<PoolStartupException>(() =>
                Program.ValidateConfig(config, false));
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalStartup_RequiresPaymentProcessingForEveryConfiguredPool(
        bool enabled)
    {
        var config = CreateRecoveryConfig();
        config.Pools[0].Enabled = enabled;
        config.Pools[0].PaymentProcessing = null;

        var result = new ClusterConfigValidator().Validate(config);

        var error = Assert.Single(result.Errors, failure =>
            failure.PropertyName == "Pools[0].PaymentProcessing");
        Assert.Equal(
            "Pool 'recovery-pool': paymentProcessing configuration missing",
            error.ErrorMessage);
    }

    [Fact]
    public void RecoveryMode_DoesNotConsumePoolPaymentProcessing()
    {
        var config = CreateRecoveryConfig();
        config.Pools[0].PaymentProcessing = null;

        var result = new ClusterConfigValidator(true).Validate(config);

        Assert.DoesNotContain(result.Errors, failure =>
            failure.PropertyName.EndsWith("PaymentProcessing",
                StringComparison.Ordinal));
        Assert.True(result.IsValid);
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
