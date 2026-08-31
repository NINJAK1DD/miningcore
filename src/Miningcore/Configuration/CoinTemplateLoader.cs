using Autofac;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Mining;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Miningcore.Configuration;

public static class CoinTemplateLoader
{
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private const string UnsupportedBlockSerializerProperty = "blockSerializer";
    private const string VersionRollingMaskProperty = "versionRollingMask";
    private const string VersionRollingConsensusMaskProperty =
        "versionRollingConsensusMask";
    private const string DisableVersionRollingProperty =
        "disableVersionRolling";
    private const string OdoCryptHasher = "odocrypt";
    private const string OdoCryptActivationProperty =
        "odoCryptActivationHeight";
    private const string OdoCryptIntervalProperty =
        "odoCryptShapeChangeInterval";

    private static void RejectUnsupportedMetadata(string filename, string coinId,
        JToken template)
    {
        var property = template.Children<JProperty>().FirstOrDefault(x =>
            string.Equals(x.Name, UnsupportedBlockSerializerProperty,
                StringComparison.OrdinalIgnoreCase));

        if(property != null)
        {
            throw new PoolStartupException(
                $"Invalid coin-template '{coinId}' in file '{filename}': property " +
                $"'{property.Name}' is unsupported and has no runtime effect. Remove it only " +
                "when the coin uses Miningcore's standard family serializer; a coin requiring " +
                "non-standard block serialization needs a typed Miningcore implementation.");
        }
    }

