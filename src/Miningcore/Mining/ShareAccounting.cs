using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Persistence.Model;
using Share = Miningcore.Blockchain.Share;

namespace Miningcore.Mining;

internal static class ShareAccounting
{
    internal static string CreateId() => Guid.NewGuid().ToString("N");

    internal static Guid ParseCanonicalId(string value)
    {
        if(value?.Length != 32 || value.Any(x =>
               !(x is >= '0' and <= '9' or >= 'a' and <= 'f')) ||
           !Guid.TryParseExact(value, "N", out var result) || result == Guid.Empty)
            throw new InvalidDataException(
                "Share accounting id must be a lowercase, non-zero UUID without separators");

        return result;
    }

    internal static Share[] ValidateAndFlatten(Share envelope,
        IReadOnlyDictionary<string, PoolConfig> pools)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(pools);

        if(envelope.BlockOnly)
        {
            if(envelope.PairedShare != null || !string.IsNullOrEmpty(envelope.AccountingId) ||
               envelope.AccountingRole != ShareAccountingRole.None)
                throw new InvalidDataException(
                    "Block-only records must not carry ordinary share-accounting data");

            return Array.Empty<Share>();
        }

        if(string.IsNullOrEmpty(envelope.AccountingId))
        {
            if(envelope.PairedShare != null ||
               envelope.AccountingRole != ShareAccountingRole.None ||
               envelope.RewardBasisSatoshis != 0)
                throw new InvalidDataException(
                    "Unidentified shares must not carry partial accounting data");

            return new[] { envelope };
        }

        ParseCanonicalId(envelope.AccountingId);
        ValidateProjection(envelope, pools);

        if(envelope.PairedShare == null)
        {
            if(envelope.AccountingRole != ShareAccountingRole.Single)
                throw new InvalidDataException(
                    "An unpaired accounting share must have the single-chain role");

            return new[] { envelope };
        }

