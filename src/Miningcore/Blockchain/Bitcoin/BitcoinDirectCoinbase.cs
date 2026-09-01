using System.Numerics;
using Miningcore.Configuration;
using NBitcoin;
using Newtonsoft.Json;

namespace Miningcore.Blockchain.Bitcoin;

public sealed class BitcoinDirectCoinbaseOutput
{
    public string Address { get; init; }
    public string ScriptPubKey { get; init; }
    public long AmountSatoshis { get; init; }
}

internal sealed class BitcoinDirectCoinbaseRecipient
{
    public string Address { get; init; }
    public decimal Percentage { get; init; }
    public IDestination Destination { get; init; }
    public string ScriptPubKey { get; init; }
}

internal sealed class BitcoinDirectCoinbaseTemplate
{
    public long AuthorizationGeneration { get; init; }
    public string MinerAddress { get; init; }
    public IDestination MinerDestination { get; init; }
    public string MinerScriptPubKey { get; init; }
    public IReadOnlyList<BitcoinDirectCoinbaseRecipient> Recipients { get; init; }
}

internal sealed class BitcoinDirectCoinbaseSettlement
{
    public const string Mode = "coinbase-direct";
    public const string BlockType = "bitcoin-coinbase-direct";

    public long GrossRewardSatoshis { get; init; }
    public long MinerRewardSatoshis { get; init; }
    public string MinerScriptPubKey { get; init; }
    public BitcoinDirectCoinbaseOutput[] RecipientOutputs { get; init; }

    public string SerializeRecipientOutputs() =>
        JsonConvert.SerializeObject(RecipientOutputs, Formatting.None);
}

internal static class BitcoinDirectCoinbase
{
    internal const long MinimumOutputSatoshis = 1;
    internal const int MaximumRecipientOutputs = 64;

    public static BitcoinDirectCoinbaseSettlement Split(long grossRewardSatoshis,
        BitcoinDirectCoinbaseTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(template.MinerDestination);

        if(grossRewardSatoshis <= 0)
            throw new InvalidDataException(
                "Direct SOLO coinbase value must be positive");

        var outputs = new List<BitcoinDirectCoinbaseOutput>();
        long directRecipientTotal = 0;

        foreach(var recipient in template.Recipients ??
                    Array.Empty<BitcoinDirectCoinbaseRecipient>())
        {
            var amount = FloorPercentage(grossRewardSatoshis,
                recipient.Percentage);
            if(amount < MinimumOutputSatoshis)
                throw new InvalidDataException(
                    $"Direct SOLO recipient '{recipient.Address}' rounds below the adopted one-satoshi minimum output");

            try
            {
                directRecipientTotal = checked(directRecipientTotal + amount);
            }
            catch(OverflowException ex)
            {
                throw new InvalidDataException(
                    "Direct SOLO recipient outputs exceed the supported satoshi range", ex);
            }

            outputs.Add(new BitcoinDirectCoinbaseOutput
            {
                Address = recipient.Address,
                ScriptPubKey = recipient.ScriptPubKey,
                AmountSatoshis = amount,
            });
        }

        var minerAmount = grossRewardSatoshis - directRecipientTotal;
        if(minerAmount < MinimumOutputSatoshis)
            throw new InvalidDataException(
                "Direct SOLO recipients leave no positive miner coinbase output");

        return new BitcoinDirectCoinbaseSettlement
        {
            GrossRewardSatoshis = grossRewardSatoshis,
            MinerRewardSatoshis = minerAmount,
            MinerScriptPubKey = template.MinerScriptPubKey,
            RecipientOutputs = outputs.ToArray(),
        };
    }

    public static void ValidateTemplateAmount(long grossRewardSatoshis,
        IReadOnlyList<BitcoinDirectCoinbaseRecipient> recipients)
    {
        if(grossRewardSatoshis <= 0)
            throw new InvalidDataException(
                "Direct SOLO coinbase value must be positive");

        long total = 0;
        foreach(var recipient in recipients ??
                    Array.Empty<BitcoinDirectCoinbaseRecipient>())
        {
            var amount = FloorPercentage(grossRewardSatoshis,
                recipient.Percentage);
            if(amount < MinimumOutputSatoshis)
                throw new InvalidDataException(
                    $"Direct SOLO recipient '{recipient.Address}' rounds below the adopted one-satoshi minimum output");
            total = checked(total + amount);
        }

        if(grossRewardSatoshis - total < MinimumOutputSatoshis)
            throw new InvalidDataException(
                "Direct SOLO recipients leave no positive miner coinbase output");
    }