    private static void ValidateVersionRollingMaskSyntax(string filename,
        string coinId, JObject template, string propertyName)
    {
        var properties = template.Properties().Where(x =>
            string.Equals(x.Name, propertyName,
                StringComparison.OrdinalIgnoreCase)).ToArray();

        if(properties.Length == 0)
            return;

        if(properties.Length != 1)
        {
            throw VersionRollingError(filename, coinId,
                $"property '{propertyName}' has ambiguous case-variant duplicates");
        }

        var property = properties[0];

        if(property.Name != propertyName)
        {
            throw VersionRollingError(filename, coinId,
                $"property '{property.Name}' must use the exact casing " +
                $"'{propertyName}'");
        }

        if(property.Value.Type != JTokenType.String)
        {
            throw VersionRollingError(filename, coinId,
                $"property '{propertyName}' must be an eight-digit hexadecimal " +
                "string such as '0x1fffe000'");
        }

        var value = property.Value.Value<string>();

        if(value?.Length != 10 ||
           !value.StartsWith("0x", StringComparison.Ordinal) ||
           !uint.TryParse(value.AsSpan(2),
               System.Globalization.NumberStyles.AllowHexSpecifier,
               System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            throw VersionRollingError(filename, coinId,
                $"property '{propertyName}' must be an eight-digit hexadecimal " +
                "string such as '0x1fffe000'");
        }
    }

    private static void ValidateDisableVersionRollingSyntax(string filename,
        string coinId, JObject template)
    {
        var properties = template.Properties().Where(x =>
            string.Equals(x.Name, DisableVersionRollingProperty,
                StringComparison.OrdinalIgnoreCase)).ToArray();

        if(properties.Length == 0)
            return;

        if(properties.Length != 1)
        {
            throw VersionRollingError(filename, coinId,
                $"property '{DisableVersionRollingProperty}' has ambiguous " +
                "case-variant duplicates");
        }

        var property = properties[0];

        if(property.Name != DisableVersionRollingProperty)
        {
            throw VersionRollingError(filename, coinId,
                $"property '{property.Name}' must use the exact casing " +
                $"'{DisableVersionRollingProperty}'");
        }

        if(property.Value.Type != JTokenType.Boolean)
        {
            throw VersionRollingError(filename, coinId,
                $"property '{DisableVersionRollingProperty}' must be a JSON Boolean");
        }
    }

    private static void ValidateVersionRollingContract(string filename,
        string coinId, BitcoinTemplate template)
    {
        var allowedMask = template.AllowedVersionRollingMask;
        var consensusMask = template.VersionRollingConsensusMask;

        if(template.DisableVersionRolling && allowedMask.HasValue)
        {
            throw VersionRollingError(filename, coinId,
                $"'{VersionRollingMaskProperty}' cannot be combined with " +
                "'disableVersionRolling: true'");
        }

        if(allowedMask == 0)
        {
            throw VersionRollingError(filename, coinId,
                $"'{VersionRollingMaskProperty}' must be nonzero; use " +
                "'disableVersionRolling: true' to disable negotiation");
        }

        if(consensusMask == 0)
        {
            throw VersionRollingError(filename, coinId,
                $"'{VersionRollingConsensusMaskProperty}' must be nonzero when present");
        }

        if(allowedMask.HasValue &&
           (allowedMask.Value & ~BitcoinConstants.VersionRollingPoolMask) != 0)
        {
            throw VersionRollingError(filename, coinId,
                $"'{VersionRollingMaskProperty}' 0x{allowedMask.Value:x8} contains " +
                $"bits outside Miningcore's BIP310 pool mask " +
                $"0x{BitcoinConstants.VersionRollingPoolMask:x8}");
        }

        if(!template.DisableVersionRolling && consensusMask.HasValue &&
           !allowedMask.HasValue)
        {
            throw VersionRollingError(filename, coinId,
                $"'{VersionRollingConsensusMaskProperty}' requires an explicit " +
                $"'{VersionRollingMaskProperty}' or 'disableVersionRolling: true'");
        }

        if(allowedMask.HasValue && consensusMask.HasValue &&
           (allowedMask.Value & consensusMask.Value) != 0)
        {
            var overlap = allowedMask.Value & consensusMask.Value;

            throw VersionRollingError(filename, coinId,
                $"'{VersionRollingMaskProperty}' overlaps consensus-owned bits " +
                $"0x{overlap:x8}");
        }
    }

    private static void ValidateOdoCryptContract(string filename, string coinId,
        JObject template)
    {
        var headerHasher = template["headerHasher"] as JObject;
        var isOdoCrypt = string.Equals(headerHasher?["hash"]?.Value<string>(),
            OdoCryptHasher, StringComparison.Ordinal);
        var networks = template["networks"] as JObject;
        var contractProperties = new[]
        {
            OdoCryptActivationProperty,
            OdoCryptIntervalProperty,
        };
        var odoProperties = template.Descendants().OfType<JProperty>()
            .Where(x => contractProperties.Any(propertyName => string.Equals(
                x.Name, propertyName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if(!isOdoCrypt)
        {
            if(odoProperties.Length != 0)
            {
                throw new PoolStartupException(
                    $"Invalid coin-template '{coinId}' in file '{filename}': " +
                    "Odocrypt activation and schedule properties are valid only " +
                    "with the " +
                    $"'{OdoCryptHasher}' header hasher");
            }

            return;
        }

        if(networks == null)
        {
                throw new PoolStartupException(
                    $"Invalid coin-template '{coinId}' in file '{filename}': " +
                    $"'{OdoCryptHasher}' requires typed network activation " +
                    "and schedule parameters");
        }

        var requiredNetworks = new[] {"main", "test", "signet", "regtest"};

        foreach(var property in odoProperties)
        {
            var networkObject = property.Parent as JObject;
            var networkProperty = networkObject?.Parent as JProperty;
            var canonicalName = contractProperties.FirstOrDefault(propertyName =>
                string.Equals(property.Name, propertyName,
                    StringComparison.OrdinalIgnoreCase));

            if(property.Name != canonicalName ||
               networkProperty?.Parent != networks ||
               !requiredNetworks.Contains(networkProperty.Name,
                   StringComparer.Ordinal))
            {
                throw new PoolStartupException(
                    $"Invalid coin-template '{coinId}' in file '{filename}': " +
                    $"'{property.Name}' must be canonically cased and " +
                    "may appear only in the main, test, signet and regtest " +
                    "network objects");
            }
        }

        foreach(var requiredNetwork in requiredNetworks)
        {
            if(networks[requiredNetwork] is not JObject network)
            {
                throw new PoolStartupException(
                    $"Invalid coin-template '{coinId}' in file '{filename}': " +
                    $"'{OdoCryptHasher}' requires a '{requiredNetwork}' network object");
            }

            foreach(var propertyName in contractProperties)
            {
                var properties = network.Properties().Where(x => string.Equals(
                    x.Name, propertyName,
                    StringComparison.OrdinalIgnoreCase)).ToArray();

                if(properties.Length != 1 || properties[0].Name != propertyName)
                {
                    throw new PoolStartupException(
                        $"Invalid coin-template '{coinId}' in file '{filename}': " +
                        $"network '{requiredNetwork}' must contain exactly one " +
                        $"canonically cased '{propertyName}' property");
                }

                var value = properties[0].Value;

                if(value.Type != JTokenType.Integer ||
                   !ulong.TryParse(value.ToString(), out var parsed) ||
                   parsed == 0 || parsed > uint.MaxValue)
                {
                    var unit = propertyName == OdoCryptIntervalProperty
                        ? " number of seconds"
                        : string.Empty;

                    throw new PoolStartupException(
                        $"Invalid coin-template '{coinId}' in file '{filename}': " +
                        $"network '{requiredNetwork}' property " +
                        $"'{propertyName}' must be a nonzero unsigned 32-bit integer" +
                        unit);
                }
            }
        }
    }

    private static PoolStartupException VersionRollingError(string filename,
        string coinId, string message) => new(
        $"Invalid coin-template '{coinId}' in file '{filename}': {message}");

    private static IEnumerable<KeyValuePair<string, CoinTemplate>> LoadTemplates(string filename, IComponentContext ctx)
    {
        using var textReader = File.OpenText(filename);
        using var jreader = ConfigurationJson.CreateReader(textReader);

        JObject jo;

        try
        {
            jo = JObject.Load(jreader, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            });
        }
        catch(JsonReaderException ex)
        {
            throw new PoolStartupException(
                $"Invalid coin-template file '{filename}': {ex.Message}", ex);
        }

        foreach(var o in jo)
        {
            if(o.Value.Type != JTokenType.Object)
                throw new PoolStartupException("Invalid coin-template file: dictionary of coin-templates expected");

            var templateObject = (JObject) o.Value;
            RejectUnsupportedMetadata(filename, o.Key, templateObject);

            var value = o.Value[nameof(CoinTemplate.Family).ToLower()];
            if(value == null)
                throw new PoolStartupException($"Invalid coin-template '{o.Key}': missing 'family' property");

            var family = value.ToObject<CoinFamily>();
            var hasVersionRollingPolicy = templateObject.Properties().Any(x =>
                string.Equals(x.Name, VersionRollingMaskProperty,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Name, VersionRollingConsensusMaskProperty,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Name, DisableVersionRollingProperty,
                    StringComparison.OrdinalIgnoreCase));

            if(family != CoinFamily.Bitcoin && hasVersionRollingPolicy)
            {
                throw VersionRollingError(filename, o.Key,
                    "version-rolling policy is supported only by templates " +
                    "using the Bitcoin Stratum runtime");
            }

            ValidateVersionRollingMaskSyntax(filename, o.Key, templateObject,
                VersionRollingMaskProperty);
            ValidateVersionRollingMaskSyntax(filename, o.Key, templateObject,
                VersionRollingConsensusMaskProperty);
            ValidateDisableVersionRollingSyntax(filename, o.Key, templateObject);
            ValidateOdoCryptContract(filename, o.Key, templateObject);

            var result = (CoinTemplate) o.Value.ToObject(CoinTemplate.Families[family]);

            if(family == CoinFamily.Bitcoin)
            {
                if(result is not BitcoinTemplate bitcoinTemplate)
                {
                    throw VersionRollingError(filename, o.Key,
                        "Bitcoin Stratum templates require BitcoinTemplate metadata");
                }

                ValidateVersionRollingContract(filename, o.Key, bitcoinTemplate);
            }

            ctx.InjectProperties(result);

            // Patch explorer links
            if((result.ExplorerBlockLinks == null || result.ExplorerBlockLinks.Count == 0) &&
               !string.IsNullOrEmpty(result.ExplorerBlockLink))
            {
                result.ExplorerBlockLinks = new Dictionary<string, string>
                {
                    {"block", result.ExplorerBlockLink}
                };
            }

            // Record the source of the template
            result.Source = filename;

            yield return KeyValuePair.Create(o.Key, result);
        }
    }

    public static Dictionary<string, CoinTemplate> Load(IComponentContext ctx, string[] coinDefs)
    {
        var result = new Dictionary<string, CoinTemplate>();

        foreach(var filename in coinDefs)
        {
            var definitions = LoadTemplates(filename, ctx).ToArray();

            foreach(var definition in definitions)
            {
                var coinId = definition.Key;

                // log redefinitions
                if(result.ContainsKey(coinId))
                    logger.Warn($"Redefinition of coin '{coinId}' in file {filename}. First seen in {result[coinId].Source}");

                result[coinId] = definition.Value;
            }
        }

        return result;
    }
}
