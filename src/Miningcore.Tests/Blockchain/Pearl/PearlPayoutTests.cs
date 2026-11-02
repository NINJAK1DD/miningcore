using System;
using System.IO;
using Miningcore.Blockchain.Pearl;
using Miningcore.Persistence.Model;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlPayoutTests
{
    [Fact]
    public void ReorgClassificationRequiresImmutableEvidenceAndNeverRecreditsConfirmedBlocks()
    {
        Miningcore.Persistence.Model.Block Block(BlockStatus status) => new()
        {
            Id = 1, PoolId = "pearl", Status = status, Hash = new string('a',64),
            TransactionConfirmationData = new string('b',64), BlockHeight = 1,
            Reward = 10, NetworkDifficulty = 5, Miner = PearlWorkTests.Address,
            Created = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc)
        };
        var orphan = Block(BlockStatus.Orphaned);
        var confirmed = Block(BlockStatus.Confirmed);
        Assert.False(PearlPayoutHandler.CanApplyClassification(orphan, confirmed));
        confirmed.PearlCustodialEvidenceVerified = true;
        Assert.True(PearlPayoutHandler.CanApplyClassification(orphan, confirmed));
        Assert.False(PearlPayoutHandler.CanApplyClassification(confirmed, confirmed));
        Assert.True(PearlPayoutHandler.CanApplyClassification(confirmed, Block(BlockStatus.Quarantined)));
        confirmed.Reward++;
        Assert.False(PearlPayoutHandler.CanApplyClassification(orphan, confirmed));
        confirmed.Reward--;
        confirmed.Miner = "different";
        Assert.False(PearlPayoutHandler.CanApplyClassification(orphan, confirmed));
    }

    private const string Marker = "rprl1pg3h2up2s7a8reykv4nv087xs0r4ynswtw9nz69yxngaul6wjmk9sn7mfw9";
    private static (JObject Evidence, Balance[] Recipients) Payment()
    {
        var tx = Transaction.Create(Network.Main);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(new TxOut(Money.Satoshis(125000001), PearlAddress.RewardScript(PearlWorkTests.Address, "rprl")));
        tx.Outputs.Add(new TxOut(Money.Satoshis(1000000), PearlAddress.RewardScript(Marker, "rprl")));
        return (new JObject { ["txid"] = tx.GetHash().ToString(), ["hex"] = tx.ToHex(), ["confirmations"] = 0,
            ["walletconflicts"] = new JArray(), ["fee"] = -0.00001m },
            new[] {new Balance {PoolId = "pearl", Address = PearlWorkTests.Address, Amount = 1.25000001m}});
    }

    [Fact]
    public void PayoutRequiresExactRecipientAndUniqueMarkerOutputs()
    {
        var (evidence, recipients) = Payment();
        var id = evidence.Value<string>("txid");
        PearlPayoutHandler.VerifyPayout(evidence, id, Marker, recipients, PearlNetwork.Regtest);
        Assert.Throws<InvalidDataException>(() => PearlPayoutHandler.VerifyPayout(evidence, id, Marker,
            new[] {recipients[0] with {Amount = 1.25m}}, PearlNetwork.Regtest));
        evidence["walletconflicts"] = new JArray(new string('a',64));
        Assert.Throws<InvalidDataException>(() => PearlPayoutHandler.VerifyPayout(evidence, id, Marker, recipients, PearlNetwork.Regtest));
        evidence["walletconflicts"] = new JArray();
        evidence["confirmations"] = -1;
        Assert.Throws<InvalidDataException>(() => PearlPayoutHandler.VerifyPayout(evidence, id, Marker, recipients, PearlNetwork.Regtest));
    }

    [Fact]
    public void PayoutCannotUseAnotherTransactionNetworkOrDuplicatedMarker()
    {
        var (evidence, recipients) = Payment();
        var id = evidence.Value<string>("txid");
        Assert.ThrowsAny<Exception>(() => PearlPayoutHandler.VerifyPayout(evidence, id, Marker, recipients, PearlNetwork.Mainnet));
        Assert.Throws<InvalidDataException>(() => PearlPayoutHandler.VerifyPayout(evidence, new string('a',64), Marker, recipients, PearlNetwork.Regtest));
        var tx = Transaction.Parse(evidence.Value<string>("hex"), Network.Main);
        tx.Outputs.Add(new TxOut(Money.Satoshis(1000000), PearlAddress.RewardScript(Marker, "rprl")));
        evidence["hex"] = tx.ToHex();
        evidence["txid"] = tx.GetHash().ToString();
        Assert.Throws<InvalidDataException>(() => PearlPayoutHandler.VerifyPayout(evidence, evidence.Value<string>("txid"), Marker, recipients, PearlNetwork.Regtest));
    }
}
