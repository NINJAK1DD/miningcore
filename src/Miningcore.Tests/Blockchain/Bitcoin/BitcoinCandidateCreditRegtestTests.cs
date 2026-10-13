using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
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
    public async Task CandidateOnlyHighMinimum_RealBlockCannotInflatePpsCreditAndReplayIsIdempotent()
    {
        await using var node = await BitcoinPayoutHandlerRegtestTests.BitcoinCoreRegtestNode.StartAsync(walletBroadcast: true);
        var template = (await node.RootRpcAsync("getblocktemplate", new JObject { ["rules"] = new JArray("segwit") })).ToObject<BlockTemplate>();
        var destination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var bus = Substitute.For<IMessageBus>();
        var config = new PoolConfig
        {
            Id = "candidate-credit-" + Guid.NewGuid().ToString("N"), Coin = "bitcoin",
            Template = ModuleInitializer.CoinTemplates["bitcoin"], Address = destination.ToString(),
            Daemons = new[] { node.WalletEndpoint },
            PaymentProcessing = new PoolPaymentProcessingConfig
                { Enabled = true, PayoutScheme = PayoutScheme.PPS, PpsBinary64Activation = DateTime.UtcNow.Date },
        };
        var manager = new TemplateManager(container, clock, bus, Substitute.For<IBlockCandidateRecorder>());
        manager.Configure(config, new ClusterConfig());
        manager.SetTemplate(template, destination);
        await using var ledger = await PpsLedger.CreateAsync(config, bus);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: true, difficulty: 1e-10);
        await HandshakeAsync(wire, destination.ToString(), false);
        var network = manager.GetJobForStratum().Difficulty;
        await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = network * 1000 });
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        Assert.Equal(network * 1000, (await wire.ReadAsync())["params"][0].Value<double>());
        var notice = await wire.ReadAsync();
        Assert.Equal("mining.notify", notice["method"].Value<string>());
        var probe = manager.ProbeJob();
        string nonce = null;
        var time = template.CurTime.ToStringHex8();
        for(uint i = 0; i < 10000; i++)
        {
            try
            {
                var candidateNonce = i.ToStringHex8();
                if(probe.ProcessShare(wire.Connection, "10000000000000", time, candidateNonce, "00000000").Share.IsBlockCandidate)
                {
                    nonce = candidateNonce;
                    break;
                }
            }
            catch(StratumException ex) when(ex.Code == StratumError.LowDifficultyShare) { }
        }
        Assert.NotNull(nonce);
        var reply = await wire.RequestAsync("mining.submit", destination + ".worker",
            notice["params"][0].Value<string>(), "10000000000000", time, nonce, "00000000");
        Assert.True(reply["result"].Value<bool>());
        Assert.True(ledger.Accepted.IsBlockCandidate);
        Assert.Equal(network, ledger.Accepted.Difficulty);
        Assert.InRange(ledger.Accepted.PpsCalculatedAmount.Value, 0.00000001m, template.CoinbaseValue / 100_000_000m);
        await ledger.AssertSingleCreditAsync();
        await ledger.Recorder.PersistSharesAsync(new[] { ledger.Accepted });
        await ledger.AssertSingleCreditAsync();
        var duplicate = await wire.RequestAsync("mining.submit", destination + ".worker",
            notice["params"][0].Value<string>(), "10000000000000", time, nonce, "00000000");
        Assert.Equal((int) StratumError.DuplicateShare, duplicate["error"]["code"].Value<int>());
        await ledger.AssertSingleCreditAsync();
    }
}
