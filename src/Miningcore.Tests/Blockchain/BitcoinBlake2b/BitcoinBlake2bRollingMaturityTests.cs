using System;
using System.Threading.Tasks;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Mining;
using Miningcore.Rpc;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public class BitcoinBlake2bRollingMaturityTests
{
    private const long Cutoff = 1819756800;

    [Theory]
    [InlineData(973439, 973539, 100)]
    [InlineData(973440, 973540, 6480)]
    [InlineData(973440, 979918, 6480)]
    [InlineData(973440, 979919, 6480)]
    [InlineData(973440, 979920, 100)]
    [InlineData(979918, 980018, 100)]
    [InlineData(979919, 980019, 25920)]
    [InlineData(979919, 1005838, 25920)]
    [InlineData(979919, 1005839, 25920)]
    [InlineData(979920, 1005840, 25920)]
    public void MainnetConsensus_PreservesHistoricalOverlapAndRollingDepth(uint reward, long spend, int depth)
    {
        Assert.Equal(depth, BitcoinBlake2bMaturity.Mainnet.ConsensusMaturity(reward, spend, Cutoff - 1));
    }

    [Theory]
    [InlineData(973439, 973538, false)]
    [InlineData(973439, 973539, true)]
    [InlineData(973440, 979918, false)]
    [InlineData(973440, 979919, false)]
    [InlineData(973440, 979920, true)]
    [InlineData(979918, 980017, false)]
    [InlineData(979918, 980018, true)]
    [InlineData(979919, 1005838, false)]
    [InlineData(979919, 1005839, true)]
    public void SourceSpendBoundaries_DifferFromWalletRpcMargin(uint reward, long spend, bool allowed)
    {
        var depth = BitcoinBlake2bMaturity.Mainnet.ConsensusMaturity(reward, spend, Cutoff - 1);
        Assert.Equal(allowed, spend - reward >= depth);
    }

    [Theory]
    [InlineData(973439, Cutoff - 1, 6480)]
    [InlineData(973440, Cutoff - 1, 6480)]
    [InlineData(979918, Cutoff - 1, 6480)]
    [InlineData(979919, Cutoff - 1, 25920)]
    [InlineData(979920, Cutoff - 1, 25920)]
    [InlineData(979919, Cutoff, 100)]
    [InlineData(979919, Cutoff + 1, 100)]
    public void WalletPolicy_IsPerRewardAndKeepsItsExtraConfirmation(uint height, long parentMtp, int depth)
    {
        var schedule = BitcoinBlake2bMaturity.Mainnet;
        var selected = schedule.PolicyMaturity(height, parentMtp);
        Assert.Equal(depth, selected);
        Assert.Equal(depth + 1, schedule.RequiredConfirmations(1, selected));
        Assert.Equal(1, schedule.Remaining(depth, 1, selected));
        Assert.Equal(0, schedule.Remaining(depth + 1, 1, selected));
        Assert.True(schedule.Progress(depth, 1, false, selected) < 1);
        Assert.Equal(1, schedule.Progress(depth + 1, 1, false, selected));
        Assert.True(schedule.Progress(depth + 1, 1, true, selected) < 1);
        Assert.Equal(30000, schedule.RequiredConfirmations(30000, selected));
        Assert.True(schedule.ActiveAt(2000000)); // expiry never disables GBT
    }

    [Theory]
    [InlineData("height_end", "979919")]
    [InlineData("coinbase_expiry_time", "1819756799")]
    [InlineData("coinbase_expiry_time", "\"1819756800\"")]
    [InlineData("height", "973440")]
    [InlineData("coinbase_start_height", "979918")]
    [InlineData("maturity", "6480")]
    [InlineData("active", "false")]
    public void RollingDeployment_RejectsMixedHistoricalOrMalformedFields(string field, string value)
    {
        var info = BitcoinBlake2bMaturityTests.Deployment(BitcoinBlake2bMaturity.Mainnet, 979918);
        info["deployments"]["long_coinbase_maturity"][field] = JToken.Parse(value);
        Assert.Throws<PoolStartupException>(() => BitcoinBlake2bMaturity.Mainnet.ValidateDeployment(info, "test"));
    }

    [Fact]
    public void RollingDeployment_RequiresEveryReviewedField()
    {
        foreach(var field in new[] { "height", "coinbase_start_height", "coinbase_expiry_time", "maturity", "active", "type" })
        {
            var info = BitcoinBlake2bMaturityTests.Deployment(BitcoinBlake2bMaturity.Mainnet, 979918);
            ((JObject) info["deployments"]["long_coinbase_maturity"]).Remove(field);
            Assert.Throws<PoolStartupException>(() => BitcoinBlake2bMaturity.Mainnet.ValidateDeployment(info, "test"));
        }
    }

    [Fact]
    public async Task SeparateRpcSnapshots_AllowBoundaryAdvancementButWithholdOrphanedEvidence()
    {
        var oldHash = new string('a', 64);
        var newHash = new string('b', 64);
        var chain = new JObject { ["blocks"] = 979917, ["bestblockhash"] = oldHash };
        var deployment = BitcoinBlake2bMaturityTests.Deployment(BitcoinBlake2bMaturity.Mainnet, 979918);
        deployment["hash"] = newHash;
        BitcoinBlake2bMaturity.Mainnet.ValidateDeployment(deployment, "test");
        var orphan = false;
        Task<RpcResponse<JObject>> Header(string hash) => Task.FromResult(new RpcResponse<JObject>(new JObject
        {
            ["hash"] = hash, ["height"] = hash == oldHash ? 979917 : 979918,
            ["confirmations"] = orphan && hash == oldHash ? -1 : hash == oldHash ? 2 : 1,
            ["previousblockhash"] = hash == newHash ? oldHash : new string('0', 64),
        }));
        Assert.True(await BitcoinBlake2bChainEvidence.VerifyAsync(chain, deployment, Header, "test"));
        orphan = true;
        Assert.False(await BitcoinBlake2bChainEvidence.VerifyAsync(chain, deployment, Header, "test"));
    }

    [Theory]
    [InlineData("missing-hash")]
    [InlineData("string-height")]
    [InlineData("wrong-header-hash")]
    [InlineData("wrong-header-height")]
    [InlineData("missing-confirmations")]
    public async Task ChainEvidence_RejectsContradictionsAndMalformedSnapshots(string fault)
    {
        var hash = new string('a', 64);
        var chain = new JObject { ["blocks"] = 979918, ["bestblockhash"] = hash };
        var deployment = new JObject { ["height"] = 979918, ["hash"] = hash };
        var header = new JObject { ["height"] = 979918, ["hash"] = hash, ["confirmations"] = 1 };
        switch(fault)
        {
            case "missing-hash": deployment.Remove("hash"); break;
            case "string-height": chain["blocks"] = "979918"; break;
            case "wrong-header-hash": header["hash"] = new string('b', 64); break;
            case "wrong-header-height": header["height"] = 979917; break;
            case "missing-confirmations": header.Remove("confirmations"); break;
        }
        await Assert.ThrowsAsync<PoolStartupException>(() => BitcoinBlake2bChainEvidence.VerifyAsync(
            chain, deployment, _ => Task.FromResult(new RpcResponse<JObject>(header)), "test"));
    }

    [Fact]
    public void ProcessBinding_AllowsForwardAndReorgRpcPhasesWithoutChangingSchedule()
    {
        var tracker = new BitcoinBlake2bPayoutContractTracker();
        foreach(var tip in new[] { 979917, 979918, 979919, 979917, 1100000 })
        {
            BitcoinBlake2bMaturity.Mainnet.ValidateDeployment(
                BitcoinBlake2bMaturityTests.Deployment(BitcoinBlake2bMaturity.Mainnet, tip), "test");
            tracker.Attest("test", "main", BitcoinBlake2bMaturity.Mainnet);
        }
        Assert.Throws<InvalidOperationException>(() => tracker.Attest("test", "regtest", BitcoinBlake2bMaturity.Regtest));
        Assert.Throws<InvalidOperationException>(() => tracker.Attest("test", "main", BitcoinBlake2bMaturity.Mainnet with { Expiry = Cutoff + 1 }));
    }

    [Fact]
    public void RollingRegtest_PreStartWalletPolicyIsLongWhileConsensusIsOrdinary()
    {
        var coin = JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "coins.json")))["bitcoin-blake2b"].ToObject<Miningcore.Configuration.BitcoinBlake2bTemplate>();
        var network = coin.Networks["regtest"];
        network.Blake2bMaturityStart = 30;
        network.Blake2bMaturityEnforce = 134;
        network.Blake2bMaturityRelease = 140;
        network.Blake2bMaturityExpiry = Cutoff;
        var schedule = BitcoinBlake2bMaturity.ForNetwork(coin, "regtest", "test");
        Assert.Equal(110, schedule.PolicyMaturity(29, Cutoff - 1));
        Assert.Equal(111, schedule.RequiredConfirmations(1, schedule.PolicyMaturity(29, Cutoff - 1)));
        Assert.Equal(100, schedule.ConsensusMaturity(29, 140, Cutoff - 1));
        Assert.Equal(100, schedule.PolicyMaturity(29, Cutoff));
        Assert.False(schedule.HistoricalActiveAt(134));
        Assert.False(schedule.ActiveAt(133));
        Assert.True(schedule.ActiveAt(134));
        schedule.ValidateDeployment(BitcoinBlake2bMaturityTests.Deployment(schedule, 19), "test");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotReads_RejectMutuallyIncompatibleBranchesAndFinalReorg(bool finalReorg)
    {
        var low = new string('a', 64);
        var high = new string('b', 64);
        var chain = new JObject { ["blocks"] = 979917, ["bestblockhash"] = low };
        var deployment = new JObject { ["height"] = 979918, ["hash"] = high };
        var highReads = 0;
        Task<RpcResponse<JObject>> Header(string hash)
        {
            if(hash == high) highReads++;
            // Both snapshots are independently positive on different branches.
            // With finalReorg, ancestry agrees but the higher snapshot becomes
            // orphaned during verification. Neither interleaving may attest.
            return Task.FromResult(new RpcResponse<JObject>(new JObject
            {
                ["hash"] = hash, ["height"] = hash == low ? 979917 : 979918,
                ["confirmations"] = finalReorg && highReads == 2 ? -1 : 1,
                ["previousblockhash"] = finalReorg ? low : new string('e', 64),
            }));
        }
        Assert.Equal(BitcoinBlake2bChainObservation.ChainChanged,
            await BitcoinBlake2bChainEvidence.ObserveAsync(chain, deployment, Header, "test"));
        Assert.Equal(finalReorg ? 2 : 1, highReads);
    }

    [Fact]
    public async Task SnapshotHeightWindow_RejectsStaleDeploymentWithoutUnboundedAncestryRpc()
    {
        var reads = 0;
        var chain = new JObject { ["blocks"] = 979917, ["bestblockhash"] = new string('a', 64) };
        var deployment = new JObject { ["height"] = 979924, ["hash"] = new string('b', 64) };
        Task<RpcResponse<JObject>> Header(string _) { reads++; throw new InvalidOperationException(); }
        Assert.Equal(BitcoinBlake2bChainObservation.ChainChanged,
            await BitcoinBlake2bChainEvidence.ObserveAsync(chain, deployment, Header, "test"));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void CreationCache_IsBoundedLruPoolScopedAndRejectsContradictoryLinks()
    {
        var tracker = new BitcoinBlake2bPayoutContractTracker(2);
        BitcoinBlake2bBlockHeader Header(char value) => new(new string(value, 64), 30, 1, new string('d', 64), null);
        var a = Header('a'); var b = Header('b'); var c = Header('c');
        tracker.RememberCreation("pool", a, Cutoff - 1);
        tracker.RememberCreation("pool", b, Cutoff);
        Assert.True(tracker.TryCreation("pool", a with { Confirmations = 100 }, out var mtp, out var conflict));
        Assert.Equal(Cutoff - 1, mtp);
        Assert.False(conflict);
        tracker.RememberCreation("pool", c, Cutoff + 1);
        Assert.Equal(2, tracker.CachedCreationCount);
        Assert.False(tracker.TryCreation("pool", b, out _, out _));
        Assert.False(tracker.TryCreation("other", a, out _, out _));
        Assert.False(tracker.TryCreation("pool", a with { PreviousBlockHash = new string('e', 64) }, out _, out conflict));
        Assert.True(conflict);
        tracker.ForgetCreation("pool", a.Hash);
        Assert.Equal(1, tracker.CachedCreationCount);
        for(var i = 0; i < 10000; i++)
        {
            tracker.RememberCreation("pool", a, Cutoff);
            tracker.ForgetCreation("pool", a.Hash);
        }
        Assert.Equal(1, tracker.CachedCreationCount);
    }
}
