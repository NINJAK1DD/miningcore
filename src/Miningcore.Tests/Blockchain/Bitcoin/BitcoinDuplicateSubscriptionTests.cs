using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain.BitcoinBlake2b;
using Miningcore.Time;
using NBitcoin;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Miningcore.Tests.Blockchain.Bitcoin;

[Collection(BitcoinCorePayoutIntegrationCollection.Name)]
public class BitcoinDuplicateSubscriptionTests : TestBase
{
    private readonly ITestOutputHelper output;
    public BitcoinDuplicateSubscriptionTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task OutstandingCanonicalProof_RemainsValidAfterDuplicateSubscribe()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: true, difficulty: 1e-7);
        await Subscribe(wire);
        Assert.True((await wire.RequestAsync("mining.authorize", "test.worker", ""))["result"].Value<bool>());
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var original = context.ExtraNonce1;
        var job = Assert.IsType<ProofJob>(Assert.Single(context.validJobs));
        var duplicate = await wire.RequestAsync("mining.subscribe", "proxy/retry", "resume-id");
        // On the old implementation RequestAsync consumes the duplicate response,
        // and this fence drains its set_difficulty/notify before submission.
        await wire.RequestAsync("mining.extranonce.subscribe");
        var proofContext = new BitcoinWorkerContext { ExtraNonce1 = original };
        proofContext.Init(context.Difficulty, null, clock);
        var proofWorker = new StratumConnection(new NLog.NullLogger(NLog.LogManager.LogFactory),
            container.Resolve<Microsoft.IO.RecyclableMemoryStreamManager>(), clock, "offline-proof", false);
        proofWorker.SetContext(proofContext);
        string nonce = null;
        for(uint i = 0; i < 100_000; i++)
        {
            try { job.Evaluate(proofWorker, i); }
            catch(StratumException ex) when(ex.Code == StratumError.LowDifficultyShare) { continue; }
            if(context.ExtraNonce1 != original)
            {
                try { job.Evaluate(wire.Connection, i); continue; }
                catch(StratumException ex) when(ex.Code == StratumError.LowDifficultyShare) { }
            }
            nonce = i.ToStringHex8();
            break;
        }
        Assert.NotNull(nonce);
        output.WriteLine("Issued extranonce={0}, current={1}, nonce={2}, original coinbase={3}, current coinbase={4}",
            original, context.ExtraNonce1, nonce, job.CoinbaseId(original), job.CoinbaseId(context.ExtraNonce1));
        var response = await wire.RequestAsync("mining.submit", "test.worker", job.JobId,
            "00000000000000", job.BlockTemplate.CurTime.ToStringHex8(), nonce);
        output.WriteLine("Outstanding proof response: {0}", response);
        Assert.True(response["result"].Value<bool>(), response.ToString());
        Assert.Equal(original, context.ExtraNonce1);
        Assert.Equal((int) StratumError.Other, duplicate["error"]["code"].Value<int>());
        Assert.Equal(1, context.Stats.ValidShares);
        Assert.Equal(0, context.Stats.InvalidShares);
        Assert.Equal(1, wire.JobsCreated);
        bus.Received(1).SendMessage(Arg.Is<Share>(x => x.Miner == "test" && x.Difficulty == 1e-7), Arg.Any<string>());
    }

    [Theory]
    [InlineData("bitcoin", false)]
    [InlineData("bitcoin", true)]
    [InlineData("litecoin", false)]
    [InlineData("dogecoin", false)]
    [InlineData("bitcoin-cash", false)]
    public async Task BitcoinFamily_DuplicatePreservesAssignmentAndProxyExtensions_ThenBoundsReplies(string template, bool asicBoost)
    {
        var (config, manager, clock, bus) = Fixture(template);
        config.EnableAsicBoost = asicBoost;
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        // Proxy session-resume parameters remain accepted on the initial subscribe.
        await wire.SendRequestAsync("mining.subscribe", "test/proxy", "previous-connection-id");
        var initial = await wire.ReadAsync();
        Assert.Equal(7, initial["result"][2].Value<int>());
        Assert.Equal(asicBoost ? JTokenType.Null : (JTokenType?) null, initial["error"]?.Type);
        await wire.ReadAsync();
        await wire.ReadAsync();
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        context.VersionRollingMask = 0x1fffe000;
        var varDiff = context.VarDiff = new Miningcore.VarDiff.VarDiffContext();
        context.EnqueueNewDifficulty(2e-9);
        var jobs = context.validJobs.ToArray();
        var extraNonce = context.ExtraNonce1;
        var userAgent = context.UserAgent;
        // Invalid duplicate params must be rejected before conversion, too.
        await wire.SendRawAsync("{\"id\":100,\"method\":\"mining.subscribe\",\"params\":{\"unexpected\":true}}");
        var warning = await wire.ReadAsync();
        Assert.Equal(100, warning["id"].Value<int>());
        Assert.Equal((int) StratumError.Other, warning["error"]["code"].Value<int>());
        Assert.False(warning["result"].Value<bool>());
        // Strict FIFO fences prove the duplicate emitted no hidden notifications.
        await wire.SendRequestAsync("mining.extranonce.subscribe");
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.Equal(extraNonce, context.ExtraNonce1);
        Assert.Equal(userAgent, context.UserAgent);
        Assert.Equal(0x1fffe000u, context.VersionRollingMask);
        Assert.Same(varDiff, context.VarDiff);
        Assert.True(context.HasPendingDifficulty);
        Assert.Equal(1e-9, context.Difficulty);
        Assert.Equal(1, wire.JobsCreated);
        await wire.SendDisconnectingBatchAsync(string.Join("\n", Enumerable.Range(0, 20).Select(i =>
            $"{{\"id\":{200 + i},\"method\":\"mining.subscribe\",\"params\":[\"repeat\"]}}")));
        await wire.AssertNoMoreMessagesAsync();
        await wire.DispatchBufferedAsync("mining.subscribe", "buffered");
        Assert.Equal(extraNonce, context.ExtraNonce1);
        Assert.Equal(1, wire.JobsCreated);
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x => x.Info == "duplicate-subscribe"), Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingIds_DoNotConsumeWarning_AndConnectionsHaveIndependentAllowances(bool omitId)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var first = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        var invalid = omitId ? "{\"method\":\"mining.subscribe\",\"params\":[]}" :
            "{\"id\":null,\"method\":\"mining.subscribe\",\"params\":[]}";
        await MissingId(first, invalid);
        Assert.False(first.Connection.Context.IsSubscribed);
        Assert.Null(first.Connection.ContextAs<BitcoinWorkerContext>().ExtraNonce1);
        await Subscribe(first);
        await MissingId(first, invalid);
        await first.SendRequestAsync("mining.subscribe", "duplicate");
        Assert.Equal((int) StratumError.Other, (await first.ReadAsync())["error"]["code"].Value<int>());
        await MissingId(first, invalid);
        await using var second = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            sharedPool: first, canonical: true);
        await Subscribe(second);
        Assert.NotEqual(first.Connection.ContextAs<BitcoinWorkerContext>().ExtraNonce1,
            second.Connection.ContextAs<BitcoinWorkerContext>().ExtraNonce1);
        await second.SendRequestAsync("mining.subscribe", "duplicate");
        Assert.Equal((int) StratumError.Other, (await second.ReadAsync())["error"]["code"].Value<int>());
        await first.SendRequestAsync("mining.authorize", "test.worker", "");
        Assert.True((await first.ReadAsync())["result"].Value<bool>());
        await first.SendRequestAsync("mining.subscribe", "repeat");
        await first.AssertNoMoreMessagesAsync();
        Assert.True((await second.RequestAsync("mining.extranonce.subscribe"))["result"].Value<bool>());
    }

    private static async Task MissingId(BitcoinBlake2bWireSession wire, string request)
    {
        await wire.SendRawAsync(request);
        Assert.Equal((int) StratumError.MinusOne, (await wire.ReadAsync())["error"]["code"].Value<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectCoinbase_DuplicatePreservesAuthorizationAndInFlightAccounting(bool authorizeFirst)
    {
        var (config, manager, clock, bus) = Fixture(direct: true);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: true, difficulty: 1e-7);
        var address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        if(authorizeFirst)
            Assert.True((await wire.RequestAsync("mining.authorize", address + ".worker", ""))["result"].Value<bool>());
        await wire.SendRequestAsync("mining.subscribe", "direct/miner");
        Assert.NotNull((await wire.ReadAsync())["result"]);
        Assert.Equal("mining.set_difficulty", (await wire.ReadAsync())["method"].Value<string>());
        if(!authorizeFirst)
        {
            // No job until successful network-aware authorization. A stray
            // duplicate must not bypass this boundary or create any job.
            await wire.SendRequestAsync("mining.subscribe", "duplicate");
            Assert.Equal((int) StratumError.Other, (await wire.ReadAsync())["error"]["code"].Value<int>());
            Assert.Equal(0, wire.JobsCreated);
            Assert.True((await wire.RequestAsync("mining.authorize", address + ".worker", ""))["result"].Value<bool>());
        }
        Assert.Equal("mining.notify", (await wire.ReadAsync())["method"].Value<string>());
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var authorization = context.GetDirectPayoutAuthorization();
        var extraNonce = context.ExtraNonce1;
        var job = Assert.IsType<ProofJob>(Assert.Single(context.validJobs));
        Assert.Equal(address.ToString(), job.DirectPayoutAddress);
        Assert.Equal(address.ScriptPubKey.ToHex(), job.DirectCoinbaseSettlement.MinerScriptPubKey);
        Assert.Equal(authorization.Generation, job.DirectPayoutGeneration);
        var nonce = FindProof(job, wire.Connection);
        var admitted = new TaskCompletionSource<Share>(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.When(x => x.SendMessage(Arg.Any<Share>(), Arg.Any<string>())).Do(call =>
        {
            var share = call.ArgAt<Share>(0);
            share.SetPersistenceAdmission(persistence.Task);
            admitted.TrySetResult(share);
        });
        await wire.SendRequestAsync("mining.submit", address + ".worker", job.JobId,
            "00000000000000", job.BlockTemplate.CurTime.ToStringHex8(), nonce);
        var share = await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(address.ToString(), share.Miner);
            Assert.Equal(0, context.Stats.ValidShares);
            if(authorizeFirst)
                await wire.SendRequestAsync("mining.subscribe", "duplicate");
            Assert.Same(authorization, context.GetDirectPayoutAuthorization());
            Assert.Same(job, context.GetJob(job.JobId));
            Assert.Equal(extraNonce, context.ExtraNonce1);
        }
        finally { persistence.TrySetResult(); }
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        if(authorizeFirst)
            Assert.Equal((int) StratumError.Other, (await wire.ReadAsync())["error"]["code"].Value<int>());
        // The next FIFO response witnesses completed accounting and unchanged work.
        await wire.SendRequestAsync("mining.extranonce.subscribe");
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        Assert.Equal(1, context.Stats.ValidShares);
        Assert.Equal(0, context.Stats.InvalidShares);
        Assert.Equal(1, context.AcceptedProofSequence);
        Assert.Equal(1, wire.JobsCreated);
        Assert.Same(authorization, context.GetDirectPayoutAuthorization());
        bus.Received(1).SendMessage(Arg.Any<Share>(), Arg.Any<string>());
    }

    private static string FindProof(ProofJob job, StratumConnection worker)
    {
        for(uint i = 0; i < 100_000; i++)
        {
            try
            {
                var share = job.Evaluate(worker, i);
                if(!share.IsBlockCandidate)
                    return i.ToStringHex8();
            }
            catch(StratumException ex) when(ex.Code == StratumError.LowDifficultyShare) { }
        }
        throw new InvalidOperationException("No easy share found");
    }

    [BitcoinCoreIntegrationFact]
    public async Task BitcoinCore_AcceptsOutstandingCustodialAndDirectBlocksAfterDuplicateSubscribe()
    {
        await using var node = await BitcoinPayoutHandlerRegtestTests.BitcoinCoreRegtestNode.StartAsync(walletBroadcast: true);
        foreach(var direct in new[] { false, true })
        {
            var (config, manager, clock, bus) = Fixture(direct: direct);
            config.Daemons = new[] { node.WalletEndpoint };
            manager.Configure(config, new ClusterConfig());
            manager.ValidateWithDaemon = true;
            var template = (await node.RootRpcAsync("getblocktemplate", new JObject
                { ["rules"] = new JArray("segwit") })).ToObject<BlockTemplate>();
            manager.Initialize(template);
            await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
                canonical: true, difficulty: 1e-9);
            var address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
            Assert.True((await wire.RequestAsync("mining.authorize", address + ".worker", ""))["result"].Value<bool>());
            await Subscribe(wire);
            var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
            var original = context.ExtraNonce1;
            var job = Assert.IsType<ProofJob>(Assert.Single(context.validJobs));
            string nonce = null;
            for(uint i = 0; i < 100_000; i++)
            {
                if(job.Evaluate(wire.Connection, i).IsBlockCandidate)
                {
                    nonce = i.ToStringHex8();
                    break;
                }
            }
            Assert.NotNull(nonce);
            await wire.SendRequestAsync("mining.subscribe", "proxy/retry");
            Assert.Equal((int) StratumError.Other, (await wire.ReadAsync())["error"]["code"].Value<int>());
            var response = await wire.RequestAsync("mining.submit", address + ".worker", job.JobId,
                "00000000000000", template.CurTime.ToStringHex8(), nonce);
            Assert.True(response["result"].Value<bool>(), response.ToString());
            Assert.Equal(original, context.ExtraNonce1);
            Assert.Equal(template.Height, (await node.RootRpcAsync("getblockcount")).Value<uint>());
            var accepted = await node.RootRpcAsync("getblock", (await node.RootRpcAsync("getbestblockhash")).Value<string>(), 2);
            var coinbase = accepted["tx"][0];
            Assert.Equal(job.CoinbaseId(original), coinbase["txid"].Value<string>());
            var expectedDestination = direct ? address : BitcoinAddress.Create(config.Address, Network.RegTest);
            Assert.Equal(expectedDestination.ScriptPubKey.ToHex(), coinbase["vout"][0]["scriptPubKey"]["hex"].Value<string>());
            Assert.Equal(1, context.Stats.ValidShares);
            Assert.Equal(0, context.Stats.InvalidShares);
            if(direct)
                await manager.Recorder.Received(1).PersistDirectBlockSubmissionAsync(Arg.Is<Share>(x =>
                    x.DirectMinerScriptPubKey == address.ScriptPubKey.ToHex()));
            output.WriteLine("Bitcoin Core accepted {0} block {1} after duplicate subscribe; original coinbase {2}",
                direct ? "direct" : "custodial", template.Height, job.CoinbaseId(original));
        }
    }

    private static async Task Subscribe(BitcoinBlake2bWireSession wire)
    {
        var response = await wire.RequestAsync("mining.subscribe", "test/miner");
        Assert.Equal(7, response["result"][2].Value<int>());
        Assert.Equal("mining.set_difficulty", (await wire.ReadAsync())["method"].Value<string>());
        Assert.Equal("mining.notify", (await wire.ReadAsync())["method"].Value<string>());
    }

    private (PoolConfig, FixtureManager, IMasterClock, IMessageBus) Fixture(string template = "bitcoin", bool direct = false)
    {
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var bus = Substitute.For<IMessageBus>();
        var config = new PoolConfig
        {
            Id = "duplicate-subscription", Coin = template, Template = ModuleInitializer.CoinTemplates[template],
            Address = new Key().PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.RegTest).ToString(),
            Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 1 } },
            Banning = new PoolShareBasedBanningConfig { Enabled = false },
            Extra = new Dictionary<string, object> { ["soloCoinbasePayout"] = direct },
        };
        var manager = new FixtureManager(container, clock, bus);
        manager.Configure(config, new ClusterConfig());
        manager.Initialize(new BlockTemplate
        {
            Height = 21, Version = 0x20000000, CurTime = (uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Bits = "1d00ffff", Target = "00000000ffff" + new string('0', 52),
            PreviousBlockhash = new string('0', 64), CoinbaseValue = 5000000000,
            Transactions = Array.Empty<BitcoinBlockTransaction>(),
        });
        return (config, manager, clock, bus);
    }

    private sealed class ProofJob : BitcoinJob
    {
        internal Share Evaluate(StratumConnection worker, uint nonce) =>
            ProcessShareInternal(worker, "00000000000000", BlockTemplate.CurTime, nonce, null).Share;
        internal string CoinbaseId(string extraNonce) => NBitcoin.Transaction.Parse(
            SerializeCoinbase(extraNonce, "00000000000000").ToHexString(), Network.RegTest).GetHash().ToString();
    }

    private sealed class FixtureManager : BitcoinJobManager
    {
        internal FixtureManager(IComponentContext ctx, IMasterClock clock, IMessageBus bus) :
            this(ctx, clock, bus, Substitute.For<IBlockCandidateRecorder>()) { }
        private FixtureManager(IComponentContext ctx, IMasterClock clock, IMessageBus bus, IBlockCandidateRecorder recorder) :
            base(ctx, clock, bus, new BitcoinExtraNonceProvider("duplicate-subscription", null), recorder)
        {
            Recorder = recorder;
            recorder.PersistDirectBlockSubmissionAsync(Arg.Any<Share>()).Returns(Task.FromResult(new DirectBlockSubmissionPreparation()));
        }
        internal IBlockCandidateRecorder Recorder { get; }
        internal bool ValidateWithDaemon { get; set; }
        protected override BitcoinJob CreateJob() => new ProofJob();
        internal void Initialize(BlockTemplate template)
        {
            network = Network.RegTest;
            hasSubmitBlockMethod = true;
            poolAddressDestination = BitcoinAddress.Create(poolConfig.Address, network);
            currentJob = CreateJob();
            currentJob.Init(template, "outstanding", poolConfig, extraPoolConfig, clusterConfig, clock,
                poolAddressDestination, network, false, coin.ShareMultiplier, coin.CoinbaseHasherValue,
                coin.HeaderHasherValue, coin.BlockHasherValue);
        }
        public override Task<bool> ValidateAddressAsync(string address, CancellationToken ct) =>
            ValidateWithDaemon ? base.ValidateAddressAsync(address, ct) : Task.FromResult(true);
    }
}
