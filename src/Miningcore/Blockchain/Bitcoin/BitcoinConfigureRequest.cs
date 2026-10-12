using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Bitcoin;

internal static class BitcoinConfigureRequest
{
    internal const int MaximumExtensions = 32;

    internal static bool TryParse(object parameters, out JArray extensions, out JObject values)
    {
        extensions = null;
        values = null;
        var token = parameters as JToken ?? (parameters == null ? null : JToken.FromObject(parameters));
        if(token is not JArray { Count: >= 2 } array || array[0] is not JArray requested ||
            requested.Count > MaximumExtensions || array[1] is not JObject options)
            return false;
        foreach(var extension in requested)
        {
            if(extension.Type != JTokenType.String)
                return false;
            var name = extension.Value<string>();
            if(string.IsNullOrEmpty(name) || name == BitcoinStratumExtensions.VersionRollingMask)
                return false;
        }
        extensions = requested;
        values = options;
        return true;
    }
}
