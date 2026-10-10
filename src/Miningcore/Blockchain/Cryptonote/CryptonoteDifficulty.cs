using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Rpc;
using NLog;

namespace Miningcore.Blockchain.Cryptonote;

// Shared by Conceal, CryptoNote and Zano: assigned targets and credited work must
// use the same representable range. Never saturate only the wire target.
internal static class CryptonoteDifficulty
{
    internal const double Minimum = 1d;
    // floor(2^32 / D) loses one target unit. Retaining at least 101 units
    // bounds miner work / nominal credit below 1 + 1/101, hence below 1%.
    // Keep the legacy four-byte wire format and nominal accounting units.
    internal const uint ShortTargetMinimum = 101;
    internal static readonly double ShortTargetMaximum = Math.Floor(Math.BitDecrement(4294967296d / ShortTargetMinimum));
    // Full-width targets also need headroom for the signed *255 conversion.
    internal static readonly double FullTargetMaximum = Math.BitDecrement(9223372036854775808d / 255d);

    internal static bool IsRepresentable(double difficulty, double maximum) =>
        double.IsFinite(difficulty) && difficulty >= Minimum && difficulty <= maximum;

    internal static double Validate(double difficulty, double maximum)
    {
        if(!IsRepresentable(difficulty, maximum))
            throw new ArgumentOutOfRangeException(nameof(difficulty), "Difficulty is outside the representable target range");
        return difficulty;
    }

    internal static double NormalizeShortAssignment(double difficulty) =>
        Math.Floor(Validate(difficulty, ShortTargetMaximum));

    internal static double ValidateShortAssignment(double difficulty)
    {
        Validate(difficulty, ShortTargetMaximum);
        if(difficulty != Math.Floor(difficulty))
            throw new ArgumentOutOfRangeException(nameof(difficulty), "Short-target assignments require whole-number difficulty");
        return difficulty;
    }

    internal static void ValidatePool(PoolConfig config, double maximum)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach(var port in config.Ports?.Values ?? Enumerable.Empty<PoolEndpoint>())
        {
            Validate(port.Difficulty, maximum);
            if(port.VarDiff != null)
            {
                Validate(port.VarDiff.MinDiff, maximum);
                if(port.VarDiff.MaxDiff.HasValue)
                    Validate(port.VarDiff.MaxDiff.Value, maximum);
                if(maximum == ShortTargetMaximum)
                {
                    if(port.VarDiff.MaxDelta is double delta &&
                       (!double.IsFinite(delta) || delta < 0 || delta is > 0 and < 1))
                        throw new ArgumentOutOfRangeException(nameof(config), "Short-target VarDiff maxDelta must be finite and either zero (unlimited) or at least 1");
                    var minimum = Math.Ceiling(port.VarDiff.MinDiff);
                    var upper = Math.Floor(port.VarDiff.MaxDiff ?? maximum);
                    if(minimum > upper)
                        throw new ArgumentOutOfRangeException(nameof(config), "VarDiff bounds contain no whole-number assignment");
                    var starting = NormalizeShortAssignment(port.Difficulty);
                    if(starting < minimum || starting > upper)
                        throw new ArgumentOutOfRangeException(nameof(config), "Normalized endpoint difficulty must lie within the whole-number VarDiff bounds");
                }
            }
        }
    }

    // Caller holds the worker assignment gate. Preserve the existing eligibility
    // rule: ignored hints must not turn a valid login into a failure.
    internal static bool TryApplyStaticHint(WorkerContextBase context, double difficulty, ILogger logger)
    {
        if(!(context.VarDiff != null ? difficulty >= context.VarDiff.Config.MinDiff : difficulty > context.Difficulty))
            return false;
        if(!IsRepresentable(difficulty, context.MaximumDifficulty))
        {
            RpcConsumerDiagnostics.Write(logger, LogLevel.Warn, "CryptonoteDifficulty.StaticHintIgnored");
            return false;
        }
        var assignment = context.RequiresIntegerDifficulty ? Math.Floor(difficulty) : difficulty;
        if(!(context.VarDiff != null ? assignment >= context.VarDiff.Config.MinDiff : assignment > context.Difficulty))
            return false;
        context.SetDifficulty(assignment);
        context.VarDiff = null;
        return true;
    }

    internal static double NormalizeShareCredit(double difficulty, double multiplier)
    {
        Validate(difficulty, FullTargetMaximum);
        if(!double.IsFinite(multiplier) || multiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        var credit = difficulty / multiplier;
        if(!double.IsFinite(credit) || credit <= 0)
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        return credit;
    }
}