        var auxiliary = envelope.PairedShare;
        if(envelope.AccountingRole != ShareAccountingRole.Parent ||
           auxiliary.AccountingRole != ShareAccountingRole.Auxiliary ||
           auxiliary.PairedShare != null)
            throw new InvalidDataException(
                "A paired accounting envelope must contain exactly one parent and one auxiliary projection");
        if(!string.Equals(envelope.AccountingId, auxiliary.AccountingId,
               StringComparison.Ordinal))
            throw new InvalidDataException(
                "Paired share projections have different accounting ids");
        if(string.Equals(envelope.PoolId, auxiliary.PoolId,
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Paired share projections must target different pools");

        ValidateProjection(auxiliary, pools);
        RequireSame(!string.Equals(envelope.Worker, auxiliary.Worker,
            StringComparison.Ordinal), "worker");
        RequireSame(!string.Equals(envelope.UserAgent, auxiliary.UserAgent,
            StringComparison.Ordinal), "user agent");
        RequireSame(!string.Equals(envelope.IpAddress, auxiliary.IpAddress,
            StringComparison.Ordinal), "source IP");
        RequireSame(!string.Equals(envelope.Source, auxiliary.Source,
            StringComparison.Ordinal), "cluster source");
        RequireSame(!string.Equals(envelope.SessionId, auxiliary.SessionId,
            StringComparison.Ordinal), "session id");
        RequireSame(envelope.Created != auxiliary.Created, "timestamp");
        RequireSame(envelope.ShareDifficulty != auxiliary.ShareDifficulty,
            "achieved miner difficulty");

        return new[] { envelope, auxiliary };
    }

    private static void RequireSame(bool mismatch, string field)
    {
        if(mismatch)
            throw new InvalidDataException(
                $"Paired share projections have different {field} values");
    }

    private static void ValidateProjection(Share share,
        IReadOnlyDictionary<string, PoolConfig> pools)
    {
        if(string.IsNullOrWhiteSpace(share.PoolId) ||
           !pools.ContainsKey(share.PoolId))
            throw new InvalidDataException(
                $"Accounting share targets unknown pool '{share.PoolId}'");
        if(string.IsNullOrWhiteSpace(share.Miner) ||
           string.IsNullOrWhiteSpace(share.SessionId) ||
           string.IsNullOrWhiteSpace(share.IpAddress) ||
           share.Created.Kind != DateTimeKind.Utc ||
           !double.IsFinite(share.Difficulty) || share.Difficulty <= 0 ||
           !double.IsFinite(share.ShareDifficulty) || share.ShareDifficulty <= 0 ||
           !double.IsFinite(share.ActualDifficulty) || share.ActualDifficulty <= 0 ||
           !double.IsFinite(share.NetworkDifficulty) || share.NetworkDifficulty <= 0 ||
           share.RewardBasisSatoshis <= 0)
            throw new InvalidDataException(
                $"Accounting share for pool '{share.PoolId}' is incomplete or non-finite");
    }

    internal static Persistence.Model.Share ToPersistenceShare(Share share)
    {
        Guid? id = string.IsNullOrEmpty(share.AccountingId)
            ? null
            : ParseCanonicalId(share.AccountingId);

        return new Persistence.Model.Share
        {
            PoolId = share.PoolId,
            BlockHeight = checked((ulong) share.BlockHeight),
            Miner = share.Miner,
            Worker = share.Worker,
            UserAgent = share.UserAgent,
            Difficulty = share.Difficulty,
            ShareDifficulty = share.ShareDifficulty,
            ActualDifficulty = share.ActualDifficulty,
            NetworkDifficulty = share.NetworkDifficulty,
            IpAddress = share.IpAddress,
            Source = share.Source,
            SessionId = share.SessionId,
            AccountingId = id,
            AccountingRole = id.HasValue ? checked((short) share.AccountingRole) : null,
            RewardBasisSatoshis = id.HasValue ? share.RewardBasisSatoshis : null,
            Created = share.Created,
        };
    }

    internal static PpsShareCredit CreatePpsCredit(PoolConfig pool, Share share)
    {
        if(pool.PaymentProcessing?.Enabled != true ||
           pool.PaymentProcessing.PayoutScheme != PayoutScheme.PPS)
            return null;
        if(pool.Template?.Family != CoinFamily.Bitcoin)
            throw new InvalidDataException(
                $"PPS accounting is currently restricted to the audited Bitcoin-family share contract ({pool.Id})");

        var recipients = pool.RewardRecipients ?? Array.Empty<RewardRecipient>();
        if(recipients.Any(x => x == null || x.Percentage < 0))
            throw new InvalidDataException(
                $"Pool '{pool.Id}' contains a null or negative reward-recipient percentage");

        decimal recipientPercent;
        try
        {
            recipientPercent = recipients.Where(x => x.Percentage > 0)
                .Sum(x => x.Percentage);
        }
        catch(OverflowException ex)
        {
            throw new InvalidDataException(
                $"Pool '{pool.Id}' reward-recipient percentages exceed the supported accounting range",
                ex);
        }

        if(recipientPercent >= 100)
            throw new InvalidDataException(
                $"Pool '{pool.Id}' must retain a positive reward fraction for PPS");

        var reward = share.RewardBasisSatoshis / 100_000_000m;
        var retainedReward = reward * (1m - recipientPercent / 100m);
        decimal difficulty;
        decimal networkDifficulty;

        try
        {
            difficulty = (decimal) share.Difficulty;
            networkDifficulty = (decimal) share.NetworkDifficulty;
        }
        catch(OverflowException ex)
        {
            throw new InvalidDataException(
                $"PPS difficulty for pool '{pool.Id}' exceeds decimal accounting range", ex);
        }

        var calculated = retainedReward * difficulty / networkDifficulty;
        calculated = decimal.Round(calculated, 24, MidpointRounding.ToZero);
        if(calculated <= 0)
            throw new InvalidDataException(
                $"PPS credit for pool '{pool.Id}' is below the supported 24-decimal liability precision");

        return new PpsShareCredit
        {
            PoolId = pool.Id,
            AccountingId = ParseCanonicalId(share.AccountingId),
            Address = share.Miner,
            CalculatedAmount = calculated,
            Difficulty = share.Difficulty,
            NetworkDifficulty = share.NetworkDifficulty,
            RewardBasisSatoshis = share.RewardBasisSatoshis,
            Created = share.Created,
        };
    }

    internal static string ComputePayloadHash(Guid accountingId,
        IReadOnlyList<Persistence.Model.Share> shares,
        IReadOnlyList<PpsShareCredit> credits)
    {
        var builder = new StringBuilder();
        builder.Append(accountingId.ToString("N")).Append('\n');

        foreach(var share in shares.OrderBy(x => x.PoolId, StringComparer.Ordinal))
        {
            builder.Append(share.PoolId).Append('|')
                .Append(share.BlockHeight).Append('|')
                .Append(share.Miner).Append('|').Append(share.Worker).Append('|')
                .Append(share.UserAgent).Append('|').Append(share.IpAddress).Append('|')
                .Append(share.Source).Append('|').Append(share.SessionId).Append('|')
                .Append(share.Difficulty.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(share.ShareDifficulty?.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(share.ActualDifficulty?.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(share.NetworkDifficulty.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(share.AccountingRole).Append('|').Append(share.RewardBasisSatoshis).Append('|')
                .Append(share.Created.ToUniversalTime().Ticks).Append('\n');
        }

        foreach(var credit in credits.OrderBy(x => x.PoolId, StringComparer.Ordinal))
        {
            builder.Append("pps|").Append(credit.PoolId).Append('|')
                .Append(credit.Address).Append('|')
                .Append(credit.CalculatedAmount.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
