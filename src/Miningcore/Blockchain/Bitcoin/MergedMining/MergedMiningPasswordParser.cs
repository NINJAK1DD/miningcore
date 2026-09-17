namespace Miningcore.Blockchain.Bitcoin.MergedMining;

internal static class MergedMiningPasswordParser
{
    public static bool IsValidAddressParameter(string key)
    {
        return !string.IsNullOrWhiteSpace(key) &&
            !key.Equals("d", StringComparison.OrdinalIgnoreCase) &&
            key.IndexOfAny(new[] { ';', '=' }) == -1;
    }

    public static string GetValue(string password, string key)
    {
        if(string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(key))
            return null;

        foreach(var part in password.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if(separator <= 0)
                continue;

            var candidateKey = part[..separator].Trim();
            if(!candidateKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = part[(separator + 1)..].Trim();
            return value.Length > 0 ? value : null;
        }

        return null;
    }
}
