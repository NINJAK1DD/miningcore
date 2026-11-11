using System;
using System.IO;
using System.Collections.Generic;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Time;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public partial class MergedMiningManagerReorgTests
{
    [Fact]
    public void MergedMining_PreflightChecksAllPoolsBeforeListenerReservation()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while(directory != null && !File.Exists(Path.Combine(directory.FullName, "src", "Miningcore", "Program.cs")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "src", "Miningcore", "Program.cs"));
        var assignment = source.IndexOf("AssignPoolTemplatesAndLogPaymentExtraOmissions(enabledPools,", StringComparison.Ordinal);
        var validation = source.IndexOf("ValidateMergedMiningDeployment(clusterConfig, requireAssignedTemplates: true);", StringComparison.Ordinal);
        var listeners = source.IndexOf("listenerCoordinator.ReserveAllAsync(", StringComparison.Ordinal);
        Assert.True(assignment >= 0 && assignment < validation && validation < listeners);
    }

    [Theory]
    [InlineData("parent-coin", true)]
    [InlineData("aux-coin", true)]
    [InlineData("aux-family", true)]
    [InlineData("missing-parent-template", true)]
    [InlineData("missing-aux-template", true)]
    [InlineData("missing-id", false)]
    [InlineData("unknown-id", false)]
    [InlineData("same-id", false)]
    [InlineData("disabled-aux", false)]
    [InlineData("missing-address", false)]
    [InlineData("missing-daemon", false)]
    [InlineData("address-parameter", false)]
    [InlineData("aux-beneficiary", false)]
    [InlineData("parent-payment", false)]
    [InlineData("aux-payment", false)]
    [InlineData("parent-scheme", false)]
    [InlineData("aux-scheme", false)]
    public void InvalidMergedConfiguration_AgreesAcrossEveryStartupBoundary(string invalid, bool templateOnly)
    {
        var (parent, auxiliary, cluster) = CreateConfig();
        cluster.Persistence = new() { Postgres = new() };
        var merged = (IDictionary<string, object>) parent.Extra["mergedMining"];
        switch(invalid)
        {
            case "parent-coin": ((BitcoinTemplate) parent.Template).Symbol = "BTC"; break;
            case "aux-coin": ((BitcoinTemplate) auxiliary.Template).Symbol = "BTC"; break;
            case "aux-family": auxiliary.Template = new EthereumCoinTemplate { Family = CoinFamily.Ethereum }; break;
            case "missing-parent-template": parent.Template = null; break;
            case "missing-aux-template": auxiliary.Template = null; break;
            case "missing-id": merged["auxPoolId"] = ""; break;
            case "unknown-id": merged["auxPoolId"] = "not-found"; break;
            case "same-id": merged["auxPoolId"] = parent.Id; break;
            case "disabled-aux": auxiliary.Enabled = false; break;
            case "missing-address": auxiliary.Address = ""; break;
            case "missing-daemon": auxiliary.Daemons = null; break;
            case "address-parameter": merged["addressParameter"] = "d"; break;
            case "aux-beneficiary": auxiliary.PaymentProcessing.PayoutScheme = PayoutScheme.PROP; merged["requireAuxAddress"] = false; break;
            case "parent-payment": parent.PaymentProcessing.Enabled = false; break;
            case "aux-payment": auxiliary.PaymentProcessing.Enabled = false; break;
            case "parent-scheme": parent.PaymentProcessing.PayoutScheme = PayoutScheme.PPBS; break;
            case "aux-scheme": auxiliary.PaymentProcessing.PayoutScheme = PayoutScheme.PPBS; break;
        }
        var parentTemplate = parent.Template;
        var auxiliaryTemplate = auxiliary.Template;
        parent.Template = auxiliary.Template = null;
        var early = Record.Exception(() => Program.ValidateMergedMiningDeployment(cluster));
        if(templateOnly) Assert.Null(early);
        else Assert.IsAssignableFrom<PoolStartupException>(early);
        parent.Template = parentTemplate;
        auxiliary.Template = auxiliaryTemplate;

        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var manager = new TestManager(container, Substitute.For<IMasterClock>(), new MessageBus(),
            Substitute.For<IExtraNonceProvider>(), Substitute.For<IBlockCandidateRecorder>());
        var preflight = Assert.IsAssignableFrom<PoolStartupException>(Record.Exception(() =>
            Program.ValidateMergedMiningDeployment(cluster, requireAssignedTemplates: true)));
        var startup = Assert.IsAssignableFrom<PoolStartupException>(Record.Exception(() => manager.Configure(parent, cluster)));
        Assert.Equal(preflight.Message, startup.Message);
        Assert.Equal(preflight.PoolId, startup.PoolId);
        Assert.Contains(preflight.PoolId, preflight.Message);
        if(early is PoolStartupException earlyFailure)
        {
            Assert.Equal(preflight.Message, earlyFailure.Message);
            Assert.Equal(preflight.PoolId, earlyFailure.PoolId);
        }
        // Assigned templates must be checked even when the early-call flag is false.
        if(invalid is "parent-coin" or "aux-coin" or "aux-family")
            Assert.Equal(preflight.Message, Record.Exception(() => Program.ValidateMergedMiningDeployment(cluster)).Message);
    }

    [Theory]
    [MemberData(nameof(SupportedPayoutPairs))]
    public void PausedDeployment_AndRealManagerConfigure_AgreeForEveryAccountingPair(
        PayoutScheme parentScheme, PayoutScheme auxiliaryScheme)
    {
        var (parent, auxiliary, cluster) = CreateConfig();
        parent.PaymentProcessing.PayoutScheme = parentScheme;
        auxiliary.PaymentProcessing.PayoutScheme = auxiliaryScheme;
        cluster.Persistence = new PersistenceConfig { Postgres = new PostgresConfig() };
        cluster.PaymentProcessing.Paused = true;
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();

        foreach(var topology in new[] { "direct", "sender", "receiver" })
        {
            cluster.ShareRelay = topology == "sender" ? new ShareRelayConfig() : null;
            cluster.ShareRelays = topology == "receiver" ? new[] { new ShareRelayEndpointConfig() } : null;
            Program.ValidateMergedMiningDeployment(cluster, requireAssignedTemplates: true);
            Program.ValidatePpsDeployment(cluster, requireAssignedTemplates: true);
            var manager = new TestManager(container, Substitute.For<IMasterClock>(), new MessageBus(),
                Substitute.For<IExtraNonceProvider>(), Substitute.For<IBlockCandidateRecorder>());
            manager.Configure(parent, cluster);
            Assert.False(Program.ShouldRunPaymentProcessor(cluster));
            Assert.True(Program.RequiresSynchronousBlockCandidatePersistence(cluster));
            if(parentScheme == PayoutScheme.PPS || auxiliaryScheme == PayoutScheme.PPS ||
               topology != "sender" && (parentScheme != PayoutScheme.SOLO || auxiliaryScheme != PayoutScheme.SOLO))
                Assert.True(Program.RequiresShareAccountingPersistence(cluster));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void OldPauseConfiguration_IsRejectedByPreflightAndActualManager(int disabledIndex)
    {
        var (parent, _, cluster) = CreateConfig();
        cluster.Persistence = new PersistenceConfig { Postgres = new PostgresConfig() };
        cluster.Pools[disabledIndex].PaymentProcessing.Enabled = false;
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var manager = new TestManager(container, Substitute.For<IMasterClock>(), new MessageBus(),
            Substitute.For<IExtraNonceProvider>(), Substitute.For<IBlockCandidateRecorder>());

        var preflight = Assert.Throws<PoolStartupException>(() => Program.ValidateMergedMiningDeployment(cluster));
        var startup = Assert.Throws<PoolStartupException>(() => manager.Configure(parent, cluster));
        Assert.Equal(preflight.Message, startup.Message);
        Assert.Equal(cluster.Pools[disabledIndex].Id, preflight.PoolId);
        Assert.Contains("paymentProcessing.paused=true", startup.Message);
    }
}
