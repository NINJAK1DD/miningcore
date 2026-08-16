namespace Miningcore.Extensions
{
    public static class DictionaryExtensions
    {
        public static void StripValue<T>(this IDictionary<string, T> dict, string key)
        {
            if(dict == null)
                return;

            // Extension-data dictionaries are case-sensitive, while Miningcore
            // configuration member matching is not. Remove every equivalent key
            // so case-variant duplicates cannot leave a credential in a public
            // projection. Ordinal comparison is deliberate: these are ASCII
            // configuration identifiers, not culture- or compatibility-folded
            // display strings, so Unicode lookalikes are not equivalent keys.
            var matchingKeys = dict.Keys
                .Where(candidate => string.Equals(candidate, key,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach(var matchingKey in matchingKeys)
                dict.Remove(matchingKey);
        }
    }
}