    public static BitcoinDirectCoinbaseRecipient[] ValidateRecipients(
        IEnumerable<RewardRecipient> configuredRecipients,
        Func<string, IDestination> resolveDestination)
    {
        ArgumentNullException.ThrowIfNull(resolveDestination);

        var result = new List<BitcoinDirectCoinbaseRecipient>();
        var scripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach(var recipient in configuredRecipients ??
                    Array.Empty<RewardRecipient>())
        {
            if(recipient == null)
                throw new InvalidDataException(
                    "Direct SOLO reward recipients must not contain null entries");
            if(recipient.Percentage < 0)
                throw new InvalidDataException(
                    "Direct SOLO reward-recipient percentages must not be negative");
            if(recipient.Percentage == 0)
                continue;
            if(string.IsNullOrWhiteSpace(recipient.Address))
                throw new InvalidDataException(
                    "Every positive direct SOLO reward recipient requires an address");

            IDestination destination;
            try
            {
                destination = resolveDestination(recipient.Address.Trim());
            }
            catch(Exception ex)
            {
                throw new InvalidDataException(
                    $"Direct SOLO reward recipient '{recipient.Address}' is not valid for the active Bitcoin network",
                    ex);
            }

            var script = destination.ScriptPubKey.ToHex();
            if(!scripts.Add(script))
                throw new InvalidDataException(
                    "Direct SOLO reward recipients contain duplicate payout scripts");

            result.Add(new BitcoinDirectCoinbaseRecipient
            {
                Address = recipient.Address.Trim(),
                Percentage = recipient.Percentage,
                Destination = destination,
                ScriptPubKey = script,
            });
        }

        decimal total;
        try
        {
            total = result.Sum(x => x.Percentage);
        }
        catch(OverflowException ex)
        {
            throw new InvalidDataException(
                "Direct SOLO reward-recipient percentages exceed the supported range", ex);
        }

        if(total >= 100m)
            throw new InvalidDataException(
                "Direct SOLO reward-recipient percentages must total less than 100%");

        if(result.Count > MaximumRecipientOutputs)
            throw new InvalidDataException(
                $"Direct SOLO supports at most {MaximumRecipientOutputs} positive reward-recipient outputs");

        return result
            .OrderBy(x => x.ScriptPubKey, StringComparer.Ordinal)
            .ThenBy(x => x.Address, StringComparer.Ordinal)
            .ToArray();
    }

    public static void EnsureMinerIsDistinct(
        BitcoinDirectCoinbaseTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if(template.Recipients?.Any(x => string.Equals(x.ScriptPubKey,
               template.MinerScriptPubKey,
               StringComparison.OrdinalIgnoreCase)) == true)
            throw new InvalidDataException(
                "The direct SOLO miner destination duplicates a configured reward-recipient payout script");
    }

    internal static long FloorPercentage(long satoshis, decimal percentage)
    {
        if(satoshis < 0)
            throw new ArgumentOutOfRangeException(nameof(satoshis));
        if(percentage < 0)
            throw new ArgumentOutOfRangeException(nameof(percentage));

        var bits = decimal.GetBits(percentage);
        var scale = (bits[3] >> 16) & 0x7f;
        var numerator = new BigInteger((uint) bits[0]) |
            new BigInteger((uint) bits[1]) << 32 |
            new BigInteger((uint) bits[2]) << 64;
        var denominator = BigInteger.Pow(10, scale) * 100;
        var amount = new BigInteger(satoshis) * numerator / denominator;

        if(amount > long.MaxValue)
            throw new OverflowException(
                "Direct SOLO percentage result exceeds Int64");

        return (long) amount;
    }
}
