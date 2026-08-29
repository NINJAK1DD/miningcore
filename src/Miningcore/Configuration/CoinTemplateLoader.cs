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

        var value = property.Value.Value<string>();

        if(property.Value.Type != JTokenType.String || value?.Length != 10 ||
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

    private static void ValidateVersionRollingContract(string filename,
        string coinId, BitcoinTemplate template)
    {
        var allowedMask = template.VersionRollingMask;
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

            RejectUnsupportedMetadata(filename, o.Key, o.Value);

            var templateObject = (JObject) o.Value;
            ValidateVersionRollingMaskSyntax(filename, o.Key, templateObject,
                VersionRollingMaskProperty);
            ValidateVersionRollingMaskSyntax(filename, o.Key, templateObject,
                VersionRollingConsensusMaskProperty);

            var value = o.Value[nameof(CoinTemplate.Family).ToLower()];
            if(value == null)
                throw new PoolStartupException($"Invalid coin-template '{o.Key}': missing 'family' property");

            var family = value.ToObject<CoinFamily>();
            var result = (CoinTemplate) o.Value.ToObject(CoinTemplate.Families[family]);

            if(result is BitcoinTemplate bitcoinTemplate)
                ValidateVersionRollingContract(filename, o.Key, bitcoinTemplate);
            else if(templateObject.Properties().Any(x =>
                        string.Equals(x.Name, VersionRollingMaskProperty,
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Name, VersionRollingConsensusMaskProperty,
                            StringComparison.OrdinalIgnoreCase)))
            {
                throw VersionRollingError(filename, o.Key,
                    "version-rolling masks are supported only by Bitcoin-family templates");
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
