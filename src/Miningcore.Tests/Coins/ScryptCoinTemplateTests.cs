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
using Miningcore.Mining;
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

    [Theory]
    [InlineData("blockchaincoinx")]
    [InlineData("theminerzcoin")]
    public void PseudoPosTemplate_SelectsRuntimePosFraming(string key)
    {
        var difficulty = JObject.Parse(
            "{\"proof-of-work\":1,\"proof-of-stake\":1}");

        Assert.True(BitcoinJobManagerBase<BitcoinJob>
            .ResolveProofOfStakeMode(GetTemplate(key), difficulty));
    }

    [Fact]
    public void OrdinaryTemplate_WithHybridDifficultyShape_RemainsProofOfWork()
    {
        var difficulty = JObject.Parse(
            "{\"proof-of-work\":1,\"proof-of-stake\":1}");

        Assert.False(BitcoinJobManagerBase<BitcoinJob>
            .ResolveProofOfStakeMode(new BitcoinTemplate(), difficulty));
    }

    [Theory]
    [InlineData("dogecoin", 0x00620004u)]
    [InlineData("cyberyen", 0x10000004u)]
    [InlineData("bells", 0x00100004u)]
    // Namecoin's daemon combines chain ID 1 with base version 4.
    [InlineData("namecoin", 0x00010004u)]
    [InlineData("luckybit", 0x20130004u)]
    public void StrictChainIdTemplate_DisablesVersionRollingAndPreservesVersion(
        string key, uint templateVersion)
    {
        var template = GetTemplate(key);
        var mask = BitcoinPool.ResolveVersionRollingMask(template,
            BitcoinConstants.VersionRollingPoolMask);

        Assert.True(template.DisableVersionRolling);
        Assert.Null(mask);
        Assert.Null(BitcoinPool.ResolveVersionRollingMask(template, 0));
        Assert.Equal(templateVersion, BitcoinJob.ApplyVersionRolling(
            templateVersion, mask, 0));
        Assert.Equal(templateVersion, BitcoinJob.ApplyVersionRolling(
            templateVersion, mask, uint.MaxValue));
    }

    [Fact]
    public void VersionRollingDisabledTemplates_MatchReviewedStrictChainIdSet()
    {
        var expected = new[]
        {
            "bells", "cyberyen", "dogecoin", "luckybit", "namecoin", "paccoin",
        };
        var actual = ModuleInitializer.CoinTemplates
            .Where(x => x.Value is BitcoinTemplate {DisableVersionRolling: true} &&
                x.Value.Family != CoinFamily.BitcoinBlake2b)
            .Select(x => x.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void OrdinaryTemplate_RetainsVersionRollingContract()
    {
        var mask = BitcoinPool.ResolveVersionRollingMask(new BitcoinTemplate(),
            BitcoinConstants.VersionRollingPoolMask);

        Assert.Equal(BitcoinConstants.VersionRollingPoolMask, mask);
    }

    [Fact]
    public void UnauditedDefaultVersionRollingPolicy_IsOperatorVisible()
    {
        Assert.True(BitcoinPool.UsesUnauditedDefaultVersionRolling(
            new BitcoinTemplate()));
        Assert.False(BitcoinPool.UsesUnauditedDefaultVersionRolling(
            new BitcoinTemplate
            {
                AllowedVersionRollingMask =
                    BitcoinConstants.VersionRollingPoolMask,
            }));
        Assert.False(BitcoinPool.UsesUnauditedDefaultVersionRolling(
            new BitcoinTemplate {DisableVersionRolling = true}));
    }

    [Fact]
    public void Paccoin_DisablesVersionRollingWithoutClaimingSourceVerifiedBits()
    {
        var template = GetTemplate("paccoin");

        Assert.True(template.DisableVersionRolling);
        Assert.Null(template.AllowedVersionRollingMask);
        Assert.Null(template.VersionRollingConsensusMask);
    }

    [Theory]
    [InlineData("dogecoin")]
    [InlineData("cyberyen")]
    [InlineData("bells")]
    [InlineData("namecoin")]
    [InlineData("luckybit")]
    public void SourceVerifiedStrictChainIdTemplate_RecordsConsensusOwnedBits(string key)
    {
        Assert.Equal(0xffff0100u, GetTemplate(key).VersionRollingConsensusMask);
    }

    [Theory]
    [InlineData("auroracoin-sha256")]
    [InlineData("auroracoin-scrypt")]
    [InlineData("auroracoin-skein")]
    [InlineData("auroracoin-qubit")]
    [InlineData("auroracoin-groestl")]
    [InlineData("danecoin")]
    [InlineData("digibyte-sha256")]
    [InlineData("digibyte-scrypt")]
    [InlineData("digibyte-skein")]
    [InlineData("digibyte-qubit")]
    [InlineData("digibyte-odocrypt")]
    [InlineData("mooncoin")]
    [InlineData("newyorkcoin")]
    [InlineData("pyrk-sha256")]
    [InlineData("pyrk-scrypt")]
    [InlineData("pyrk-x11")]
    [InlineData("skydoge")]
    [InlineData("smileycoin-sha256")]
    [InlineData("smileycoin-scrypt")]
    [InlineData("smileycoin-skein")]
    [InlineData("smileycoin-qubit")]
    [InlineData("smileycoin-groestl")]
    [InlineData("susucoin")]
    [InlineData("viacoin")]
    [InlineData("worldcoin")]
    public void ReviewedOrdinaryTemplate_RecordsDefaultSafeMask(string key)
    {
        var template = GetTemplate(key);

        Assert.False(template.DisableVersionRolling);
        Assert.Equal(BitcoinConstants.VersionRollingPoolMask,
            template.AllowedVersionRollingMask);
        Assert.Equal(BitcoinConstants.VersionRollingPoolMask,
            BitcoinPool.ResolveVersionRollingMask(template, uint.MaxValue));
    }

    [Fact]
    public void VersionRollingExplicitMaskTemplates_MatchSourceReviewedSet()
    {
        var expected = new[]
        {
            "auroracoin-groestl", "auroracoin-qubit", "auroracoin-scrypt",
            "auroracoin-sha256", "auroracoin-skein", "butkoin-scrypt",
            "butkoin-sha256", "danecoin", "digibyte-odocrypt",
            "digibyte-qubit", "digibyte-scrypt", "digibyte-sha256",
            "digibyte-skein", "litecoin-cash", "litecoin-cash-minotaurx",
            "maza", "maza-minotaurx", "mooncoin", "newyorkcoin", "pepepow",
            "plexhive", "plexhive-minotaurx", "pyrk-scrypt", "pyrk-sha256",
            "pyrk-x11", "skydoge", "smileycoin-groestl", "smileycoin-qubit",
            "smileycoin-scrypt", "smileycoin-sha256", "smileycoin-skein",
            "susucoin", "veles-scrypt", "veles-sha256", "verge-blake",
            "verge-groestl", "verge-lyra", "verge-scrypt", "verge-x17",
            "viacoin", "worldcoin",
        };
        var actual = ModuleInitializer.CoinTemplates
            .Where(x => x.Value is BitcoinTemplate
                {AllowedVersionRollingMask: not null})
            .Select(x => x.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PepePow_ExcludesConsensusOwnedAlgorithmBits()
    {
        var template = GetTemplate("pepepow");
        const uint requestedMask = 0x0000e000;
        const uint templateVersion = 0x0000c004;

        Assert.Equal(0x0000c000u, template.VersionRollingConsensusMask);
        Assert.Equal(0x1fff2000u, template.AllowedVersionRollingMask);
        var negotiatedMask = BitcoinPool.ResolveVersionRollingMask(template,
            requestedMask);

        Assert.Equal(0x00002000u, negotiatedMask);
        Assert.Equal(0x0000c000u, BitcoinJob.ApplyVersionRolling(
            templateVersion, negotiatedMask, 0) & 0x0000c000u);
        Assert.Equal(0x0000c000u, BitcoinJob.ApplyVersionRolling(
            templateVersion, negotiatedMask, uint.MaxValue) & 0x0000c000u);
    }

    [Fact]
    public void VersionRollingNegotiation_ClipsExpansionAndDeclinesDisjointMask()
    {
        var template = new BitcoinTemplate
            {AllowedVersionRollingMask = 0x00006000};

        Assert.Equal(0x00002000u,
            BitcoinPool.ResolveVersionRollingMask(template, 0x20002000));
        Assert.Null(BitcoinPool.ResolveVersionRollingMask(template, 0x00008000));
    }

    [Theory]
    [InlineData("verge-scrypt")]
    [InlineData("verge-groestl")]
    [InlineData("verge-x17")]
    [InlineData("verge-blake")]
    [InlineData("verge-lyra")]
    [InlineData("butkoin-scrypt")]
    [InlineData("butkoin-sha256")]
    public void MultiAlgorithmTemplate_ExcludesConsensusSelectorBits(string key)
    {
        var template = GetTemplate(key);

        Assert.Equal(0x1fff8000u, template.AllowedVersionRollingMask);
        Assert.Equal(0x00007800u, template.VersionRollingConsensusMask);
        Assert.Equal(0u,
            template.AllowedVersionRollingMask.Value &
            template.VersionRollingConsensusMask.Value);
        Assert.Null(
            BitcoinPool.ResolveVersionRollingMask(template, 0x00006000));
    }

    [Theory]
    [InlineData("veles-sha256", 0x1fff0000u, 0x0000ff00u)]
    [InlineData("veles-scrypt", 0x1fff0000u, 0x0000ff00u)]
    [InlineData("litecoin-cash", 0x1f00e000u, 0x00ff0000u)]
    [InlineData("litecoin-cash-minotaurx", 0x1f00e000u, 0x00ff0000u)]
    [InlineData("maza", 0x1f00e000u, 0x00ff0000u)]
    [InlineData("maza-minotaurx", 0x1f00e000u, 0x00ff0000u)]
    [InlineData("plexhive", 0x1f00e000u, 0x00ff0000u)]
    [InlineData("plexhive-minotaurx", 0x1f00e000u, 0x00ff0000u)]
    public void VersionSelectedPowTemplate_PreservesConsensusSelectorBits(
        string key, uint allowedMask, uint consensusMask)
    {
        var template = GetTemplate(key);
        var templateVersion = consensusMask | 4u;
        var negotiatedMask = BitcoinPool.ResolveVersionRollingMask(template,
            uint.MaxValue);

        Assert.Equal(allowedMask, template.AllowedVersionRollingMask);
        Assert.Equal(consensusMask, template.VersionRollingConsensusMask);
        Assert.Equal(0u, allowedMask & consensusMask);
        Assert.Null(BitcoinPool.ResolveVersionRollingMask(template,
            consensusMask));
        Assert.Equal(consensusMask, BitcoinJob.ApplyVersionRolling(
            templateVersion, negotiatedMask, 0) & consensusMask);
        Assert.Equal(consensusMask, BitcoinJob.ApplyVersionRolling(
            templateVersion, negotiatedMask, uint.MaxValue) & consensusMask);
    }

    [Theory]
    [InlineData("auroracoin-sha256", 0x00000f00u)]
    [InlineData("auroracoin-scrypt", 0x00000f00u)]
    [InlineData("auroracoin-skein", 0x00000f00u)]
    [InlineData("auroracoin-qubit", 0x00000f00u)]
    [InlineData("auroracoin-groestl", 0x00000f00u)]
    [InlineData("digibyte-sha256", 0x00000f00u)]
    [InlineData("digibyte-scrypt", 0x00000f00u)]
    [InlineData("digibyte-skein", 0x00000f00u)]
    [InlineData("digibyte-qubit", 0x00000f00u)]
    [InlineData("digibyte-odocrypt", 0x00000f00u)]
    [InlineData("smileycoin-sha256", 0x00000e00u)]
    [InlineData("smileycoin-scrypt", 0x00000e00u)]
    [InlineData("smileycoin-skein", 0x00000e00u)]
    [InlineData("smileycoin-qubit", 0x00000e00u)]
    [InlineData("smileycoin-groestl", 0x00000e00u)]
    [InlineData("pyrk-sha256", 0x00000e00u)]
    [InlineData("pyrk-scrypt", 0x00000e00u)]
    [InlineData("pyrk-x11", 0x00000e00u)]
    public void ReviewedLowSelectorTemplate_RecordsDisjointConsensusBits(
        string key, uint consensusMask)
    {
        var template = GetTemplate(key);

        Assert.Equal(BitcoinConstants.VersionRollingPoolMask,
            template.AllowedVersionRollingMask);
        Assert.Equal(consensusMask, template.VersionRollingConsensusMask);
        Assert.Equal(0u,
            template.AllowedVersionRollingMask.Value & consensusMask);
    }

    [Fact]
    public void VergeScrypt_UsesAuthoritativeVergeSourceMetadata()
    {
        Assert.Equal("https://github.com/vergecurrency/VERGE",
            GetTemplate("verge-scrypt").Github);
    }

    [Theory]
    [InlineData("pyrk-sha256")]
    [InlineData("pyrk-scrypt")]
    [InlineData("pyrk-x11")]
    public void PyrkTemplate_UsesReviewedSourceMetadata(string key)
    {
        Assert.Equal("https://github.com/pyrkcommunity/pyrk",
            GetTemplate(key).Github);
    }

    [Theory]
    [InlineData("1fffe000", 0x1fffe000u)]
    [InlineData("1FFFE000", 0x1fffe000u)]
    [InlineData("0x1fffe000", 0x1fffe000u)]
    [InlineData("0X1FFFE000", 0x1fffe000u)]
    public void MinerVersionRollingMask_AcceptsBip310AndDefensivePrefixForms(
        string value, uint expected)
    {
        Assert.True(BitcoinPool.TryParseRequestedVersionRollingMask(
            JValue.CreateString(value), out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(123)]
    [InlineData("0x1fffe00")]
    [InlineData("0x1fffe00000")]
    [InlineData("1fffe00z")]
    public void MinerVersionRollingMask_DeclinesMalformedValues(object value)
    {
        var token = value == null ? JValue.CreateNull() : JToken.FromObject(value);
        var result = BitcoinPool.NegotiateVersionRolling(new BitcoinTemplate(),
            true, token);

        Assert.Equal(BitcoinPool.VersionRollingNegotiationStatus.InvalidMinerMask,
            result.Status);
        Assert.Null(result.Mask);
    }

    [Fact]
    public void VersionRollingNegotiation_AppliesFailClosedWireResults()
    {
        var disabled = BitcoinPool.NegotiateVersionRolling(new BitcoinTemplate
        {
            DisableVersionRolling = true,
        }, true, new JObject());
        var disjoint = BitcoinPool.NegotiateVersionRolling(new BitcoinTemplate
        {
            AllowedVersionRollingMask = 0x00002000,
        }, true, JValue.CreateString("00004000"));
        var enabled = BitcoinPool.NegotiateVersionRolling(new BitcoinTemplate
        {
            AllowedVersionRollingMask = 0x00002000,
        }, true, JValue.CreateString("00006000"));
        var invalid = BitcoinPool.NegotiateVersionRolling(new BitcoinTemplate(),
            true, new JObject());

        Assert.Equal(BitcoinPool.VersionRollingNegotiationStatus.TemplateDisabled,
            disabled.Status);
        Assert.Equal(BitcoinPool.VersionRollingNegotiationStatus.DisjointMask,
            disjoint.Status);
        Assert.Equal(BitcoinPool.VersionRollingNegotiationStatus.InvalidMinerMask,
            invalid.Status);
        Assert.Equal(BitcoinPool.VersionRollingNegotiationStatus.Enabled,
            enabled.Status);
        Assert.Equal(0x00002000u, enabled.Mask);

        var context = new BitcoinWorkerContext
            {VersionRollingMask = 0x1fffe000};
        var wireResult = new Dictionary<string, object>
        {
            [BitcoinStratumExtensions.VersionRollingMask] = "1fffe000",
        };

        foreach(var declined in new[] {disabled, invalid, disjoint})
        {
            context.VersionRollingMask = 0x1fffe000;
            wireResult[BitcoinStratumExtensions.VersionRollingMask] =
                "1fffe000";

            BitcoinPool.ApplyVersionRollingNegotiation(context, wireResult,
                declined);

            Assert.False(Assert.IsType<bool>(
                wireResult[BitcoinStratumExtensions.VersionRolling]));
            Assert.False(wireResult.ContainsKey(
                BitcoinStratumExtensions.VersionRollingMask));
            Assert.Null(context.VersionRollingMask);
        }

        BitcoinPool.ApplyVersionRollingNegotiation(context, wireResult,
            enabled);

        Assert.True(Assert.IsType<bool>(
            wireResult[BitcoinStratumExtensions.VersionRolling]));
        Assert.Equal("00002000",
            wireResult[BitcoinStratumExtensions.VersionRollingMask]);
        Assert.Equal(0x00002000u, context.VersionRollingMask);

        Assert.Throws<InvalidOperationException>(() =>
            BitcoinPool.ApplyVersionRollingNegotiation(context, wireResult,
                new BitcoinPool.VersionRollingNegotiation(
                    BitcoinPool.VersionRollingNegotiationStatus.Enabled,
                    null)));
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
    public void TheMinerzCoin_SyntheticVersionSevenUsesSha256dBlockIdentity()
    {
        // This synthetic version-seven header pins the daemon's > 6 hasher branch;
        // it is not represented as a captured TheMinerzCoin mainnet block.
        const string currentVersionHeader =
            "07000000" +
            "0000000000000000000000000000000000000000000000000000000000000000" +
            "3ba3edfd7a7b12b27ac72c3e67768f617fc81bc3888a51323a9fb8aa4b1e5e4a" +
            "29ab5f49" +
            "ffff001d" +
            "1dac2b7c";
        var output = new byte[32];
        var hasher = Assert.IsType<DigestReverser>(
            GetTemplate("theminerzcoin").PoSBlockHasherValue);

        Assert.IsType<Sha256D>(hasher.Upstream);
        hasher.Digest(currentVersionHeader.HexToByteArray(), output);
        Assert.Equal(
            "6ffc18484ebcd9ef2cf5e5935eee3c4f06faef2718bcd54c44cd4310cf9dbaf5",
            output.ToHexString());
    }

    [Fact]
    public void BlockChainCoinX_MissingPoolPublicKeyFailsWithNamedDiagnostic()
    {
        var pool = new PoolConfig
        {
            Id = "xccx-test",
            Template = GetTemplate("blockchaincoinx"),
        };

        var ex = Assert.Throws<PoolStartupException>(() =>
            BitcoinJobManagerBase<BitcoinJob>.ResolvePoolPublicKey(pool,
                new ValidateAddressResponse()));

        Assert.Equal("xccx-test", ex.PoolId);
        Assert.Contains("requires 'pubKey'", ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BlockChainCoinX_ConfiguredPoolPublicKeyIsAccepted()
    {
        var expected = new Key().PubKey;
        var pool = new PoolConfig
        {
            Id = "xccx-test",
            PubKey = expected.ToHex(),
            Template = GetTemplate("blockchaincoinx"),
        };

        var actual = BitcoinJobManagerBase<BitcoinJob>
            .ResolvePoolPublicKey(pool, new ValidateAddressResponse());

        Assert.Equal(expected.ToHex(), actual.ToHex());
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
