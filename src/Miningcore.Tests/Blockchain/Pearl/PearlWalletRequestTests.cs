using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Miningcore.Blockchain.Pearl;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlWalletRequestTests
{
    private const string Address = "rprl1pjkt5h3mld9xj6ar2ys5xd2ny7062uqmthav3ayytr8yxhqxdr5rqlq2fzn";
    internal static JObject Coin(decimal amount, int index = 0) => new()
    {
        ["txid"] = new string('c', 64), ["vout"] = index, ["account"] = "default",
        ["spendable"] = true, ["confirmations"] = 101, ["amount"] = amount,
        ["scriptPubKey"] = "5120" + new string('a', 64)
    };

    [Fact]
    public void ThousandsOfMarkersDoNotInflateFeesWhenLargerInputsFundThePayout()
    {
        var outputs = new JArray(Enumerable.Range(0, 10000).Select(i => Coin(.01m, i)));
        outputs.Add(Coin(10m, 10000));
        Assert.Equal(PearlWalletRequest.FeeReserve(1, 1, 10000), PearlWalletRequest.FeeReserve(outputs, 5.01m, 1, 10000));
        Assert.Throws<InvalidDataException>(() => PearlWalletRequest.FeeReserve(outputs, 105m, 1, 10000));
        Assert.Throws<InvalidDataException>(() => PearlWalletRequest.FeeReserve(new JArray(Coin(1.011m)), 1.01m, 1, 10000));
    }

    [Fact]
    public void SelectionIncludesItsOwnFeeAndRejectsIneligibleOrDuplicateEvidence()
    {
        Assert.Equal(PearlWalletRequest.FeeReserve(2, 1, 10000000),
            PearlWalletRequest.FeeReserve(new JArray(Coin(1.01m), Coin(.2m, 1)), 1.01m, 1, 10000000));
        foreach(var field in new[] {"account", "spendable", "confirmations", "scriptPubKey", "amount", "txid", "vout"})
        {
            var coin = Coin(10m); coin.Remove(field);
            Assert.Throws<InvalidDataException>(() => PearlWalletRequest.FeeReserve(new JArray(coin), 1m, 1, 10000));
        }
        Assert.Throws<InvalidDataException>(() => PearlWalletRequest.FeeReserve(new JArray(Coin(10), Coin(10)), 1m, 1, 10000));
    }

    [Fact]
    public void FeePrecedesMinConfAndUsesExplicitDefaultAccountWithoutComments()
    {
        var args = PearlWalletRequest.SendMany(new Dictionary<string, decimal> {{Address, 1.25000001m}},
            PearlNetwork.Regtest, 1000, 2);
        var wire = JArray.Parse(JsonConvert.SerializeObject(args));
        Assert.Equal(4, wire.Count);
        Assert.Equal("default", wire[0].Value<string>());
        Assert.Equal(1.25000001m, wire[1][Address].Value<decimal>());
        Assert.Equal(0.00001m, wire[2].Value<decimal>());
        Assert.Equal(2, wire[3].Value<int>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.000000001")]
    [InlineData("2100000001")]
    [InlineData("100000000.00000001")]
    public void InvalidSubgrainOrInexactAmountsFailBeforeSending(string value)
    {
        var amount = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<InvalidDataException>(() => PearlWalletRequest.SendMany(
            new Dictionary<string, decimal> {{Address, amount}}, PearlNetwork.Regtest, 1000));
    }

    [Fact]
    public void FeeReserveIncludesFragmentationAndRoundsUpToGrains()
    {
        Assert.Equal(.01m, PearlWalletRequest.FeeReserve(1, 1, 1000));
        Assert.Equal(.089m, PearlWalletRequest.FeeReserve(1, 1, 10_000_000));
        Assert.True(PearlWalletRequest.FeeReserve(100, 1, 10_000_000) > PearlWalletRequest.FeeReserve(1, 1, 10_000_000));
        Assert.Throws<InvalidDataException>(() => PearlWalletRequest.FeeReserve(0, 1, 1000));
        Assert.Throws<InvalidDataException>(() => PearlWalletRequest.FeeReserve(10000, 1, 1000));
        Assert.Throws<InvalidDataException>(() => PearlWalletRequest.FeeReserve(1, 1, 999));
    }

    [Fact]
    public void WrongNetworkDuplicatesAndUnboundedFeesFailBeforeSending()
    {
        var amounts = new Dictionary<string, decimal> {{Address, 1m}};
        Assert.ThrowsAny<Exception>(() => PearlWalletRequest.SendMany(amounts, PearlNetwork.Mainnet, 1000));
        amounts.Add(Address.ToUpperInvariant(), 1m);
        Assert.Throws<InvalidDataException>(() => PearlWalletRequest.SendMany(amounts, PearlNetwork.Regtest, 1000));
        amounts.Remove(Address.ToUpperInvariant());
        foreach(var fee in new long[] {0, -1, 100000001})
            Assert.Throws<ArgumentOutOfRangeException>(() => PearlWalletRequest.SendMany(amounts, PearlNetwork.Regtest, fee));
        Assert.Throws<ArgumentOutOfRangeException>(() => PearlWalletRequest.SendMany(amounts, PearlNetwork.Regtest, 1000, 0));
    }
}
