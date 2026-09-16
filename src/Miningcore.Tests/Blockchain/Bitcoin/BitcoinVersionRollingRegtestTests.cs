using System;
using System.Globalization;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Extensions;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

[Collection(BitcoinCorePayoutIntegrationCollection.Name)]
public class BitcoinVersionRollingRegtestTests
{
    [BitcoinCoreIntegrationFact]
    public async Task SourceVerifiedMask_SubmitsRolledHeaderToBitcoinCore()
    {
        await using var node = await BitcoinPayoutHandlerRegtestTests
            .BitcoinCoreRegtestNode.StartAsync(walletBroadcast: true);
        var blockTemplate = Assert.IsType<JObject>(await node.RootRpcAsync(
            "getblocktemplate", new JObject
            {
                ["rules"] = new JArray("segwit"),
            }));
        var template = new BitcoinTemplate
        {
            AllowedVersionRollingMask = 0x00002000,
            VersionRollingConsensusMask = 0x00004000,
        };
        const uint requestedMask = 0x00006000;
        var negotiatedMask = BitcoinPool.ResolveVersionRollingMask(template,
            requestedMask);
        var templateVersion = blockTemplate.Value<uint>("version") |
            template.AllowedVersionRollingMask.Value |
            template.VersionRollingConsensusMask.Value;
        var rolledVersion = BitcoinJob.ApplyVersionRolling(templateVersion,
            negotiatedMask, 0);
        var header = Network.RegTest.Consensus.ConsensusFactory.CreateBlockHeader();

        header.Version = unchecked((int) rolledVersion);
        header.HashPrevBlock = uint256.Parse(
            blockTemplate.Value<string>("previousblockhash"));
        header.HashMerkleRoot = uint256.One;
        header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(
            blockTemplate.Value<long>("curtime"));
        header.Bits = new Target(uint.Parse(blockTemplate.Value<string>("bits"),
            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));

        const int maxNonceAttempts = 1_000_000;

        for(var attempt = 0;
            attempt < maxNonceAttempts && !header.CheckProofOfWork();
            attempt++)
        {
            header.Nonce++;
        }

        Assert.True(header.CheckProofOfWork(),
            $"Regtest header did not reach its target within {maxNonceAttempts} nonces");
        Assert.Equal(0x00002000u, negotiatedMask);
        Assert.NotEqual(templateVersion, rolledVersion);
        Assert.Equal(0x00004000u, rolledVersion & 0x00004000u);

        await node.RootRpcAsync("submitheader", header.ToBytes().ToHexString());
        var acceptedHeader = Assert.IsType<JObject>(await node.RootRpcAsync(
            "getblockheader", header.GetHash().ToString()));

        Assert.Equal(rolledVersion, acceptedHeader.Value<uint>("version"));
    }
}
