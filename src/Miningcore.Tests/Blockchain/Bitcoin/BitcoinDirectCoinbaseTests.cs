using System;
using System.IO;
using System.Linq;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using NBitcoin;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinDirectCoinbaseTests
{
    [Fact]
    public void Split_UsesExactFloorRoundingAndMinerResidual()
    {
        var template = CreateTemplate(1.25m, 0.1m);

        var result = BitcoinDirectCoinbase.Split(312_500_001, template);

        Assert.Equal(312_500_001, result.GrossRewardSatoshis);
        Assert.Equal(4_218_750, result.RecipientOutputs.Sum(x =>
            x.AmountSatoshis));
        Assert.Equal(308_281_251, result.MinerRewardSatoshis);
        Assert.Equal(result.GrossRewardSatoshis,
            result.MinerRewardSatoshis +
            result.RecipientOutputs.Sum(x => x.AmountSatoshis));
    }

    [Fact]
    public void ValidateRecipients_IgnoresZeroAndOrdersByScript()
    {
        var first = Address(Network.RegTest);
        var second = Address(Network.RegTest);
        var recipients = BitcoinDirectCoinbase.ValidateRecipients(new[]
        {
            new RewardRecipient { Address = "placeholder", Percentage = 0 },
            new RewardRecipient { Address = second.ToString(), Percentage = 2 },
            new RewardRecipient { Address = first.ToString(), Percentage = 1 },
        }, value => BitcoinAddress.Create(value, Network.RegTest));

        Assert.Equal(2, recipients.Length);
        Assert.True(string.CompareOrdinal(recipients[0].ScriptPubKey,
            recipients[1].ScriptPubKey) < 0);
    }

    [Fact]
    public void ValidateRecipients_RejectsDuplicateScripts()
    {
        var address = Address(Network.RegTest).ToString();

        Assert.Throws<InvalidDataException>(() =>
            BitcoinDirectCoinbase.ValidateRecipients(new[]
            {
                new RewardRecipient { Address = address, Percentage = 1 },
                new RewardRecipient { Address = address, Percentage = 2 },
            }, value => BitcoinAddress.Create(value, Network.RegTest)));
    }

    [Theory]
    [InlineData("100")]
    [InlineData("100.00000001")]
    public void ValidateRecipients_RejectsTotalsWithoutMinerResidual(
        string percentage)
    {
        var address = Address(Network.RegTest).ToString();

        Assert.Throws<InvalidDataException>(() =>
            BitcoinDirectCoinbase.ValidateRecipients(new[]
            {
                new RewardRecipient
                {
                    Address = address,
                    Percentage = decimal.Parse(percentage),
                },
            }, value => BitcoinAddress.Create(value, Network.RegTest)));
    }

    [Fact]
    public void Split_RejectsPositiveRecipientBelowOneSatoshi()
    {
        var template = CreateTemplate(0.00000001m);

        Assert.Throws<InvalidDataException>(() =>
            BitcoinDirectCoinbase.Split(1, template));
    }

    [Fact]
    public void Split_AllowsExactlyOneSatoshiMinerResidual()
    {
        var result = BitcoinDirectCoinbase.Split(100,
            CreateTemplate(99m));

        Assert.Equal(99, Assert.Single(result.RecipientOutputs)
            .AmountSatoshis);
        Assert.Equal(1, result.MinerRewardSatoshis);
    }

    [Fact]
    public void Split_RemainsExactAtInt64Boundary()
    {
        var result = BitcoinDirectCoinbase.Split(long.MaxValue,
            CreateTemplate(1.23456789m));

        Assert.Equal(long.MaxValue,
            checked(result.MinerRewardSatoshis +
                result.RecipientOutputs.Sum(x => x.AmountSatoshis)));
    }

    [Fact]
    public void MinerDestination_CannotDuplicateRecipientScript()
    {
        var miner = Address(Network.RegTest);
        var recipient = new BitcoinDirectCoinbaseRecipient
        {
            Address = miner.ToString(),
            Destination = miner,
            Percentage = 1,
            ScriptPubKey = miner.ScriptPubKey.ToHex(),
        };
        var template = new BitcoinDirectCoinbaseTemplate
        {
            MinerAddress = miner.ToString(),
            MinerDestination = miner,
            MinerScriptPubKey = miner.ScriptPubKey.ToHex(),
            Recipients = new[] { recipient },
        };

        Assert.Throws<InvalidDataException>(() =>
            BitcoinDirectCoinbase.EnsureMinerIsDistinct(template));
    }

    [Fact]
    public void WrongNetworkRecipient_IsRejected()
    {
        var mainnet = Address(Network.Main).ToString();

        Assert.Throws<InvalidDataException>(() =>
            BitcoinDirectCoinbase.ValidateRecipients(new[]
            {
                new RewardRecipient { Address = mainnet, Percentage = 1 },
            }, value => BitcoinAddress.Create(value, Network.RegTest)));
    }

    [Fact]
    public void MainnetTestnetAndRegtestRecipients_UseTheirSelectedNetwork()
    {
        foreach(var network in new[]
                    {
                        Network.Main,
                        Network.TestNet,
                        Network.RegTest,
                    })
        {
            var address = Address(network).ToString();
            var recipient = Assert.Single(
                BitcoinDirectCoinbase.ValidateRecipients(new[]
                {
                    new RewardRecipient
                    {
                        Address = address,
                        Percentage = 1,
                    },
                }, value => BitcoinAddress.Create(value, network)));

            Assert.Equal(address, recipient.Address);
        }
    }

    [Fact]
    public void DirectResolver_AcceptsNativeSegwitAndRejectsWrongNetwork()
    {
        foreach(var network in new[]
                    {
                        Network.Main,
                        Network.TestNet,
                        Network.RegTest,
                    })
        {
            var address = Address(network);
            var destination = BitcoinJobManager
                .ResolveDirectPayoutDestination(address.ToString(), network);

            Assert.Equal(address.ScriptPubKey,
                destination.ScriptPubKey);
        }

        var mainnet = Address(Network.Main);
        Assert.Throws<FormatException>(() => BitcoinJobManager
            .ResolveDirectPayoutDestination(mainnet.ToString(),
                Network.RegTest));
    }

    [Fact]
    public void ValidateRecipients_BoundsPositiveOutputCount()
    {
        var recipients = Enumerable.Range(0,
                BitcoinDirectCoinbase.MaximumRecipientOutputs + 1)
            .Select(_ => new RewardRecipient
            {
                Address = Address(Network.RegTest).ToString(),
                Percentage = 0.01m,
            }).ToArray();

        Assert.Throws<InvalidDataException>(() =>
            BitcoinDirectCoinbase.ValidateRecipients(recipients,
                value => BitcoinAddress.Create(value, Network.RegTest)));
    }

    private static BitcoinDirectCoinbaseTemplate CreateTemplate(
        params decimal[] percentages)
    {
        var miner = Address(Network.RegTest);
        var recipients = percentages.Select(percentage =>
        {
            var address = Address(Network.RegTest);
            return new BitcoinDirectCoinbaseRecipient
            {
                Address = address.ToString(),
                Destination = address,
                Percentage = percentage,
                ScriptPubKey = address.ScriptPubKey.ToHex(),
            };
        }).OrderBy(x => x.ScriptPubKey, StringComparer.Ordinal).ToArray();

        return new BitcoinDirectCoinbaseTemplate
        {
            MinerAddress = miner.ToString(),
            MinerDestination = miner,
            MinerScriptPubKey = miner.ScriptPubKey.ToHex(),
            Recipients = recipients,
        };
    }

    private static BitcoinAddress Address(Network network) =>
        new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, network);
}
