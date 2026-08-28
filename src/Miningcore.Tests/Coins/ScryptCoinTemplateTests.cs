using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Tests.Util;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Coins;

public class ScryptCoinTemplateTests : TestBase
{
    public static IEnumerable<object[]> SupportedTemplates()
    {
        yield return new object[] { "blockchaincoinx", "XCCX" };
        yield return new object[] { "catcoin", "CAT" };
        yield return new object[] { "cyberyen", "CY" };
        yield return new object[] { "ferrite", "FEC" };
        yield return new object[] { "ibithub", "IBH" };
        yield return new object[] { "litecoin-ii", "LC2" };
        yield return new object[] { "mateablecoin-scrypt", "MTBC" };
        yield return new object[] { "stohncoin", "SOH" };
        yield return new object[] { "theminerzcoin", "TMC" };
        yield return new object[] { "bells", "BEL" };
        yield return new object[] { "newyorkcoin", "NYC" };
    }

    [Theory]
    [MemberData(nameof(SupportedTemplates))]
    public void Template_UsesBitcoinCompatibleScryptContract(string key,
        string symbol)
    {
        var template = GetTemplate(key);

        Assert.Equal(symbol, template.Symbol);
        Assert.Equal(CoinFamily.Bitcoin, template.Family);
        Assert.StartsWith("https://github.com/", template.Github);
        Assert.IsType<Sha256D>(template.CoinbaseHasherValue);
        Assert.IsType<Scrypt>(template.HeaderHasherValue);
        Assert.Equal(65536d, template.ShareMultiplier);
    }

    [Fact]
    public void RetainedTemplate_ProducesKnownScryptVector()
    {
        var template = GetTemplate("catcoin");
        var input = Enumerable.Repeat((byte) 0x80, 32).ToArray();
        var output = new byte[32];

        template.HeaderHasherValue.Digest(input, output);

        Assert.Equal(
            "b546d334422ff5fff98e8ba847a55bbc06271c64bb5e21107b1b225f6579d40a",
            output.ToHexString());
    }

    [Theory]
    [InlineData("catcoin")]
    [InlineData("cyberyen")]
    [InlineData("ferrite")]
    [InlineData("ibithub")]
    [InlineData("litecoin-ii")]
    [InlineData("mateablecoin-scrypt")]
    [InlineData("stohncoin")]
    [InlineData("theminerzcoin")]
    [InlineData("bells")]
    [InlineData("newyorkcoin")]
    public void Sha256dIdentityTemplate_UsesReviewedBlockHasher(string key)
    {
        var hasher = Assert.IsType<DigestReverser>(GetTemplate(key).BlockHasherValue);

        Assert.IsType<Sha256D>(hasher.Upstream);
    }

    [Fact]
    public void NonMwebTemplate_AdvertisesOnlySegwitRule()
    {
        var parameters = BitcoinJobManager.BuildBlockTemplateParams(
            GetTemplate("stohncoin"));
        var request = JObject.FromObject(Assert.Single(parameters));

        Assert.Equal(new[] { "segwit" }, request["rules"].Values<string>());
    }

    [Theory]
    [InlineData("catcoin")]
    [InlineData("cyberyen")]
    [InlineData("ferrite")]
    [InlineData("litecoin-ii")]
    [InlineData("newyorkcoin")]
    public void MwebCapableTemplate_AdvertisesRequiredClientRules(string key)
    {
        var template = GetTemplate(key);
        var parameters = BitcoinJobManager.BuildBlockTemplateParams(template);
        var request = JObject.FromObject(Assert.Single(parameters));

        Assert.True(template.HasMWEB);
        Assert.Equal(new[] { "segwit", "mweb" },
            request["rules"].Values<string>());
    }

