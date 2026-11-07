using System;
using System.Collections.Generic;
using System.IO;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Mining;
using Xunit;

namespace Miningcore.Tests;

public partial class ProgramPoolTemplateTests
{
    [Theory]
    [InlineData(CoinFamily.Bitcoin, 0)]
    [InlineData(CoinFamily.Bitcoin, -1)]
    [InlineData(CoinFamily.Nexa, 0)]
    [InlineData(CoinFamily.Progpow, -1)]
    [InlineData(CoinFamily.Satoshicash, 0)]
    [InlineData(CoinFamily.Equihash, -1)]
    public void CustodialThreshold_IsRejectedAtAssignedTemplateStartup(CoinFamily family, int minimum)
    {
        var pool = new PoolConfig { Id = "invalid-policy", Coin = "custom", Enabled = true,
            PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true },
            Extra = new Dictionary<string, object> { ["minimumConfirmations"] = minimum } };
        var config = new ClusterConfig { Pools = new[] { pool },
            PaymentProcessing = new ClusterPaymentProcessingConfig { Enabled = true } };
        Program.AssignPoolTemplates(config.Pools, new Dictionary<string, CoinTemplate>
        {
            ["custom"] = family == CoinFamily.Equihash
                ? new EquihashCoinTemplate { Family = family }
                : new BitcoinTemplate { Family = family },
        });
        var error = Assert.Throws<PoolStartupException>(() => Program.ValidateBitcoinConfirmationDeployment(config));
        Assert.Equal(pool.Id, error.PoolId);
        Assert.Contains("minimumConfirmations must be positive", error.Message);
        pool.Extra["minimumConfirmations"] = 1;
        Program.ValidateBitcoinConfirmationDeployment(config);
    }

    [Fact]
    public void CustodialThreshold_ValidatesEffectiveTemplateDefaultAndOverride()
    {
        var pool = new PoolConfig { Id = "custom", Enabled = true,
            PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true },
            Template = new BitcoinTemplate { Family = CoinFamily.Bitcoin, CoinbaseMinConfimations = 0 } };
        var config = new ClusterConfig { Pools = new[] { pool },
            PaymentProcessing = new ClusterPaymentProcessingConfig { Enabled = true } };
        Assert.Throws<PoolStartupException>(() => Program.ValidateBitcoinConfirmationDeployment(config));
        pool.Extra = new Dictionary<string, object> { ["minimumConfirmations"] = 42 };
        Program.ValidateBitcoinConfirmationDeployment(config);
        Assert.Equal(42, BitcoinConfirmationPolicy.Resolve(pool));
        pool.Extra = null;
        ((BitcoinTemplate) pool.Template).CoinbaseMinConfimations = null;
        Assert.Equal(102, BitcoinConfirmationPolicy.Resolve(pool));
        pool.Extra = new Dictionary<string, object> { ["minimumConfirmations"] = 0 };
        pool.Enabled = false;
        Program.ValidateBitcoinConfirmationDeployment(config);
        pool.Enabled = true;
        pool.Template = new EthereumCoinTemplate { Family = CoinFamily.Ethereum };
        Program.ValidateBitcoinConfirmationDeployment(config);
    }

    [Fact]
    public void CustodialThreshold_OnlyAppliesWhenClusterAndPoolProcessPayments()
    {
        foreach(var clusterEnabled in new bool?[] { null, false, true })
        foreach(var poolEnabled in new bool?[] { null, false, true })
        {
            var pool = new PoolConfig { Id = "relay", Enabled = true,
                Template = new BitcoinTemplate { Family = CoinFamily.Bitcoin },
                Extra = new Dictionary<string, object> { ["minimumConfirmations"] = 0 },
                PaymentProcessing = poolEnabled.HasValue
                    ? new PoolPaymentProcessingConfig { Enabled = poolEnabled.Value } : null };
            var config = new ClusterConfig { Pools = new[] { pool },
                PaymentProcessing = clusterEnabled.HasValue
                    ? new ClusterPaymentProcessingConfig { Enabled = clusterEnabled.Value } : null };
            if(clusterEnabled == true && poolEnabled == true)
                Assert.Throws<PoolStartupException>(() => Program.ValidateBitcoinConfirmationDeployment(config));
            else
                Program.ValidateBitcoinConfirmationDeployment(config);
            pool.Template = new BitcoinBlake2bTemplate { Family = CoinFamily.BitcoinBlake2b };
            Program.ValidateBitcoinConfirmationDeployment(config);
        }
    }

    [Fact]
    public void CustodialThreshold_ProductionStartupChecksBeforeReservingListeners()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while(dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "Miningcore", "Program.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Miningcore", "Program.cs"));
        var assignment = source.IndexOf("AssignPoolTemplatesAndLogPaymentExtraOmissions(enabledPools,", StringComparison.Ordinal);
        var validation = source.IndexOf("ValidateBitcoinConfirmationDeployment(clusterConfig);", StringComparison.Ordinal);
        var listeners = source.IndexOf("listenerCoordinator.ReserveAllAsync(", StringComparison.Ordinal);
        Assert.True(assignment >= 0 && assignment < validation && validation < listeners);
    }
}
