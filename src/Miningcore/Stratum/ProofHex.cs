namespace Miningcore.Stratum;

// Miner proof fields must have a unique textual representation modulo hex case.
// The legacy general-purpose decoder is permissive and is not a wire validator.
internal static class ProofHex
{
    internal static bool IsHex(string value)
    {
        if(string.IsNullOrEmpty(value)) return false;
        foreach(var c in value)
            if(c is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
                return false;
        return true;
    }

    internal static bool IsExact(string value, int characters) =>
        value != null && value.Length == characters && IsHex(value);

    internal static void Require(string value, int characters, string field)
    {
        if(value == null || value.Length != characters)
            throw new StratumException(StratumError.Other, $"incorrect size of {field}");
        if(!IsHex(value))
            throw new StratumException(StratumError.Other, $"invalid {field}");
    }
}
