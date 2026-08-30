using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Persistence.Model;
using Share = Miningcore.Blockchain.Share;

namespace Miningcore.Mining;

internal static class ShareAccounting
{
    internal static readonly TimeSpan EvidencePruneSafetyMargin =
        TimeSpan.FromDays(1);
    // NUMERIC(38,24) reserves 24 fractional digits and therefore permits fewer
    // than 10^14 whole coins. Reject unsupported liabilities before admission.
    internal const decimal PpsCalculatedAmountExclusiveUpperBound =
        100_000_000_000_000m;

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
               envelope.RewardBasisSatoshis != 0 ||
               envelope.PpsCalculatedAmount.HasValue)
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

    internal static void ValidateReplayHorizon(Share envelope, DateTime nowUtc,
        int retentionDays)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if(string.IsNullOrEmpty(envelope.AccountingId))
            return;
        if(retentionDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));

        nowUtc = nowUtc.Kind == DateTimeKind.Utc
            ? nowUtc
            : nowUtc.ToUniversalTime();
        var oldest = nowUtc.AddDays(-retentionDays);
        var newest = nowUtc.AddMinutes(5);
        if(envelope.Created < oldest)
            throw new InvalidDataException(
                $"Share accounting evidence is older than the configured {retentionDays}-day replay horizon; preserve it for manual financial reconciliation");
        if(envelope.Created > newest)
            throw new InvalidDataException(
                "Share accounting evidence is more than five minutes in the future");
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
           share.RewardBasisSatoshis <= 0 ||
           share.PpsCalculatedAmount is <= 0)
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

    internal static void AttachPpsCreditEvidence(PoolConfig pool, Share share)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(share);

        if(pool.PaymentProcessing?.Enabled != true ||
           pool.PaymentProcessing.PayoutScheme != PayoutScheme.PPS)
        {
            share.PpsCalculatedAmount = null;
            return;
        }

        share.PpsCalculatedAmount = CalculatePpsAmount(pool, share);
    }

    internal static PpsShareCredit CreatePpsCredit(PoolConfig pool, Share share,
        bool allowMissingPpsConfiguration = false)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(share);

        var hasPaymentConfiguration = pool.PaymentProcessing != null;
        var configuredPps = pool.PaymentProcessing?.PayoutScheme ==
            PayoutScheme.PPS;

        if(!share.PpsCalculatedAmount.HasValue)
        {
            if(configuredPps)
                throw new InvalidDataException(
                    $"PPS share for pool '{pool.Id}' is missing immutable liability evidence");

            return null;
        }

        if(hasPaymentConfiguration && !configuredPps)
            throw new InvalidDataException(
                $"Non-PPS pool '{pool.Id}' received unexpected PPS liability evidence");
        if(!hasPaymentConfiguration && !allowMissingPpsConfiguration)
            throw new InvalidDataException(
                $"Pool '{pool.Id}' received PPS liability evidence without an explicit PPS payout contract");
        var sanitizedRecovery = allowMissingPpsConfiguration && !hasPaymentConfiguration;
        ValidatePpsTemplateContract(pool, sanitizedRecovery);

        var calculated = share.PpsCalculatedAmount.Value;
        ValidatePpsCalculatedAmount(pool.Id, calculated, true);
        if(hasPaymentConfiguration)
        {
            if(calculated != CalculatePpsAmount(pool, share))
                throw new InvalidDataException(
                    $"PPS liability evidence for pool '{pool.Id}' conflicts with its accepting configuration");
        }
        else if(!RecoveryMaximumCoversCalculated(pool, share, calculated,
            sanitizedRecovery))
            throw new InvalidDataException(
                $"Recovery PPS liability evidence for pool '{pool.Id}' exceeds the maximum independently derived from its immutable share inputs");

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

    private static decimal CalculatePpsAmount(PoolConfig pool, Share share)
    {
        ValidatePpsTemplateContract(pool, false);

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

        decimal retainedReward;
        decimal calculated;

        try
        {
            var reward = share.RewardBasisSatoshis / 100_000_000m;
            retainedReward = reward * (1m - recipientPercent / 100m);
            var difficulty = (decimal) share.Difficulty;
            var networkDifficulty = (decimal) share.NetworkDifficulty;
            calculated = retainedReward * difficulty / networkDifficulty;
        }
        catch(OverflowException ex)
        {
            throw new InvalidDataException(
                $"PPS liability for pool '{pool.Id}' exceeds the supported decimal accounting range", ex);
        }

        calculated = decimal.Round(calculated, 24, MidpointRounding.ToZero);
        ValidatePpsCalculatedAmount(pool.Id, calculated, true);
        return calculated;
    }

    private static bool RecoveryMaximumCoversCalculated(PoolConfig pool,
        Share share, decimal calculated, bool allowUnavailableTemplate)
    {
        ValidatePpsTemplateContract(pool, allowUnavailableTemplate);

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
                $"Recovery PPS liability inputs for pool '{pool.Id}' exceed the supported decimal accounting range",
                ex);
        }

        var reward = share.RewardBasisSatoshis / 100_000_000m;
        if(reward <= 0 || difficulty <= 0 || networkDifficulty <= 0)
            throw new InvalidDataException(
                $"Recovery PPS liability inputs for pool '{pool.Id}' must be positive");

        // Compare calculated * networkDifficulty <= reward * difficulty as exact
        // decimal rationals. This avoids overflowing merely because the sanitized
        // zero-fee ceiling is above NUMERIC(38,24); the embedded liability itself
        // was range-checked before this comparison.
        var (calculatedValue, calculatedScale) = GetDecimalParts(calculated);
        var (networkValue, networkScale) = GetDecimalParts(networkDifficulty);
        var (rewardValue, rewardScale) = GetDecimalParts(reward);
        var (difficultyValue, difficultyScale) = GetDecimalParts(difficulty);
        var left = calculatedValue * networkValue;
        var right = rewardValue * difficultyValue;
        var leftScale = calculatedScale + networkScale;
        var rightScale = rewardScale + difficultyScale;
        if(leftScale < rightScale)
            left *= BigInteger.Pow(10, rightScale - leftScale);
        else if(rightScale < leftScale)
            right *= BigInteger.Pow(10, leftScale - rightScale);

        return left <= right;
    }

    private static void ValidatePpsTemplateContract(PoolConfig pool,
        bool allowUnavailableTemplate)
    {
        // Recovery may continue after optional template enrichment fails. A present template
        // remains authoritative, and live/configured PPS admission always requires Bitcoin.
        if(pool.Template == null && allowUnavailableTemplate)
            return;

        if(pool.Template?.Family != CoinFamily.Bitcoin)
            throw new InvalidDataException(
                $"PPS accounting is currently restricted to the audited Bitcoin-family share contract ({pool.Id})");
    }

    private static (BigInteger Value, int Scale) GetDecimalParts(decimal value)
    {
        var bits = decimal.GetBits(value);
        var magnitude = new BigInteger((uint) bits[0]);
        magnitude |= new BigInteger((uint) bits[1]) << 32;
        magnitude |= new BigInteger((uint) bits[2]) << 64;
        var scale = (bits[3] >> 16) & 0x7F;
        if((bits[3] & int.MinValue) != 0)
            magnitude = -magnitude;
        return (magnitude, scale);
    }

    private static void ValidatePpsCalculatedAmount(string poolId,
        decimal calculated, bool enforceStorageRange)
    {
        if(calculated <= 0)
            throw new InvalidDataException(
                $"PPS credit for pool '{poolId}' is below the supported 24-decimal liability precision");
        if(decimal.Round(calculated, 24, MidpointRounding.ToZero) != calculated)
            throw new InvalidDataException(
                $"PPS liability for pool '{poolId}' exceeds the supported 24-decimal liability precision");
        if(enforceStorageRange &&
           calculated >= PpsCalculatedAmountExclusiveUpperBound)
            throw new InvalidDataException(
                $"PPS liability for pool '{poolId}' exceeds the PostgreSQL NUMERIC(38,24) storage range");
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
                .Append(credit.CalculatedAmount.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(credit.Difficulty.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(credit.NetworkDifficulty.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(credit.RewardBasisSatoshis).Append('|')
                .Append(credit.Created.ToUniversalTime().Ticks)
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
