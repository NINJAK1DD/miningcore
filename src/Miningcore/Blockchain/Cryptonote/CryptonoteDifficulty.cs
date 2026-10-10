using Miningcore.Configuration;

namespace Miningcore.Blockchain.Cryptonote;

// Shared by Conceal, CryptoNote and Zano: assigned targets and credited work must
// use the same representable range. Never saturate only the wire target.
internal static class CryptonoteDifficulty
{
    internal const double Minimum = 1d;
    // BitDecrement avoids double rounding to 2^63 during the *255 conversion.
    internal static readonly double Maximum = Math.BitDecrement(9223372036854775808d / 255d);

    internal static double Validate(double difficulty)
    {
        if(!double.IsFinite(difficulty) || difficulty < Minimum || difficulty > Maximum)
            throw new ArgumentOutOfRangeException(nameof(difficulty), "Difficulty is outside the representable target range");
        return difficulty;
    }

    internal static void ValidatePool(PoolConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach(var port in config.Ports?.Values ?? Enumerable.Empty<PoolEndpoint>())
        {
            Validate(port.Difficulty);
            if(port.VarDiff != null)
            {
                Validate(port.VarDiff.MinDiff);
                if(port.VarDiff.MaxDiff.HasValue)
                    Validate(port.VarDiff.MaxDiff.Value);
            }
        }
    }

    internal static void ValidateRequest(double difficulty)
    {
        try { Validate(difficulty); }
        catch(ArgumentOutOfRangeException)
        {
            throw new Stratum.StratumException(Stratum.StratumError.MinusOne,
                "Difficulty is outside the representable target range");
        }
    }

    internal static double NormalizeShareCredit(double difficulty, double multiplier)
    {
        Validate(difficulty);
        if(!double.IsFinite(multiplier) || multiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        var credit = difficulty / multiplier;
        if(!double.IsFinite(credit) || credit <= 0)
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        return credit;
    }
}