    [Fact]
    public void MwebSerialization_FollowsReturnedTemplatePayload()
    {
        var template = GetTemplate("catcoin");
        var job = new SerializationProbe();

        var beforeActivation = job.Serialize(template, NewBlockTemplate(), false);
        var activeTemplate = NewBlockTemplate();
        activeTemplate.Extra = new Dictionary<string, object>
        {
            ["mweb"] = new JValue("0102"),
        };
        var afterActivation = job.Serialize(template, activeTemplate, false);

        Assert.Equal("aa01bb", beforeActivation.ToHexString());
        Assert.Equal("aa01bb010102", afterActivation.ToHexString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("not-hex")]
    public void MwebSerialization_RejectsMalformedReturnedPayload(string payload)
    {
        var blockTemplate = NewBlockTemplate();
        blockTemplate.Extra = new Dictionary<string, object>
        {
            ["mweb"] = payload,
        };

        Assert.Throws<InvalidDataException>(() =>
            BitcoinJob.ParseMwebPayload(GetTemplate("catcoin"), blockTemplate));
    }

    [Theory]
    [InlineData("blockchaincoinx")]
    [InlineData("theminerzcoin")]
    public void PseudoPosTemplate_SerializesTimestampAndEmptyBlockSignature(string key)
    {
        var template = GetTemplate(key);
        var job = new SerializationProbe();
        var blockTemplate = NewBlockTemplate();
        var coinbase = job.InitializeAndGetCoinbase(template, blockTemplate);
        var block = job.Serialize(template, blockTemplate, true);

        Assert.True(template.IsPseudoPoS);
        Assert.Equal(blockTemplate.CurTime,
            BinaryPrimitives.ReadUInt32LittleEndian(coinbase.AsSpan(4, 4)));
        Assert.Equal("aa01bb00", block.ToHexString());
    }

    [Fact]
    public void BlockChainCoinX_GenesisUsesScryptBlockIdentity()
    {
        const string header =
            "01000000" +
            "0000000000000000000000000000000000000000000000000000000000000000" +
            "5ef0e62f23424ca2951bcdbfba8f0afae1c828734c965e1dbdfaa36148b24e89" +
            "6318805a" +
            "ffff0f1e" +
            "53dc2000";
        var output = new byte[32];
        var template = GetTemplate("blockchaincoinx");

        template.PoSBlockHasherValue.Digest(header.HexToByteArray(), output);

        Assert.Equal(
            "000003e4086c6030369ce90af75ec0b93e534f1072023d92090dcc4365522bb0",
            output.ToHexString());
    }

    [Fact]
    public void TheMinerzCoin_UsesSha256dBlockIdentity()
    {
        const string bitcoinGenesisHeader =
            "01000000" +
            "0000000000000000000000000000000000000000000000000000000000000000" +
            "3ba3edfd7a7b12b27ac72c3e67768f617fc81bc3888a51323a9fb8aa4b1e5e4a" +
            "29ab5f49" +
            "ffff001d" +
            "1dac2b7c";
        var output = new byte[32];
        var hasher = Assert.IsType<DigestReverser>(
            GetTemplate("theminerzcoin").PoSBlockHasherValue);

        Assert.IsType<Sha256D>(hasher.Upstream);
        hasher.Digest(bitcoinGenesisHeader.HexToByteArray(), output);
        Assert.Equal(
            "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f",
            output.ToHexString());
    }

    [Fact]
    public void MateableCoin_DoesNotDeclareUnsupportedPosBlockHasher()
    {
        Assert.Null(GetTemplate("mateablecoin-scrypt").PoSBlockHasher);
    }

    [Theory]
    [InlineData("blockchaincoinx")]
    [InlineData("ibithub")]
    public void LegacyDaemonTemplate_SelectsLegacyRpcSurfaceByDefault(string key)
    {
        var template = GetTemplate(key);

        Assert.True(template.RequiresLegacyDaemon);
        Assert.True(BitcoinJobManagerBase<BitcoinJob>
            .ResolveLegacyDaemonMode(template, null));
        Assert.False(BitcoinJobManagerBase<BitcoinJob>
            .ResolveLegacyDaemonMode(template, false));
    }

    [Fact]
    public void MateableCoinScrypt_PassesRequiredAlgorithmToGetBlockTemplate()
    {
        var parameters = Assert.IsType<JArray>(
            GetTemplate("mateablecoin-scrypt").BlockTemplateRpcExtraParams);

        Assert.Equal("scrypt", Assert.Single(parameters).Value<string>());
    }

    [Theory]
    [InlineData("b1t")]
    [InlineData("bonkcoin")]
    [InlineData("craftcoin")]
    [InlineData("dingocoin")]
    [InlineData("earthcoin")]
    [InlineData("flopcoin")]
    [InlineData("junkcoin")]
    [InlineData("luckycoin")]
    [InlineData("pepecoin")]
    [InlineData("shibainucoin")]
    [InlineData("trumpow")]
    public void UnsupportedTemplate_IsNotAdvertised(string key)
    {
        Assert.False(ModuleInitializer.CoinTemplates.ContainsKey(key));
    }

    [Fact]
    public void QuaiScrypt_IsNotAdvertisedAsBitcoinRpcCompatible()
    {
        Assert.False(ModuleInitializer.CoinTemplates.ContainsKey("quai-scrypt"));
    }

    private static BitcoinTemplate GetTemplate(string key)
    {
        return Assert.IsType<BitcoinTemplate>(ModuleInitializer.CoinTemplates[key]);
    }

    private static BlockTemplate NewBlockTemplate()
    {
        return new BlockTemplate
        {
            Version = 1,
            PreviousBlockhash = new string('0', 64),
            CoinbaseValue = 5_000_000_000,
            Target = "00000fffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            CurTime = 1_518_344_291,
            Bits = "1e0fffff",
            Height = 1,
            Transactions = Array.Empty<BitcoinBlockTransaction>(),
            CoinbaseAux = new CoinbaseAux(),
        };
    }

    private sealed class SerializationProbe : BitcoinJob
    {
        public byte[] InitializeAndGetCoinbase(BitcoinTemplate template,
            BlockTemplate blockTemplate)
        {
            var poolConfig = new PoolConfig { Template = template };
            var clock = MockMasterClock.FromTicks(638010200200475015);

            Init(blockTemplate, "test", poolConfig, null, new ClusterConfig(),
                clock, new Key().PubKey, Network.Main, true,
                template.ShareMultiplier, template.CoinbaseHasherValue,
                template.HeaderHasherValue,
                template.PoSBlockHasherValue ?? template.BlockHasherValue);

            return coinbaseInitial;
        }

        public byte[] Serialize(BitcoinTemplate template,
            BlockTemplate blockTemplate, bool proofOfStake)
        {
            coin = template;
            BlockTemplate = blockTemplate;
            isPoS = proofOfStake;
            mwebPayload = ParseMwebPayload(template, blockTemplate);

            return SerializeBlock(new byte[] { 0xaa }, new byte[] { 0xbb });
        }
    }
}
