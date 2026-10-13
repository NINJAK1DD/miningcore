using Newtonsoft.Json.Linq;
using System.Runtime.CompilerServices;
using Miningcore.JsonRpc;

namespace Miningcore.Blockchain.Bitcoin;

internal static class BitcoinConfigureRequest
{
    internal const int MaximumExtensions = 32;
    private static readonly ConditionalWeakTable<JsonRpcRequest, Envelope> parsed = new();
    internal sealed record Envelope(bool IsValid, bool RequestsMinimum, JArray Extensions, JObject Options);

    internal static Envelope Parse(JsonRpcRequest request) => parsed.GetValue(request, static value =>
    {
        var token = value.Params as JToken ?? (value.Params == null ? null : JToken.FromObject(value.Params));
        var valid = TryParse(token, out var extensions, out var options);
        // Admission must inspect the extension list even if the options or names
        // are invalid. Invalid envelopes must never be echoed as result maps.
        var minimum = token is JArray array && array.First is JArray names &&
            names.Any(x => x.Type == JTokenType.String && x.Value<string>() == BitcoinStratumExtensions.MinimumDiff);
        return new(valid, minimum, extensions, options);
    });

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
