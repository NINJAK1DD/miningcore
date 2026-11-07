using System;
using System.IO;
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
