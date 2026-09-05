using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Rpc;
using Miningcore.Time;
using NBitcoin;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bStartupTests
{
    private static PoolConfig LifecycleConfig() => new()
    {
        Id = "blake2b-lifecycle", Coin = "bitcoin-blake2b",
        Template = ModuleInitializer.CoinTemplates["bitcoin-blake2b"],
        Address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString(),
        Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 1 } },
        EnableInternalStratum = true, BlockRefreshInterval = 50, JobRebroadcastTimeout = 1,
    };

    [Fact]
    public async Task ForcedRebroadcast_BeforeActivationParentRecoveryPublishesNoNullJob()
    {
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ => now);
        var manager = new LifecycleManager(container, clock) { ParentUnavailable = true };
        var failStop = Substitute.For<IMiningFailStopCoordinator>();
        using var dependencies = Scope();
        using var scope = dependencies.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(manager).As<BitcoinBlake2bJobManager>();
            builder.RegisterInstance(failStop);
        });
        var pool = ResolvePool(scope);
        pool.Configure(LifecycleConfig(), new ClusterConfig());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var setup = (Task) typeof(BitcoinBlake2bPool).GetMethod("SetupJobManager",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pool, new object[] { stop.Token })!;
        try
        {
            // This is the real timer/Concat/Jobs/pool subscription path, not
            // a direct call to the activation-parent helper. Waiting for the
            // forced UpdateJob proves at least one rebroadcast interval ran.
            await manager.ForcedRefresh.Task.WaitAsync(stop.Token);
            await Task.Delay(100, stop.Token); // allow the observable's downstream subscriber to run
            Assert.Equal(0, manager.Publications);
            Assert.False(setup.IsCompleted); // PoolBase cannot proceed to listener activation.
            Assert.Empty(failStop.ReceivedCalls());
            manager.ParentUnavailable = false;
            now = now.AddSeconds(31); // expire activation RPC backoff
            await setup.WaitAsync(stop.Token);
            Assert.Equal(1, manager.Publications);
            Assert.IsType<BitcoinBlake2bJob>(manager.GetJobForStratum());
            Assert.Empty(failStop.ReceivedCalls());
        }
        finally
        {
            stop.Cancel();
            ((IDisposable) typeof(PoolBase).GetField("disposables",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!).Dispose();
            try { await setup; } catch(OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("version", true)]
    [InlineData("chain", true)]
    [InlineData("deployment", true)]
    [InlineData("version", false)]
    [InlineData("chain", false)]
    [InlineData("deployment", false)]
    public async Task RuntimeAttestation_RejectsRecoveredAndSeamlessDaemonDrift(string drift, bool outage)
    {
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ => now);
        var manager = new LifecycleManager(container, clock);
        manager.Configure(LifecycleConfig(), new ClusterConfig());
        manager.Prepare();
        await manager.RefreshAsync();
        var verified = manager.GetJobForStratum();
        Assert.NotNull(verified);
        Assert.Equal(3, manager.AttestationCalls);
        if(outage)
        {
            manager.TemplateUnavailable = true;
            await manager.RefreshAsync();
            manager.TemplateUnavailable = false;
        }
        else now = now.Add(BitcoinBlake2bJobManager.DaemonAttestationLifetime);
        manager.Drift = drift;
        await Assert.ThrowsAsync<PoolStartupException>(() => manager.RefreshAsync());
        Assert.Same(verified, manager.GetJobForStratum());
    }

    [Fact]
    public async Task RuntimeAttestation_CachesSuccessButRetriesTransportFailuresBeforeNewWork()
    {
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ => now);
        var manager = new LifecycleManager(container, clock);
        manager.Configure(LifecycleConfig(), new ClusterConfig());
        manager.Prepare();
        await manager.RefreshAsync();
        await manager.RefreshAsync();
        Assert.Equal(3, manager.AttestationCalls);
        now = now.AddSeconds(30);
        manager.AttestationUnavailable = true;
        var verified = manager.GetJobForStratum();
        await manager.RefreshAsync();
        Assert.Same(verified, manager.GetJobForStratum());
        manager.AttestationUnavailable = false;
        now = now.AddSeconds(1);
        await manager.RefreshAsync();
        Assert.Equal(7, manager.AttestationCalls);
        Assert.NotSame(verified, manager.GetJobForStratum());
    }

    private sealed class LifecycleManager : BitcoinBlake2bJobManager
    {
        // Only ParentUnavailable crosses the timer/test-thread boundary.
        // Other switches are changed between awaited, directly driven refreshes.
        internal volatile bool ParentUnavailable;
        internal bool TemplateUnavailable;
        internal bool TemplateThrows;
        internal bool AttestationUnavailable;
        internal string FailedAttestationMethod;
        internal bool AttestationThrows;
        internal string Drift;
        internal int Publications;
        internal int AttestationCalls;
        internal readonly TaskCompletionSource ForcedRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LifecycleManager(IComponentContext ctx, IMasterClock clock) : base(ctx, clock,
            Substitute.For<IMessageBus>(), new BitcoinBlake2bExtraNonceProvider()) { }
        internal void Prepare()
        {
            network = Network.RegTest;
            poolAddressDestination = BitcoinAddress.Create(poolConfig.Address, network);
        }
        internal Task RefreshAsync() => RefreshResultAsync();
        internal Task<(bool IsNew, bool Force)> RefreshResultAsync() => UpdateJob(CancellationToken.None, true);
        protected override Task<bool> AreDaemonsHealthyAsync(CancellationToken ct) => Task.FromResult(true);
        protected override Task<bool> AreDaemonsConnectedAsync(CancellationToken ct) => Task.FromResult(true);
        protected override Task EnsureDaemonsSynchedAsync(CancellationToken ct) => Task.CompletedTask;
        protected override Task PostStartInitAsync(CancellationToken ct)
        {
            Prepare();
            SetupJobUpdates(ct);
            return Task.CompletedTask;
        }
        protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct,
            bool forceUpdate, string via = null, string json = null)
        {
            var result = await base.UpdateJob(ct, forceUpdate, via, json);
            if(forceUpdate) ForcedRefresh.TrySetResult();
            return result;
        }
        protected override object GetJobParamsForStratum(bool isNew)
        {
            Interlocked.Increment(ref Publications);
            return base.GetJobParamsForStratum(isNew);
        }
        protected override Task<RpcResponse<BlockTemplate>> FetchBlockTemplateAsync(CancellationToken ct) =>
            TemplateThrows ? throw new InvalidOperationException("failed refresh") :
            Task.FromResult(TemplateUnavailable ? new RpcResponse<BlockTemplate>(null, Unavailable()) : new(new BlockTemplate
            {
                Height = 20, Version = 0xa0000000, CurTime = 1700000000, Bits = "207fffff",
                Target = "7fffff" + new string('0', 58), PreviousBlockhash = new string('0', 64),
                CoinbaseValue = 5000000000, Transactions = Array.Empty<BitcoinBlockTransaction>(),
                Rules = new[] { "!blake2b" },
            }));
        protected override Task<RpcResponse<JObject>> GetActivationParentAsync(string hash, CancellationToken ct) =>
            Task.FromResult(ParentUnavailable ? new RpcResponse<JObject>(null, Unavailable()) : new(new JObject { ["bits"] = "207fffff" }));
        protected override Task<RpcResponse<JObject>> ReadDaemonAttestationAsync(string method, CancellationToken ct)
        {
            AttestationCalls++;
            if(AttestationUnavailable || method == FailedAttestationMethod)
            {
                if(AttestationThrows) throw new System.Net.Http.HttpRequestException("test transport failure");
                return Task.FromResult(new RpcResponse<JObject>(null, Unavailable()));
            }
            var response = method switch
            {
                BitcoinCommands.GetNetworkInfo => new JObject { ["version"] = Drift == "version" ? 300000 : 290401,
                    ["subversion"] = "/Satoshi:29.4.1/Knots:20260508/" },
                BitcoinCommands.GetBlockchainInfo => new JObject { ["chain"] = Drift == "chain" ? "main" : "regtest" },
                "getdeploymentinfo" => new JObject { ["blake2b"] = new JObject { ["height"] = 20, ["active"] = Drift != "deployment" } },
                _ => throw new InvalidOperationException(method),
            };
            return Task.FromResult(new RpcResponse<JObject>(response));
        }
        private static JsonRpcError Unavailable() => new(-500, "RPC unavailable", null);
    }

    [Theory]
    [InlineData(BitcoinCommands.GetNetworkInfo, 1, false)]
    [InlineData(BitcoinCommands.GetBlockchainInfo, 2, false)]
    [InlineData("getdeploymentinfo", 3, false)]
    [InlineData("getdeploymentinfo", 3, true)]
    public async Task RuntimeAttestation_BackoffIsBoundedAndResetsOnlyAfterCompleteSuccess(
        string failedMethod, int callsPerAttempt, bool throws)
    {
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ => now);
        var manager = new LifecycleManager(container, clock);
        manager.Configure(LifecycleConfig(), new ClusterConfig());
        manager.Prepare();
        await manager.RefreshAsync();
        var verified = manager.GetJobForStratum();
        now = now.AddSeconds(30);
        manager.FailedAttestationMethod = failedMethod;
        manager.AttestationThrows = throws;
        var expectedCalls = 3;
        foreach(var delay in new[] { 1, 2, 4, 8, 16, 30, 30 })
        {
            await manager.RefreshAsync();
            expectedCalls += callsPerAttempt;
            Assert.Equal(expectedCalls, manager.AttestationCalls);
            Assert.Same(verified, manager.GetJobForStratum());
            // A template outage must invalidate identity without erasing the
            // retry deadline, otherwise alternating outages defeat backoff.
            manager.TemplateUnavailable = true;
            await manager.RefreshAsync();
            manager.TemplateUnavailable = false;
            now = now.AddMilliseconds(delay * 1000 - 1);
            await manager.RefreshAsync();
            Assert.Equal(expectedCalls, manager.AttestationCalls);
            now = now.AddMilliseconds(1);
        }
        manager.FailedAttestationMethod = null;
        await manager.RefreshAsync();
        Assert.Equal(expectedCalls + 3, manager.AttestationCalls);
        Assert.NotSame(verified, manager.GetJobForStratum());
        now = now.AddSeconds(30);
        manager.FailedAttestationMethod = failedMethod;
        await manager.RefreshAsync();
        manager.FailedAttestationMethod = null;
        now = now.AddSeconds(1); // successful attestation reset the failure count
        await manager.RefreshAsync();
        Assert.Equal(expectedCalls + 6 + callsPerAttempt, manager.AttestationCalls);
        now = now.AddSeconds(30);
        manager.Drift = "version";
        await Assert.ThrowsAsync<PoolStartupException>(() => manager.RefreshAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedForcedRefresh_RebroadcastsOnlyAnExistingVerifiedJob(bool existing, bool exception)
    {
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var manager = new LifecycleManager(container, clock);
        manager.Configure(LifecycleConfig(), new ClusterConfig());
        manager.Prepare();
        if(existing) await manager.RefreshAsync();
        var previous = manager.GetJobForStratum();
        manager.TemplateUnavailable = !exception;
        manager.TemplateThrows = exception;
        var result = await manager.RefreshResultAsync();
        Assert.False(result.IsNew);
        Assert.Equal(existing, result.Force);
        Assert.Same(previous, manager.GetJobForStratum());
        if(existing)
        {
            manager.TemplateUnavailable = false;
            manager.TemplateThrows = false;
            manager.Drift = "version";
            // Both returned RPC errors and thrown transport failures expire
            // the cached identity, without waiting for its periodic deadline.
            await Assert.ThrowsAsync<PoolStartupException>(() => manager.RefreshAsync());
        }
    }
}
