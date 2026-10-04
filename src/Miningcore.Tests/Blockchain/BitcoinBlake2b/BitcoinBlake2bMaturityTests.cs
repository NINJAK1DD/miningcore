using System;
using System.Collections.Generic;
using System.Linq;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Mining;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using RewardBlock = Miningcore.Persistence.Model.Block;
using BlockStatus = Miningcore.Persistence.Model.BlockStatus;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public class BitcoinBlake2bMaturityTests
{
    internal static JObject Deployment(BitcoinBlake2bMaturity schedule, int tip) => new()
    {
        ["height"] = tip,
        ["blake2b"] = new JObject { ["height"] = 20, ["active"] = true },
        ["deployments"] = new JObject { ["long_coinbase_maturity"] = new JObject
        {
            ["type"] = "flagday", ["height"] = schedule.Enforce, ["height_end"] = schedule.Release - 1,
            ["coinbase_start_height"] = schedule.Start, ["maturity"] = schedule.Maturity,
            ["active"] = schedule.ActiveAt((long) tip + 1),
        } },
    };

    [Theory]
    [InlineData(973439, 973440, 100)]
    [InlineData(973440, 973439, 100)]
    [InlineData(973440, 973440, 6480)]
    [InlineData(973440, 979919, 6480)]
    [InlineData(973440, 979920, 100)]
    public void ConsensusEligibility_IsIndependentOfPersistentWalletPolicy(int coinbase, int spend, int depth)
    {
        Assert.Equal(depth, BitcoinBlake2bMaturity.Mainnet.ConsensusDepth(coinbase, spend));
        Assert.Equal(6481, BitcoinBlake2bMaturity.Mainnet.WalletConfirmations);
    }

    [Theory]
    [InlineData(973438, false)]
    [InlineData(973439, true)]
    [InlineData(979918, true)]
    [InlineData(979919, false)]
    public void DeploymentAndGbt_UseNextHeightAndInclusiveRpcEnd(int tip, bool active)
    {
        var schedule = BitcoinBlake2bMaturity.Mainnet;
        schedule.ValidateDeployment(Deployment(schedule, tip), "test");
        Assert.Equal(active, schedule.ActiveAt(tip + 1));
        var rules = active ? new[] { "!blake2b", "long_coinbase_maturity" } : new[] { "!blake2b" };
        schedule.ValidateRules(rules, (uint) tip + 1, "test");
        Assert.Throws<PoolStartupException>(() => schedule.ValidateRules(
            active ? new[] { "!blake2b" } : new[] { "!blake2b", "long_coinbase_maturity" }, (uint) tip + 1, "test"));
    }

    [Theory]
    [InlineData("height", "973441")]
    [InlineData("height_end", "979920")]
    [InlineData("coinbase_start_height", "973439")]
    [InlineData("maturity", "100")]
    [InlineData("maturity", "\"6480\"")]
    [InlineData("active", "false")]
    [InlineData("active", "1")]
    [InlineData("type", "\"bip9\"")]
    [InlineData("expiry_time", "1819756800")]
    public void Deployment_RejectsContradictionsAndUnreviewedFieldContract(string field, string value)
    {
        var info = Deployment(BitcoinBlake2bMaturity.Mainnet, 973439);
        info["deployments"]["long_coinbase_maturity"][field] = JToken.Parse(value);
        Assert.Throws<PoolStartupException>(() => BitcoinBlake2bMaturity.Mainnet.ValidateDeployment(info, "test"));
    }

    [Fact]
    public void MissingDeployment_IsAllowedOnlyForExplicitUnscheduledRegtest()
    {
        var info = new JObject { ["height"] = 19, ["deployments"] = new JObject() };
        BitcoinBlake2bMaturity.Regtest.ValidateDeployment(info, "test");
        Assert.Throws<PoolStartupException>(() => BitcoinBlake2bMaturity.Mainnet.ValidateDeployment(info, "test"));
        Assert.Throws<PoolStartupException>(() => new BitcoinBlake2bMaturity(2, 104, 106, 104).ValidateDeployment(info, "test"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"unexpected\"")]
    [InlineData("[]")]
    [InlineData("5")]
    public void MalformedDeploymentContainer_TakesTerminalContractPath(string json)
    {
        var info = Deployment(BitcoinBlake2bMaturity.Mainnet, 973439);
        info["deployments"] = JToken.Parse(json);
        Assert.Throws<PoolStartupException>(() => BitcoinBlake2bMaturity.Mainnet.ValidateDeployment(info, "test"));
    }

    [Theory]
    [InlineData("!future-rule")]
    [InlineData("!long_coinbase_maturity")]
    public void UnknownMandatoryRules_FailClosed(string rule) =>
        Assert.Throws<PoolStartupException>(() => BitcoinBlake2bMaturity.Mainnet.ValidateRules(
            new[] { "!blake2b", "long_coinbase_maturity", rule }, 973440, "test"));

    [Theory]
    [InlineData(102, "immature", 6380, false)]
    [InlineData(6480, "immature", 2, false)]
    [InlineData(6481, "generate", 1, false)]
    [InlineData(6482, "generate", 0, true)]
    [InlineData(7000, "immature", 0, false)]
    public void Reward_RequiresWalletSpendabilityAndOperatorPolicy(int confirmations, string category, int remaining, bool confirmed)
    {
        var schedule = BitcoinBlake2bMaturity.Mainnet;
        var block = new RewardBlock { Status = BlockStatus.Pending };
        BitcoinPayoutHandler.ApplyBlake2bReward(block, new Transaction
        {
            Confirmations = confirmations, Amount = 50,
            Details = new[] { new TransactionDetails { Category = category } },
        }, schedule, 6482);
        Assert.Equal(remaining, schedule.Remaining(confirmations, 6482));
        Assert.Equal(confirmed ? BlockStatus.Confirmed : BlockStatus.Pending, block.Status);
        Assert.Equal(confirmed, block.NotifyBlockUnlockedOnUpdate);
        if(!confirmed) Assert.InRange(block.ConfirmationProgress, 0, Math.BitDecrement(1.0));
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"difficulty\":123}", null)]
    [InlineData("{\"difficulty_blake2b\":4294967296}", 4294967296.0)]
    [InlineData("{\"difficulty_blake2b\":2.00001,\"next\":{\"height\":20,\"difficulty_blake2b\":4}}", 2.00001)]
    public void RpcDifficulty_KeepsOldAndExpectedWorkShapesDistinct(string json, double? expected)
    {
        var mining = JsonConvert.DeserializeObject<MiningInfo>(json);
        Assert.Equal(expected, mining.Blake2bExpectedHashWork?.ExpectedHashes);
        var historical = JsonConvert.DeserializeObject<Miningcore.Blockchain.Bitcoin.DaemonResponses.Block>("{\"difficulty\":123}");
        Assert.Equal(123, historical.Difficulty);
        Assert.Null(historical.Blake2bExpectedHashWork);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1e309")]
    [InlineData("\"2\"")]
    [InlineData("true")]
    [InlineData("{}")]
    public void RpcExpectedWork_RejectsMalformedPresentValues(string value) =>
        Assert.ThrowsAny<JsonException>(() => JsonConvert.DeserializeObject<MiningInfo>("{\"difficulty_blake2b\":" + value + "}"));

    [Fact]
    public void HeaderV2_PowContractCannotBeChangedByLegacyStakeShape()
    {
        Assert.False(BitcoinJobManagerBase<BitcoinJob>.ResolveProofOfStakeMode(
            new BitcoinBlake2bTemplate { IsPseudoPoS = true }, JObject.Parse("{\"proof-of-stake\":1}")));
    }
}
