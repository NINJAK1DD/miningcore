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
            VersionRollingMask = BitcoinConstants.VersionRollingPoolMask,
        };
        const uint requestedMask = 0x00002000;
        var negotiatedMask = BitcoinPool.ResolveVersionRollingMask(template,
            requestedMask);
        var templateVersion = blockTemplate.Value<uint>("version");
        var rolledVersion = BitcoinJob.ApplyVersionRolling(templateVersion,
            negotiatedMask, requestedMask);
        var header = Network.RegTest.Consensus.ConsensusFactory.CreateBlockHeader();

        header.Version = unchecked((int) rolledVersion);
        header.HashPrevBlock = uint256.Parse(
            blockTemplate.Value<string>("previousblockhash"));
        header.HashMerkleRoot = uint256.One;
        header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(
            blockTemplate.Value<long>("curtime"));
        header.Bits = new Target(uint.Parse(blockTemplate.Value<string>("bits"),
            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));

        while(!header.CheckProofOfWork())
            header.Nonce++;

        Assert.Equal(requestedMask, negotiatedMask);
        Assert.NotEqual(templateVersion, rolledVersion);

        await node.RootRpcAsync("submitheader", header.ToBytes().ToHexString());
        var acceptedHeader = Assert.IsType<JObject>(await node.RootRpcAsync(
            "getblockheader", header.GetHash().ToString()));

        Assert.Equal(rolledVersion, acceptedHeader.Value<uint>("version"));
    }
}
