using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Miningcore.Extensions;

public static class SerializationExtensions
{
    private static readonly DefaultContractResolver extensionDataContractResolver = new()
    {
        NamingStrategy = new CamelCaseNamingStrategy()
    };

    private static readonly JsonSerializer serializer = new()
    {
        ContractResolver = extensionDataContractResolver
    };

    internal static IReadOnlyList<ExtensionDataPropertyContract>
        GetExtensionDataPropertyContracts(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // Runtime binding is deliberately best-effort because a malformed
        // optional extension must not fail a payout. Contract discovery is
        // strict because callers use it to define the public/private response
        // boundary and must never silently classify a non-object contract.
        if(extensionDataContractResolver.ResolveContract(type) is not
           JsonObjectContract contract)
        {
            throw new ArgumentException(
                $"Type '{type.FullName}' does not use a JSON object contract",
                nameof(type));
        }

        return contract.Properties
            .Where(property => !property.Ignored && property.Writable &&
                !string.IsNullOrEmpty(property.UnderlyingName) &&
                !string.IsNullOrEmpty(property.PropertyName))
            .Select(property => new ExtensionDataPropertyContract(
                property.UnderlyingName, property.PropertyName))
            .OrderBy(property => property.JsonName, StringComparer.Ordinal)
            .ToArray();
    }

    public static T SafeExtensionDataAs<T>(this IDictionary<string, object> extra, params string[] wrappers)
    {
        if(extra == null)
            return default;

        try
        {
            object o = extra;

            foreach (var key in wrappers)
            {
                if(o is IDictionary<string, object> dict)
                    o = dict[key];

                else if(o is JObject jo)
                    o = jo[key];

                else
                    throw new NotSupportedException("Unsupported child element type");
            }

            return JToken.FromObject(o).ToObject<T>(serializer);
        }

        catch
        {
            // ignored
        }

        return default;
    }
}

internal sealed record ExtensionDataPropertyContract(string ClrName,
    string JsonName);
