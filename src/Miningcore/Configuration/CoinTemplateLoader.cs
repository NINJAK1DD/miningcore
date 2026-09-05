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
    private const string HeaderHasherProperty = "headerHasher";
    private const string HasherNameProperty = "hash";
    private const string NetworksProperty = "networks";
    private const string OdoCryptActivationProperty =
        "odoCryptActivationHeight";
    private const string OdoCryptIntervalProperty =
        "odoCryptShapeChangeInterval";
    internal const string BitcoinBlake2bProtocol =
        "knots-29.4.1-header-v2";

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

    private static JProperty GetSingleProperty(JObject parent,
        string propertyName, string filename, string coinId, bool required)
    {
        var properties = parent.Properties().Where(x => string.Equals(x.Name,
            propertyName, StringComparison.OrdinalIgnoreCase)).ToArray();

        if(properties.Length == 0)
        {
            if(required)
            {
                throw new PoolStartupException(
                    $"Invalid coin-template '{coinId}' in file '{filename}': " +
                    $"missing required property '{propertyName}'");
            }

            return null;
        }

        if(properties.Length != 1)
        {
            throw new PoolStartupException(
                $"Invalid coin-template '{coinId}' in file '{filename}': " +
                $"property '{propertyName}' has ambiguous case-variant duplicates");
        }

        if(properties[0].Name != propertyName)
        {
            throw new PoolStartupException(
                $"Invalid coin-template '{coinId}' in file '{filename}': " +
                $"property '{properties[0].Name}' must use the exact casing " +
                $"'{propertyName}'");
        }

        return properties[0];
    }

    private static void ValidateOdoCryptContract(string filename, string coinId,
        CoinFamily family, JObject template)
    {
        var headerHasherProperties = template.Properties().Where(x =>
            string.Equals(x.Name, HeaderHasherProperty,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        var isOdoCrypt = headerHasherProperties
            .Select(x => x.Value as JObject)
            .Where(x => x != null)
            .SelectMany(x => x.Properties())
            .Where(x => string.Equals(x.Name, HasherNameProperty,
                StringComparison.OrdinalIgnoreCase))
            .Any(x => x.Value.Type == JTokenType.String && string.Equals(
                x.Value.Value<string>(), OdoCryptHasher,
                StringComparison.OrdinalIgnoreCase));
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

        if(family != CoinFamily.Bitcoin)
        {
            throw new PoolStartupException(
                $"Invalid coin-template '{coinId}' in file '{filename}': " +
                "Odocrypt is supported only by templates using the Bitcoin " +
                "Stratum runtime");
        }

        var canonicalHeaderHasher = GetSingleProperty(template,
            HeaderHasherProperty, filename, coinId, true);

        if(canonicalHeaderHasher.Value is not JObject canonicalHasherObject)
        {
            throw new PoolStartupException(
                $"Invalid coin-template '{coinId}' in file '{filename}': " +
                $"property '{HeaderHasherProperty}' must be a JSON object");
        }

        var canonicalHash = GetSingleProperty(canonicalHasherObject,
            HasherNameProperty, filename, coinId, true);

        if(canonicalHash.Value.Type != JTokenType.String ||
           !string.Equals(canonicalHash.Value.Value<string>(), OdoCryptHasher,
               StringComparison.OrdinalIgnoreCase))
        {
            throw new PoolStartupException(
                $"Invalid coin-template '{coinId}' in file '{filename}': " +
                $"property '{HeaderHasherProperty}.{HasherNameProperty}' must " +
                $"name the '{OdoCryptHasher}' hasher");
        }

        var networksProperty = GetSingleProperty(template, NetworksProperty,
            filename, coinId, true);
        var networks = networksProperty.Value as JObject;

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

                if(value.Type != JTokenType.Integer)
                {
                    throw new PoolStartupException(
                        $"Invalid coin-template '{coinId}' in file '{filename}': " +
                        $"network '{requiredNetwork}' property " +
                        $"'{propertyName}' must be a JSON integer");
                }

                if(!ulong.TryParse(value.ToString(), out var parsed) ||
                   parsed > uint.MaxValue)
                {
                    throw new PoolStartupException(
                        $"Invalid coin-template '{coinId}' in file '{filename}': " +
                        $"network '{requiredNetwork}' property " +
                        $"'{propertyName}' must be an unsigned 32-bit integer");
                }

                if(parsed == 0)
                {
                    throw new PoolStartupException(
                        $"Invalid coin-template '{coinId}' in file '{filename}': " +
                        $"network '{requiredNetwork}' property " +
                        $"'{propertyName}' must be nonzero");
                }
            }
        }
    }

    private static PoolStartupException VersionRollingError(string filename,
        string coinId, string message) => new(
        $"Invalid coin-template '{coinId}' in file '{filename}': {message}");

    private static void ValidateBitcoinBlake2bSyntax(string filename,
        string coinId, CoinFamily family, JObject template)
    {
        void Reject(string reason) => throw new PoolStartupException(
            $"Invalid coin-template '{coinId}' in file '{filename}': {reason}");

        if(family != CoinFamily.BitcoinBlake2b)
        {
            if(template.Descendants().OfType<JProperty>().Any(x =>
                   x.Name.StartsWith("blake2b", StringComparison.OrdinalIgnoreCase)))
                Reject("blake2b metadata is supported only by the bitcoin-blake2b family");
            return;
        }

        // Header-v2 is not an arbitrary Bitcoin-template flag combination.
        // Restrict it to the fields exercised by this typed runtime, before
        // extension binding can silently discard casing or scope mistakes.
        var scalarNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "canonicalName", "symbol", "family", "website", "github",
            "market", "twitter", "telegram", "discord", "explorerBlockLink",
            "explorerTxLink", "explorerAccountLink", "blake2bProtocol",
        };
        foreach(var property in template.Properties())
        {
            if(scalarNames.Contains(property.Name))
            {
                if(property.Value.Type != JTokenType.String)
                    Reject($"'{property.Name}' must be a JSON string");
                continue;
            }
            if(property.Name is not ("disableVersionRolling" or "networks" or
                "coinbaseHasher" or "headerHasher" or "blockHasher"))
                Reject($"unsupported or non-canonical Bitcoin BLAKE2b property '{property.Name}'");
        }

        var sha256d = JObject.Parse("{\"hash\":\"sha256d\"}");
        var reverseSha256d = JObject.Parse("{\"hash\":\"reverse\",\"args\":[{\"hash\":\"sha256d\"}]}");
        if(!JToken.DeepEquals(template["coinbaseHasher"], sha256d) ||
           !JToken.DeepEquals(template["headerHasher"], sha256d) ||
           !JToken.DeepEquals(template["blockHasher"], reverseSha256d))
            Reject("Bitcoin BLAKE2b requires the reviewed base-serializer hash descriptors; the dedicated header-v2 job owns PoW and block identity");

        if(template["networks"] is not JObject networks)
        {
            Reject("'networks' must be an object");
            return;
        }
        foreach(var entry in networks.Properties())
        {
            if(entry.Name is not ("main" or "regtest") || entry.Value is not JObject)
                Reject($"unsupported network '{entry.Name}'; only main and regtest are reviewed");
            foreach(var field in ((JObject) entry.Value).Properties())
            {
                if(field.Name == "blake2bActivationHeadline")
                {
                    if(field.Value.Type != JTokenType.String ||
                       field.Value.Value<string>().Any(x => x is < ' ' or > '~'))
                        Reject($"'{field.Name}' must be printable ASCII");
                }
                else if(field.Name is "blake2bActivationHeight" or "blake2bTargetShift")
                {
                    if(field.Value.Type != JTokenType.Integer ||
                       !ulong.TryParse(field.Value.ToString(), out var number) ||
                       number == 0 || number > (field.Name == "blake2bTargetShift" ? 255UL : uint.MaxValue))
                        Reject($"'{field.Name}' must be a positive in-range JSON integer");
                }
                else
                    Reject($"unsupported or non-canonical network property '{field.Name}'");
            }
        }
    }

    private static void ValidateBitcoinBlake2bContract(string filename,
        string coinId, BitcoinBlake2bTemplate template)
    {
        if(!string.Equals(template.Blake2bProtocol, BitcoinBlake2bProtocol,
               StringComparison.Ordinal))
        {
            throw new PoolStartupException(
                $"Invalid coin-template '{coinId}' in file '{filename}': " +
                $"'blake2bProtocol' must be '{BitcoinBlake2bProtocol}'");
        }

        if(!template.DisableVersionRolling ||
           template.AllowedVersionRollingMask.HasValue ||
           template.VersionRollingConsensusMask.HasValue)
        {
            throw new PoolStartupException(
                $"Invalid coin-template '{coinId}' in file '{filename}': " +
                "Bitcoin BLAKE2b header-v2 requires 'disableVersionRolling: " +
                "true' and does not accept BIP310 mask metadata");
        }

        if(template.Networks == null ||
           !template.Networks.TryGetValue("main", out var main) ||
           !template.Networks.TryGetValue("regtest", out var regtest))
        {
            throw new PoolStartupException(
                $"Invalid coin-template '{coinId}' in file '{filename}': " +
                "Bitcoin BLAKE2b requires typed 'main' and 'regtest' network metadata");
        }

        ValidateBitcoinBlake2bNetwork(filename, coinId, "main", main,
            requireHeadline: true);
        ValidateBitcoinBlake2bNetwork(filename, coinId, "regtest", regtest,
            requireHeadline: false);

        if(regtest.Blake2bTargetShift != 20)
            throw new PoolStartupException($"Invalid coin-template '{coinId}' in file '{filename}': regtest target shift must match the reviewed value 20");

        if(main.Blake2bActivationHeight != 961640 ||
           main.Blake2bTargetShift != 22 ||
           !string.Equals(main.Blake2bActivationHeadline,
               "8-30 NYPost Deride And Conquer", StringComparison.Ordinal))
        {
            throw new PoolStartupException(
                $"Invalid coin-template '{coinId}' in file '{filename}': " +
                "mainnet activation metadata does not match reviewed Bitcoin " +
                "Knots v29.4.1.knots20260508 consensus parameters");
        }
    }

    private static void ValidateBitcoinBlake2bNetwork(string filename,
        string coinId, string networkName,
        BitcoinTemplate.BitcoinNetworkParams network,
        bool requireHeadline)
    {
        if(network?.Blake2bActivationHeight is not > 0 ||
           network.Blake2bTargetShift is not > 0)
        {
            throw new PoolStartupException(
                $"Invalid coin-template '{coinId}' in file '{filename}': " +
                $"network '{networkName}' requires a positive " +
                "'blake2bActivationHeight' and a target shift from 1 through 255");
        }

        if(requireHeadline &&
           string.IsNullOrEmpty(network.Blake2bActivationHeadline))
        {
            throw new PoolStartupException(
                $"Invalid coin-template '{coinId}' in file '{filename}': " +
                $"network '{networkName}' requires " +
                "'blake2bActivationHeadline'");
        }

        if(network.Blake2bActivationHeadline?.Length > 80)
        {
            throw new PoolStartupException(
                $"Invalid coin-template '{coinId}' in file '{filename}': " +
                $"network '{networkName}' activation headline exceeds 80 bytes");
        }
    }

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

            if(family != CoinFamily.Bitcoin &&
               family != CoinFamily.BitcoinBlake2b &&
               hasVersionRollingPolicy)
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
            ValidateOdoCryptContract(filename, o.Key, family, templateObject);
            ValidateBitcoinBlake2bSyntax(filename, o.Key, family, templateObject);

            var result = (CoinTemplate) o.Value.ToObject(CoinTemplate.Families[family]);

            if(family is CoinFamily.Bitcoin or CoinFamily.BitcoinBlake2b)
            {
                if(result is not BitcoinTemplate bitcoinTemplate)
                {
                    throw VersionRollingError(filename, o.Key,
                        "Bitcoin Stratum templates require BitcoinTemplate metadata");
                }

                ValidateVersionRollingContract(filename, o.Key, bitcoinTemplate);

                if(family == CoinFamily.BitcoinBlake2b)
                {
                    if(result is not BitcoinBlake2bTemplate blake2bTemplate)
                    {
                        throw new PoolStartupException(
                            $"Invalid coin-template '{o.Key}' in file '{filename}': " +
                            "Bitcoin BLAKE2b templates require BitcoinBlake2bTemplate metadata");
                    }

                    ValidateBitcoinBlake2bContract(filename, o.Key,
                        blake2bTemplate);
                }
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
