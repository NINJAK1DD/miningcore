using System;
using System.Collections.Generic;
using System.IO;
using Miningcore.Blockchain.Pearl;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlNetworkRulesTests
{
    public static IEnumerable<object[]> Vectors()
    {
        using var stream = typeof(PearlNetworkRulesTests).Assembly.GetManifestResourceStream(
            "Miningcore.Tests.Fixtures.Pearl.network-vectors.json");
        using var reader = new StreamReader(stream);
        foreach(var row in JArray.Parse(reader.ReadToEnd()))
            yield return new object[] { row.Value<string>("network"), row.Value<long>("height"),
                row.Value<uint>("certificateVersion"), row.Value<bool>("denseOnly"),
                row.Value<bool>("rankPenalty"), row.Value<string>("addressPrefix") };
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void ForkBoundariesMatchOfficialChaincfg(string name, long height, uint version,
        bool dense, bool rank, string prefix)
    {
        var network = Enum.Parse<PearlNetwork>(name, true);
        var actual = PearlNetworkRules.At(network, height);
        Assert.Equal(version, actual.CertificateVersion);
        Assert.Equal(dense, actual.DenseOnly);
        Assert.Equal(rank, actual.RankPenalty);
        Assert.Equal(prefix, PearlNetworkRules.AddressPrefix(network));
        if(version == 3 && rank && height > 0)
            PearlNetworkRules.RequireCurrentMining(network, height, version);
        else
            Assert.Throws<InvalidDataException>(() => PearlNetworkRules.RequireCurrentMining(network, height, version));
        foreach(var unsupported in new uint[] {0, 1, 2, 4, uint.MaxValue})
            Assert.Throws<InvalidDataException>(() => PearlNetworkRules.RequireCurrentMining(network, height, unsupported));
    }

    [Fact]
    public void UnknownNetworkNegativeHeightAndReorgBeforeV3FailClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PearlNetworkRules.At((PearlNetwork) 99, 100000));
        Assert.Throws<ArgumentOutOfRangeException>(() => PearlNetworkRules.At(PearlNetwork.Mainnet, -1));
        PearlNetworkRules.RequireCurrentMining(PearlNetwork.Mainnet, 99000, 3);
        Assert.Throws<InvalidDataException>(() => PearlNetworkRules.RequireCurrentMining(PearlNetwork.Mainnet, 98999, 3));
    }
}
