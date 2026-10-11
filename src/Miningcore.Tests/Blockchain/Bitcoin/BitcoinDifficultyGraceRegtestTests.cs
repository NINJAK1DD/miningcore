using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain.BitcoinBlake2b;
using Miningcore.Time;
using NBitcoin;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public partial class BitcoinPublicationRegtestTests
{
    [BitcoinCoreIntegrationFact(requiresPostgres: true)]
    public Task StaticDifficulty_RealInFlightProofKeepsOriginalExactlyOnceCredit() => DifficultyGraceAsync(false);

    [BitcoinCoreIntegrationFact(requiresPostgres: true)]
    public Task MinimumDifficulty_RealInFlightProofKeepsOriginalExactlyOnceCredit() => DifficultyGraceAsync(true);

    [BitcoinCoreIntegrationFact(requiresPostgres: true)]
    public Task NiceHashDifficulty_RealInFlightProofKeepsOriginalExactlyOnceCredit() => DifficultyGraceAsync(false, true);

    private async Task DifficultyGraceAsync(bool minimum, bool nicehash = false)
    {
        await using var node = await BitcoinPayoutHandlerRegtestTests.BitcoinCoreRegtestNode.StartAsync(walletBroadcast: true);
        var template = (await node.RootRpcAsync("getblocktemplate", new JObject { ["rules"] = new JArray("segwit") }))
            .ToObject<BlockTemplate>();
        var destination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var bus = Substitute.For<IMessageBus>();
        var coin = (BitcoinTemplate) ModuleInitializer.CoinTemplates["bitcoin"];
        var config = new PoolConfig
        {
            Id = "difficulty-grace-" + Guid.NewGuid().ToString("N"), Coin = "bitcoin", Template = coin,
            Address = destination.ToString(), Daemons = new[] { node.WalletEndpoint },
            Banning = new PoolShareBasedBanningConfig { Enabled = false },
            PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true, PayoutScheme = PayoutScheme.PPS,
                PpsBinary64Activation = DateTime.UtcNow.Date },
        };
        var manager = new TemplateManager(container, clock, bus);
        manager.Configure(config, new ClusterConfig());
        manager.SetTemplate(template, destination);
        await using var ledger = await PpsLedger.CreateAsync(config, bus);
        var time = new ManualTimeProvider();
        var options = new VarDiffConfig { MinDiff = 1e-10, MaxDiff = 1e-8, TargetTime = 10, RetargetTime = 5, VariancePercent = 1 };
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: true, difficulty: 1e-10, varDiff: options, varDiffTimeProvider: time);
        // Control the external API result; the TCP subscription still commits
        // real NiceHash identity. A later explicit fixed assignment retires
        // dynamic work through the production authorization path.
        if(nicehash) wire.Canonical.NicehashLookup = _ => Task.FromResult<double?>(null);
        await HandshakeAsync(wire, destination.ToString(), false, nicehash ? "NiceHash/1.0" : "regtest-publication");
        await wire.Canonical.UpdateVarDiff(wire.Connection, 2e-10);
        Assert.Equal(2e-10, (await wire.ReadAsync())["params"][0].Value<double>());
        var oldNotice = await wire.ReadAsync();
        var oldId = oldNotice["params"][0].Value<string>();
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        Assert.Equal(nicehash, context.IsNicehash);
        var oldJob = context.GetJob(oldId);
        Assert.NotNull(oldJob);
        var probe = manager.ProbeJob();
        var ntime = template.CurTime.ToStringHex8();
        const string extranonce2 = "00000000000000";
        var nonces = new List<string>();
        for(uint i = 0; i < 10000 && nonces.Count < 4; i++)
        {
            try
            {
                var candidate = i.ToStringHex8();
                var share = probe.ProcessShare(wire.Connection, extranonce2, ntime, candidate, "00000000").Share;
                if(!share.IsBlockCandidate && share.ShareDifficulty < 1e-8 * 0.99)
                    nonces.Add(candidate);
            }
            catch(StratumException ex) when(ex.Code == StratumError.LowDifficultyShare) { }
        }
        Assert.Equal(4, nonces.Count);
        if(minimum)
        {
            await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty" },
                new Dictionary<string, object> { ["minimum-difficulty.value"] = 1e-8 });
            Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        }
        else
        {
            await wire.SendRequestAsync("mining.authorize", destination + ".worker", "d=0.00000001");
            Assert.True((await wire.ReadAsync())["result"].Value<bool>());
            Assert.Equal(1e-8, (await wire.ReadAsync())["params"][0].Value<double>());
        }
        Assert.Null(context.VarDiff);
        // Normal broadcast reissues the same template with the fixed target.
        await wire.Canonical.Announce(manager.ProbeJob().GetJobParams(false));
        if(minimum)
            Assert.Equal(1e-8, (await wire.ReadAsync())["params"][0].Value<double>());
        var currentNotice = await wire.ReadAsync();
        Assert.Equal("mining.notify", currentNotice["method"].Value<string>());
        var currentId = currentNotice["params"][0].Value<string>();
        Assert.NotEqual(oldId, currentId);
        async Task<JObject> Submit(string id, string nonce) => await wire.RequestAsync("mining.submit",
            destination + ".worker", id, extranonce2, ntime, nonce, "00000000");
        Assert.True((await Submit(oldId, nonces[0]))["result"].Value<bool>());
        Assert.Equal(2e-10 / coin.ShareMultiplier, ledger.Accepted.Difficulty);
        await ledger.AssertSingleCreditAsync();
        Assert.NotNull((await Submit(currentId, nonces[1]))["error"]); // Below current announced target.
        Assert.Equal((int) StratumError.DuplicateShare,
            (await Submit(currentId, nonces[0]))["error"]["code"].Value<int>()); // Same proof under a new ID.
        time.MoveWallClock(TimeSpan.FromDays(-1000));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        Assert.NotNull((await Submit(oldId, nonces[2]))["error"]);
        await ledger.Recorder.PersistSharesAsync(new[] { ledger.Accepted });
        await ledger.AssertSingleCreditAsync();
        Assert.Equal(1, context.Stats.ValidShares);
        Assert.False(wire.Connection.IsDisconnectRequested);
        await using var replacement = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            sharedPool: wire, canonical: true, difficulty: 1e-8);
        await HandshakeAsync(replacement, destination.ToString(), false);
        var rejected = await replacement.RequestAsync("mining.submit", destination + ".worker", oldId,
            extranonce2, ntime, nonces[3], "00000000");
        Assert.NotNull(rejected["error"]);
        await ledger.AssertSingleCreditAsync();
    }
}
