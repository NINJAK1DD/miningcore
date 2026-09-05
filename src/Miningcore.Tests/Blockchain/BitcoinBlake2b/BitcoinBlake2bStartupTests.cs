using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Tests.Blockchain.Bitcoin;
using Miningcore.Time;
using NBitcoin;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

[Collection(BitcoinCorePayoutIntegrationCollection.Name)]
public partial class BitcoinBlake2bStartupTests : TestBase
{
    private ILifetimeScope Scope() => container.BeginLifetimeScope(builder =>
    {
        builder.RegisterInstance(Substitute.For<IConnectionFactory>());
        builder.RegisterInstance(Substitute.For<IStatsRepository>());
        builder.RegisterInstance(Substitute.For<Miningcore.Persistence.Repositories.IBlockRepository>());
        builder.RegisterInstance(Substitute.For<IShareRepository>());
        builder.RegisterInstance(Substitute.For<IBlockCandidateRecorder>());
        builder.RegisterInstance(Substitute.For<IMessageBus>());
        builder.RegisterInstance(new NicehashService(Substitute.For<IHttpClientFactory>(), Substitute.For<IMemoryCache>()));
        builder.RegisterType<StandardClock>().As<IMasterClock>();
    });

    [Fact]
    public void ProductionContainer_ResolvesIsolatedPoolAndManager()
    {
        using var scope = Scope();
        Assert.IsType<BitcoinBlake2bPool>(ResolvePool(scope));
        Assert.IsType<BitcoinBlake2bJobManager>(scope.Resolve<BitcoinBlake2bJobManager>(
            new TypedParameter(typeof(IExtraNonceProvider), new BitcoinBlake2bExtraNonceProvider())));
    }

    private static BitcoinBlake2bPool ResolvePool(ILifetimeScope scope) => Assert.IsType<BitcoinBlake2bPool>(
        Assert.Single(scope.Resolve<IEnumerable<Meta<Lazy<IMiningPool, CoinFamilyAttribute>>>>()
            .Where(x => x.Value.Metadata.SupportedFamilies.Contains(CoinFamily.BitcoinBlake2b))).Value.Value);

