using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain.BitcoinBlake2b;
using Miningcore.Time;
using Miningcore.VarDiff;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

[Collection(BitcoinCorePayoutIntegrationCollection.Name)]
public class BitcoinPublicationRegtestTests : TestBase
{
    [BitcoinCoreIntegrationFact]
    public Task CanonicalProof_RemainsAcceptedAfterPublicationFailureAndReconnect() => ExerciseAsync(false);

    [BitcoinCoreIntegrationFact]
    public Task DirectSoloProof_KeepsImmutableCreditAfterPublicationFailureAndReconnect() => ExerciseAsync(true);

    private async Task ExerciseAsync(bool direct)
    {
        await using var node = await BitcoinPayoutHandlerRegtestTests.BitcoinCoreRegtestNode.StartAsync(walletBroadcast: true);
        var template = (await node.RootRpcAsync("getblocktemplate", new JObject { ["rules"] = new JArray("segwit") }))
            .ToObject<BlockTemplate>();
        var miner = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var poolAddress = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var coin = (BitcoinTemplate) ModuleInitializer.CoinTemplates["bitcoin"];
        var config = new PoolConfig
        {
            Id = "publication-regtest-" + Guid.NewGuid().ToString("N"), Coin = "bitcoin", Template = coin,
            Address = poolAddress.ToString(), Daemons = new[] { node.WalletEndpoint },
            PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true, PayoutScheme = PayoutScheme.SOLO },
            Extra = new Dictionary<string, object> { ["soloCoinbasePayout"] = direct },
        };
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var bus = Substitute.For<IMessageBus>();
        var manager = new TemplateManager(container, clock, bus);
        manager.Configure(config, new ClusterConfig());
        manager.SetTemplate(template, poolAddress);

        // Keep the share target easier than regtest's block target so a genuine
        // accepted non-block proof can exercise ordinary share accounting.
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: true, difficulty: 1e-10);
        await HandshakeAsync(wire, miner.ToString(), direct);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var job = Assert.Single(context.validJobs);
        var authorization = context.GetDirectPayoutAuthorization();
        if(direct)
        {
            Assert.Equal(miner.ToString(), job.DirectPayoutAddress);
            Assert.Equal(miner.ToString(), authorization.Address);
        }

        // A separate identical job finds a real non-block proof without consuming
        // the serving job's duplicate-proof registry. Keep the ordinary statistical
        // accounting boundary separate from the existing durable candidate tests.
        var probe = direct
            ? manager.GetDirectJobForStratum(authorization.Address, authorization.Destination, authorization.Generation)
            : manager.ProbeJob();
        var actualParams = (object[]) job.GetJobParams(false);
        var probeParams = (object[]) probe.GetJobParams(false);
        Assert.Equal(JsonConvert.SerializeObject(actualParams.Skip(1)), JsonConvert.SerializeObject(probeParams.Skip(1)));
        var time = template.CurTime.ToStringHex8();
        const string extraNonce2 = "00000000000000";
        string nonce = null;
        for(uint i = 0; i < 10000; i++)
        {
            var candidateNonce = i.ToStringHex8();
            if(!probe.ProcessShare(wire.Connection, extraNonce2, time, candidateNonce, "00000000").Share.IsBlockCandidate)
            {
                nonce = candidateNonce;
                break;
            }
        }
        Assert.NotNull(nonce);
        var options = new VarDiffConfig { MinDiff = 1e-10, MaxDiff = 4e-10, TargetTime = 10, RetargetTime = 1, VariancePercent = 0 };
        config.Ports[wire.Connection.LocalEndpoint.Port].VarDiff = options;
        context.VarDiff = new VarDiffContext { Config = options,
            LastTs = clock.Now.ToUnixSeconds() - 1, LastRetarget = clock.Now.ToUnixSeconds() - 10 };
        wire.Canonical.BeforeCreateJob = () => throw new StratumException(StratumError.JobNotFound, "injected post-acceptance publication failure");
        var responses = wire.Connection.ResponseSequence;
        var submission = JsonConvert.SerializeObject(new { id = 90, method = "mining.submit",
            @params = new[] { miner + ".worker", job.JobId, extraNonce2, time, nonce, "00000000" } });
        await wire.SendDisconnectingBatchAsync(submission + "\n" + submission);
        await wire.ReadUntilDisconnectedAsync();
        Assert.Equal(responses + 1, wire.Connection.ResponseSequence);
        Assert.Equal(1, context.Stats.ValidShares);
        Assert.Equal(0, context.Stats.InvalidShares);
        Assert.Empty(context.validJobs);
        bus.Received(1).SendMessage(Arg.Is<Share>(x => x.Miner == miner.ToString() && !x.IsBlockCandidate), Arg.Any<string>());
        if(direct)
        {
            Assert.Same(authorization, context.GetDirectPayoutAuthorization());
            Assert.Equal(miner.ToString(), job.DirectPayoutAddress);
        }

        wire.Canonical.BeforeCreateJob = null;
        await using var replacement = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            sharedPool: wire, canonical: true, difficulty: 1e-10);
        await HandshakeAsync(replacement, miner.ToString(), direct);
        Assert.False(replacement.Connection.IsDisconnectRequested);
        Assert.NotEqual(context.ExtraNonce1, replacement.Connection.ContextAs<BitcoinWorkerContext>().ExtraNonce1);
        Assert.Single(replacement.Connection.ContextAs<BitcoinWorkerContext>().validJobs);
    }

    private static async Task HandshakeAsync(BitcoinBlake2bWireSession wire, string miner, bool direct)
    {
        await wire.SendRequestAsync("mining.configure", new[] { "version-rolling" },
            new Dictionary<string, object> { ["version-rolling.mask"] = "1fffe000" });
        Assert.True((await wire.ReadAsync())["result"]["version-rolling"].Value<bool>());
        await wire.SendRequestAsync("mining.subscribe", "regtest-publication");
        Assert.NotNull((await wire.ReadAsync())["result"]);
        Assert.Equal("mining.set_difficulty", (await wire.ReadAsync())["method"].Value<string>());
        if(!direct)
            Assert.Equal("mining.notify", (await wire.ReadAsync())["method"].Value<string>());
        await wire.SendRequestAsync("mining.authorize", miner + ".worker", "x");
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        if(direct)
            Assert.Equal("mining.notify", (await wire.ReadAsync())["method"].Value<string>());
    }

    private sealed class TemplateManager : BitcoinJobManager
    {
        internal TemplateManager(IComponentContext ctx, IMasterClock clock, IMessageBus bus) :
            base(ctx, clock, bus, new BitcoinExtraNonceProvider("publication-regtest", null)) { }

        internal void SetTemplate(BlockTemplate template, IDestination destination)
        {
            network = Network.RegTest;
            poolAddressDestination = destination;
            currentJob = CreateTemplateJob(template);
        }

        internal BitcoinJob ProbeJob() => CreateTemplateJob(currentJob.BlockTemplate);

        private BitcoinJob CreateTemplateJob(BlockTemplate template)
        {
            var job = new BitcoinJob();
            job.Init(template, "template", poolConfig, extraPoolConfig, clusterConfig, clock,
                poolAddressDestination, network, false, coin.ShareMultiplier,
                coin.CoinbaseHasherValue, coin.HeaderHasherValue, coin.BlockHasherValue);
            return job;
        }
    }
}
