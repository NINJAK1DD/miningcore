using System;
using System.IO;
using System.Linq;
using Miningcore.Blockchain.Pearl;
using Newtonsoft.Json;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlBlockJobTests
{
    private const string Address = "rprl1pjkt5h3mld9xj6ar2ys5xd2ny7062uqmthav3ayytr8yxhqxdr5rqlq2fzn";
    private static PearlBlockTemplate Template()
    {
        using var stream = typeof(PearlBlockJobTests).Assembly.GetManifestResourceStream(
            "Miningcore.Tests.Fixtures.Pearl.regtest-template.json");
        using var reader = new StreamReader(stream);
        return JsonConvert.DeserializeObject<PearlBlockTemplate>(reader.ReadToEnd());
    }

    [Fact]
    public void TemplateLengthsAndNetworkRulesAreCheckedBeforeConstruction()
    {
        var template = Template();
        template.PreviousBlockhash = new string('0', 66);
        Assert.Throws<InvalidDataException>(() => new PearlBlockJob(template, Address, PearlNetwork.Regtest, new byte[24]));
        template = Template();
        // A regtest V3 template at height 1 is not valid mainnet work.
        Assert.Throws<InvalidDataException>(() => new PearlBlockJob(template, Address, PearlNetwork.Mainnet, new byte[24]));
    }
    [Fact]
    public void TaprootAddressIsNetworkBoundAndChecksumVerified()
    {
        Assert.Equal(34, PearlAddress.RewardScript(Address, "rprl").Length);
        Assert.Equal(PearlAddress.RewardScript(Address, "rprl"),
            PearlAddress.RewardScript(Address.ToUpperInvariant(), "rprl"));
        Assert.ThrowsAny<Exception>(() => PearlAddress.RewardScript(Address, "prl"));
        Assert.ThrowsAny<Exception>(() => PearlAddress.RewardScript("R" + Address[1..], "rprl"));
        Assert.ThrowsAny<Exception>(() => PearlAddress.RewardScript(Address[..^1] + "q", "rprl"));
    }

    [Fact]
    public void WorkerCoinbasesBindDistinctImmutableHeaders()
    {
        var template = Template();
        var first = new PearlBlockJob(template, Address, PearlNetwork.Regtest, new byte[24]);
        var second = new PearlBlockJob(template, Address, PearlNetwork.Regtest, Enumerable.Repeat((byte) 1, 24).ToArray());
        Assert.NotEqual(first.Header.ToArray(), second.Header.ToArray());
        Assert.NotEqual(first.CoinbaseId, second.CoinbaseId);
        var snapshot = first.Header.ToArray();
        template.CurTime++;
        template.CoinbaseValue = 1;
        Assert.Equal(snapshot, first.Header.ToArray());
        Assert.Equal(322964134063, first.RewardGrains);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(4u)]
    public void UnsupportedMiningCertificateFailsClosed(uint version)
    {
        var template = Template();
        template.RequiredCertificateVersion = version;
        Assert.Throws<InvalidDataException>(() => new PearlBlockJob(template, Address, PearlNetwork.Regtest, new byte[24]));
    }

    [Fact]
    public void WrongTargetAndWitnessCommitmentFailClosed()
    {
        var template = Template();
        template.Target = new string('f', 64);
        Assert.Throws<InvalidDataException>(() => new PearlBlockJob(template, Address, PearlNetwork.Regtest, new byte[24]));
        template = Template();
        template.DefaultWitnessCommitment = new string('0', 64);
        Assert.Throws<InvalidDataException>(() => new PearlBlockJob(template, Address, PearlNetwork.Regtest, new byte[24]));
    }
}