    [BitcoinBlake2bIntegrationFact]
    public async Task ProductionContainer_SetupJobManagerReachesRealDaemonAndPublishesJob()
    {
        await using var node = await BitcoinPayoutHandlerRegtestTests.BitcoinCoreRegtestNode.StartAsync(
            true, Environment.GetEnvironmentVariable(BitcoinBlake2bIntegrationFactAttribute.BinaryEnvironmentVariable),
            new[] { "-testactivationheight=blake2b@20", "-blake2b_headline=Miningcore BLAKE2b regtest" }, 19);
        using var scope = Scope();
        var pool = ResolvePool(scope);
        pool.Configure(new PoolConfig
        {
            Id = "blake2b-container", Coin = "bitcoin-blake2b",
            Template = ModuleInitializer.CoinTemplates["bitcoin-blake2b"],
            Address = await node.GetNewAddressAsync(), Daemons = new[] { node.WalletEndpoint },
            EnableInternalStratum = true, BlockRefreshInterval = 100,
            Extra = new Dictionary<string, object> { ["allowPeerlessRegtest"] = true },
        }, new ClusterConfig());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            // Invoke the real protected lifecycle method on the resolved pool:
            // no manager constructor, subclass, or registration is substituted.
            await ((Task) typeof(BitcoinBlake2bPool).GetMethod("SetupJobManager",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pool, new object[] { stop.Token })!);
            var manager = Assert.IsType<BitcoinBlake2bJobManager>(typeof(BitcoinPool)
                .GetField("manager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool));
            Assert.IsType<BitcoinBlake2bJob>(manager.GetJobForStratum());
        }
        finally
        {
            stop.Cancel();
            ((IDisposable) typeof(PoolBase).GetField("disposables",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!).Dispose();
        }
    }

    [Theory]
    [InlineData(false, 0.999, false)]
    [InlineData(false, 1, true)]
    [InlineData(false, 64, true)]
    [InlineData(true, 1e-9, true)]
    [InlineData(true, 0, false)]
    public void ShareDifficulty_EnforcesMainnetFloor(bool regtest, double difficulty, bool accepted)
    {
        void Validate() => BitcoinBlake2bJobManager.ValidateDifficultyForNetwork(difficulty,
            regtest ? Network.RegTest : Network.Main);
        if(accepted) Validate();
        else Assert.Throws<ArgumentOutOfRangeException>(Validate);
    }

    [Theory]
    [InlineData("Miningcore", true)]
    [InlineData("123456789012345678901234", true)]
    [InlineData("1234567890123456789012345", false)]
    [InlineData("éééééééééééé", true)]
    [InlineData("ééééééééééééé", false)]
    [InlineData("1234567890123456789012345678901234567890", false)]
    [InlineData("éééééééééééééééééééééééééééééé", false)]
    public void Configure_ReservesActivationCoinbaseBudgetBeforeDaemonAccess(string marker, bool accepted)
    {
        var manager = new BitcoinBlake2bJobManager(container, Substitute.For<IMasterClock>(),
            Substitute.For<IMessageBus>(), new BitcoinBlake2bExtraNonceProvider());
        var config = new PoolConfig { Id = "blake2b-budget", Coin = "bitcoin-blake2b",
            Template = ModuleInitializer.CoinTemplates["bitcoin-blake2b"],
            Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 1 } } };
        var cluster = new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig { CoinbaseString = marker } };
        if(accepted) manager.Configure(config, cluster);
        else Assert.Contains("activation scriptSig budget", Assert.Throws<PoolStartupException>(() => manager.Configure(config, cluster)).Message);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("minimum")]
    [InlineData("maximum")]
    public void NetworkIdentification_RejectsEverySubFloorConfiguredTargetBeforeJobs(string field)
    {
        var endpoint = new PoolEndpoint { Difficulty = 64,
            VarDiff = new VarDiffConfig { MinDiff = 1, MaxDiff = 128 } };
        if(field == "endpoint") endpoint.Difficulty = 1e-9;
        if(field == "minimum") endpoint.VarDiff.MinDiff = 1e-9;
        if(field == "maximum") endpoint.VarDiff.MaxDiff = 1e-9;
        var manager = new ActivationManager(container, Substitute.For<IMasterClock>());
        manager.Configure(new PoolConfig { Id = "blake2b-floor", Coin = "bitcoin-blake2b",
            Template = ModuleInitializer.CoinTemplates["bitcoin-blake2b"],
            Ports = new Dictionary<int, PoolEndpoint> { [3333] = endpoint },
            Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 1 } } }, new ClusterConfig());
        manager.Identify(Network.RegTest);
        Assert.Contains("unsupported Bitcoin BLAKE2b mainnet difficulty",
            Assert.Throws<PoolStartupException>(() => manager.Identify(Network.Main)).Message);
    }

    [Fact]
    public async Task ActivationRpc_TransientFailureBacksOffThenRecoversWithoutPublishingUnverifiedWork()
    {
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ => now);
        var manager = new ActivationManager(container, clock);
        manager.Configure(new PoolConfig { Id = "blake2b-retry", Coin = "bitcoin-blake2b",
            Template = ModuleInitializer.CoinTemplates["bitcoin-blake2b"],
            Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 1 } } }, new ClusterConfig());
        var contract = ((BitcoinBlake2bTemplate) ModuleInitializer.CoinTemplates["bitcoin-blake2b"]).Networks["regtest"];
        var template = new BlockTemplate { Height = 20, Bits = "207fffff", PreviousBlockhash = new string('0', 64) };
        var cause = new TimeoutException("RPC timeout");
        manager.Response = new(null, new JsonRpcError(-500, "RPC timeout", null, cause));
        Assert.NotNull((await manager.VerifyActivationParentAsync(template, contract, CancellationToken.None)).Error);
        var deferred = (await manager.VerifyActivationParentAsync(template, contract, CancellationToken.None)).Error;
        Assert.Contains("activation-parent retry deferred for 1s; no new RPC attempted", deferred.Message);
        Assert.Same(cause, deferred.InnerException);
        Assert.Equal(1, manager.Calls);
        now = now.AddSeconds(1);
        manager.Response = new(new JObject { ["bits"] = "207fffff" });
        var success = await manager.VerifyActivationParentAsync(template, contract, CancellationToken.None);
        Assert.Null(success.Error);
        Assert.Same(template, success.Response);
        Assert.Equal(2, manager.Calls);
        manager.Response = new(new JObject { ["bits"] = "not-hex!" });
        await Assert.ThrowsAsync<PoolStartupException>(() => manager.VerifyActivationParentAsync(template, contract, CancellationToken.None));
    }

    private sealed class ActivationManager : BitcoinBlake2bJobManager
    {
        internal RpcResponse<JObject> Response;
        internal int Calls;
        internal CancellationTokenSource CancelDuringRead;
        internal ActivationManager(IComponentContext ctx, IMasterClock clock) : base(ctx, clock,
            Substitute.For<IMessageBus>(), new BitcoinBlake2bExtraNonceProvider()) => network = Network.RegTest;
        internal void Identify(Network selected) { network = selected; PostChainIdentifyConfigure(); }
        protected override Task<RpcResponse<JObject>> GetActivationParentAsync(string hash, CancellationToken ct)
        {
            Calls++;
            CancelDuringRead?.Cancel();
            return Task.FromResult(Response);
        }
    }

    [Fact]
    public async Task ActivationRpc_CancelledErrorResponseDoesNotArmBackoff()
    {
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var manager = new ActivationManager(container, clock);
        manager.Configure(LifecycleConfig(), new ClusterConfig());
        var contract = ((BitcoinBlake2bTemplate) ModuleInitializer.CoinTemplates["bitcoin-blake2b"]).Networks["regtest"];
        var template = new BlockTemplate { Height = 20, Bits = "207fffff", PreviousBlockhash = new string('0', 64) };
        using var stop = new CancellationTokenSource();
        manager.CancelDuringRead = stop;
        manager.Response = new(null, new JsonRpcError(-500, "Cancelled", null));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.VerifyActivationParentAsync(template, contract, stop.Token));
        manager.CancelDuringRead = null;
        manager.Response = new(new JObject { ["bits"] = "207fffff" });
        Assert.Null((await manager.VerifyActivationParentAsync(template, contract, CancellationToken.None)).Error);
        Assert.Equal(2, manager.Calls);
    }
}
