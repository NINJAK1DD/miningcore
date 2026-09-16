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
        JsonContract resolvedContract;

        try
        {
            resolvedContract = extensionDataContractResolver.ResolveContract(
                type);
        }
        catch(JsonSerializationException ex)
        {
            throw new InvalidOperationException(
                $"Type '{type.FullName}' does not expose an unambiguous " +
                "JSON contract supported by payment extension discovery",
                ex);
        }

        if(resolvedContract is not JsonObjectContract contract)
        {
            throw UnsupportedExtensionDataContract(type,
                "a non-object JSON contract");
        }

        ValidateExtensionDataContract(type, contract);

        return contract.Properties
            .Where(property => !property.Ignored && property.Writable &&
                !string.IsNullOrEmpty(property.UnderlyingName) &&
                !string.IsNullOrEmpty(property.PropertyName))
            .Select(property => new ExtensionDataPropertyContract(
                property.UnderlyingName, property.PropertyName))
            .OrderBy(property => property.JsonName, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateExtensionDataContract(Type type,
        JsonObjectContract contract)
    {
        if(contract.Converter != null)
        {
            throw UnsupportedExtensionDataContract(type,
                "a type-level JSON converter");
        }

        if(contract.ExtensionDataGetter != null ||
           contract.ExtensionDataSetter != null)
        {
            throw UnsupportedExtensionDataContract(type,
                "JSON extension-data capture");
        }

        if(contract.CreatorParameters.Count != 0)
        {
            throw UnsupportedExtensionDataContract(type,
                "constructor-based JSON deserialization");
        }

        if(contract.MemberSerialization == MemberSerialization.Fields)
        {
            throw UnsupportedExtensionDataContract(type,
                "field-based JSON member serialization");
        }

        var activeProperties = contract.Properties
            .Where(property => !property.Ignored)
            .ToArray();
        var convertedProperty = activeProperties.FirstOrDefault(property =>
            property.Converter != null || property.ItemConverter != null);
        if(convertedProperty != null)
        {
            throw UnsupportedExtensionDataContract(type,
                $"property-level JSON converter on " +
                $"'{convertedProperty.PropertyName}'");
        }

        var populatedGetter = activeProperties.FirstOrDefault(property =>
            property.Readable && !property.Writable &&
            IsPotentiallyPopulatedCollection(property.PropertyType));
        if(populatedGetter != null)
        {
            throw UnsupportedExtensionDataContract(type,
                $"getter-only collection property " +
                $"'{populatedGetter.PropertyName}'");
        }

        var collision = activeProperties
            .Where(property => property.Writable &&
                !string.IsNullOrEmpty(property.PropertyName))
            .GroupBy(property => property.PropertyName,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if(collision != null)
        {
            throw UnsupportedExtensionDataContract(type,
                $"case-insensitive JSON property collision " +
                $"'{collision.Key}'");
        }
    }

    private static bool IsPotentiallyPopulatedCollection(Type type) =>
        type != null && type != typeof(string) && !type.IsArray &&
        typeof(System.Collections.IEnumerable).IsAssignableFrom(type);

    private static InvalidOperationException UnsupportedExtensionDataContract(
        Type type, string feature) => new(
        $"Type '{type.FullName}' uses {feature}, which payment extension " +
        "contract discovery does not support");

    /// <summary>
    /// Attempts to bind extension data while preserving the original error.
    /// A null source is a successful no-op with a default result value.
    /// </summary>
    public static bool TryExtensionDataAs<T>(
        this IDictionary<string, object> extra, out T value,
        out Exception error, params string[] wrappers)
    {
        value = default;
        error = null;

        if(extra == null)
            return true;

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

            value = JToken.FromObject(o).ToObject<T>(serializer);
            return true;
        }

        catch(Exception ex)
        {
            error = ex;
            return false;
        }
    }

    public static T SafeExtensionDataAs<T>(this IDictionary<string, object> extra, params string[] wrappers)
    {
        TryExtensionDataAs(extra, out T value, out _, wrappers);

        return value;
    }
}

internal sealed record ExtensionDataPropertyContract(string ClrName,
    string JsonName);
