using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Messaging;
using Miningcore.Time;
using NSubstitute;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public class BitcoinBlake2bConfigurationTests : TestBase
{
    [Theory]
    [InlineData("not-hex!", "1d00ffff")]
    [InlineData("1d80ffff", "1d00ffff")]
    [InlineData("1d00ffff", "1c00ffff")]
    [InlineData(null, "1d00ffff")]
    public void ActivationTarget_MalformedParentFailsWithTerminalPoolDiagnostic(string parent, string next)
    {
        var error = Assert.Throws<PoolStartupException>(() =>
            BitcoinBlake2bJobManager.ValidateActivationTarget(parent, next, 22, 0x1d00ffffU, "blake2b-test"));
        Assert.Equal("blake2b-test", error.PoolId);
        Assert.Contains("activation metadata", error.Message);
    }

    [Theory]
    [InlineData("soloCoinbasePayout")]
    [InlineData("bip54Coinbase")]
    [InlineData("gbtArgs")]
    [InlineData("btStream")]
    [InlineData("mergedMining")]
    [InlineData("hasLegacyDaemon")]
    [InlineData("coinbaseTxComment")]
    public void Manager_RejectsForeignProtocolOptionsEvenWhenFalse(string property)
    {
        var manager = new BitcoinBlake2bJobManager(container, Substitute.For<IMasterClock>(),
            Substitute.For<IMessageBus>(), new BitcoinBlake2bExtraNonceProvider());
        var config = new PoolConfig
        {
            Id = "blake2b-test", Template = ModuleInitializer.CoinTemplates["bitcoin-blake2b"],
            Extra = new Dictionary<string, object> { [property] = false },
        };
        Assert.Throws<PoolStartupException>(() => manager.Configure(config, new ClusterConfig()));
    }

    [Theory]
    [InlineData(PayoutScheme.PPBS)]
    [InlineData(PayoutScheme.PPLNSBF)]
    [InlineData((PayoutScheme) 99)]
    public void Manager_RejectsUnreviewedPayoutSchemesBeforeDaemonAccess(PayoutScheme scheme)
    {
        var manager = new BitcoinBlake2bJobManager(container, Substitute.For<IMasterClock>(),
            Substitute.For<IMessageBus>(), new BitcoinBlake2bExtraNonceProvider());
        var config = new PoolConfig
        {
            Id = "blake2b-test", Template = ModuleInitializer.CoinTemplates["bitcoin-blake2b"],
            PaymentProcessing = new PoolPaymentProcessingConfig { PayoutScheme = scheme },
        };
        var error = Assert.Throws<PoolStartupException>(() => manager.Configure(config, new ClusterConfig()));
        Assert.Contains("only SOLO, PPS, PROP and PPLNS", error.Message);
    }

    [Fact]
    public void Catalogue_IsSeparateAndReportsItsRealAlgorithm()
    {
        var coin = Assert.IsType<BitcoinBlake2bTemplate>(ModuleInitializer.CoinTemplates["bitcoin-blake2b"]);
        Assert.Equal(CoinFamily.BitcoinBlake2b, coin.Family);
        Assert.Equal("BLAKE2b header-v2", coin.GetAlgorithmName());
        Assert.Equal(1d, coin.ShareMultiplier);
        Assert.True(coin.DisableVersionRolling);
        Assert.IsType<BitcoinTemplate>(ModuleInitializer.CoinTemplates["bitcoin"]);
        Assert.Equal("BTC", ModuleInitializer.CoinTemplates["bitcoin"].Symbol);
    }

    [Theory]
    [InlineData("blake2bProtocol", "null")]
    [InlineData("blake2bProtocol", "\"unreviewed-revision\"")]
    [InlineData("Blake2bProtocol", "\"knots-29.4.1-header-v2\"")]
    [InlineData("hasMWEB", "true")]
    [InlineData("shareMultiplier", "65536")]
    [InlineData("headerHasher", "{\"hash\":\"blake2b\"}")]
    [InlineData("disableVersionRolling", "false")]
    [InlineData("networks", "[]")]
    public void Loader_RejectsUnsupportedOrAmbiguousCapabilities(string property, string json)
    {
        var template = ReadTemplate();
        template[property] = JToken.Parse(json);
        Assert.Throws<PoolStartupException>(() => Load(template));
    }

    [Theory]
    [InlineData("blake2bActivationHeight", "0")]
    [InlineData("blake2bActivationHeight", "4294967296")]
    [InlineData("Blake2bActivationHeight", "20")]
    [InlineData("blake2bTargetShift", "\"20\"")]
    [InlineData("blake2bTargetShift", "256")]
    [InlineData("blake2bActivationHeadline", "{}")]
    [InlineData("blake2bActivationHeadline", "\"non-ASCII-é\"")]
    public void Loader_RejectsMalformedNetworkMetadata(string property, string json)
    {
        var template = ReadTemplate();
        template["networks"]["regtest"][property] = JToken.Parse(json);
        Assert.Throws<PoolStartupException>(() => Load(template));
    }

    [Fact]
    public void Loader_RejectsForeignFamilyMetadataAndUnreviewedNetwork()
    {
        var template = ReadTemplate();
        template["family"] = "bitcoin";
        Assert.Throws<PoolStartupException>(() => Load(template));
        template = ReadTemplate();
        template["networks"]["test"] = template["networks"]["regtest"].DeepClone();
        Assert.Throws<PoolStartupException>(() => Load(template));
        template = ReadTemplate();
        template["networks"]["main"]["blake2bTargetShift"] = 20;
        Assert.Throws<PoolStartupException>(() => Load(template));
    }

    [Theory]
    [InlineData(290401, "/Satoshi:29.4.1/Knots:20260508/", true)]
    [InlineData(290400, "/Satoshi:29.4.0/Knots:20260508/", false)]
    [InlineData(290401, "/Satoshi:29.4.1/Knots:20260508rc4/", false)]
    [InlineData(290401, "/Satoshi:29.4.1/", false)]
    [InlineData(300000, "/Satoshi:30.0.0/Knots:20260508/", false)]
    public void DaemonIdentity_RequiresReviewedStableRevision(int version, string agent, bool accepted)
    {
        var info = new JObject { ["version"] = version, ["subversion"] = agent };
        if(accepted)
            BitcoinBlake2bJobManager.ValidateDaemonIdentity(info, "test");
        else
            Assert.Throws<PoolStartupException>(() => BitcoinBlake2bJobManager.ValidateDaemonIdentity(info, "test"));
    }

    [Fact]
    public void ExtraNonceProvider_DoesNotRecycleAcrossConcurrentConnections()
    {
        var provider = new BitcoinBlake2bExtraNonceProvider();
        var values = Enumerable.Range(0, 10000).AsParallel().Select(_ => provider.Next()).ToArray();
        Assert.Equal(10000, values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(values, value => Assert.Equal(8, value.Length));
        typeof(BitcoinBlake2bExtraNonceProvider).GetField("counter",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(provider, (long) uint.MaxValue);
        Assert.Throws<InvalidOperationException>(() => provider.Next());
        Assert.Throws<InvalidOperationException>(() => provider.Next());
    }

    [Theory]
    [InlineData("[\"worker\",\"job\",\"0000000000000000\",\"0000000000000000\",1000000000000000]")]
    [InlineData("[\"worker\",\"job\",null,\"0000000000000000\",\"0000000000000000\"]")]
    [InlineData("[\"worker\",\"job\",{},\"0000000000000000\",\"0000000000000000\"]")]
    [InlineData("[\"worker\",\"job\",\"0000000000000000\",\"0000000000000000\",\"0000000000000000\",\"00000000\"]")]
    public void Submit_RejectsNonStringTokensAndExtraVersionBits(string json) =>
        Assert.Throws<Miningcore.Stratum.StratumException>(() =>
            BitcoinBlake2bPool.ValidateSubmissionParameters(JToken.Parse(json)));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"blake2b\":{\"height\":20,\"active\":false}}")]
    [InlineData("{\"blake2b\":{\"height\":19,\"active\":true}}")]
    [InlineData("{\"blake2b\":{\"height\":\"20\",\"active\":true}}")]
    public void Deployment_RejectsInactiveOrMismatchedSchedule(string json) =>
        Assert.Throws<PoolStartupException>(() =>
            BitcoinBlake2bJobManager.ValidateDeployment(JObject.Parse(json), 20, "test"));

    private static JObject ReadTemplate() => (JObject) JObject.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "coins.json")))["bitcoin-blake2b"].DeepClone();

    private CoinTemplate Load(JObject template)
    {
        var path = Path.Combine(Path.GetTempPath(), $"miningcore-blake2b-template-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, new JObject { ["blake2b-test"] = template }.ToString());
            return CoinTemplateLoader.Load(container, new[] { path })["blake2b-test"];
        }
        finally { File.Delete(path); }
    }
}
