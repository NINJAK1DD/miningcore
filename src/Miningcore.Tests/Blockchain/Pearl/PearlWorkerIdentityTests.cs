using Miningcore.Blockchain.Pearl;
using Miningcore.Stratum;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlWorkerIdentityTests
{
    [Theory]
    [InlineData(".rig_1", null, "rig_1")]
    [InlineData("", "\"rig_1\"", "rig_1")]
    [InlineData(".rig_1", "\"rig_1\"", "rig_1")]
    [InlineData("", null, "")]
    [InlineData("", "\"\"", "")]
    public void BothMinerDialectsResolveToTheSameValidatedIdentity(string suffix, string separate, string expected)
    {
        var result = PearlWorkerIdentity.Parse(PearlWorkTests.Address + suffix,
            separate == null ? null : JToken.Parse(separate), PearlNetwork.Regtest);
        Assert.Equal(PearlWorkTests.Address, result.Miner);
        Assert.Equal(expected, result.Worker);
    }

    [Theory]
    [InlineData(".first", "\"second\"")]
    [InlineData(".first", "\"\"")]
    [InlineData("", "42")]
    [InlineData("", "null")]
    [InlineData("", "true")]
    [InlineData("", "[]")]
    [InlineData("", "{}")]
    [InlineData("", "\"nested.worker\"")]
    [InlineData("", "\"unicode-\u00e9\"")]
    public void ConflictingOrMalformedExplicitWorkersFailClosed(string suffix, string separate)
    {
        Assert.Throws<StratumException>(() => PearlWorkerIdentity.Parse(
            PearlWorkTests.Address + suffix, JToken.Parse(separate), PearlNetwork.Regtest));
    }

    [Fact]
    public void SeparateWorkerHasTheSameLengthLimitAsTheSuffix()
    {
        Assert.Equal(64, PearlWorkerIdentity.Parse(PearlWorkTests.Address, new JValue(new string('a', 64)), PearlNetwork.Regtest).Worker.Length);
        Assert.Throws<StratumException>(() => PearlWorkerIdentity.Parse(PearlWorkTests.Address,
            new JValue(new string('a', 65)), PearlNetwork.Regtest));
    }
}
