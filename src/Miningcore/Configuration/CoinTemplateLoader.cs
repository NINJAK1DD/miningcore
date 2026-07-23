using Autofac;
using Miningcore.Mining;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Miningcore.Configuration;

public static class CoinTemplateLoader
{
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    private static IEnumerable<KeyValuePair<string, CoinTemplate>> LoadTemplates(string filename, IComponentContext ctx)
    {
        using var jreader = new JsonTextReader(File.OpenText(filename));

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

            var value = o.Value[nameof(CoinTemplate.Family).ToLower()];
            if(value == null)
                throw new PoolStartupException($"Invalid coin-template '{o.Key}': missing 'family' property");

            var family = value.ToObject<CoinFamily>();
            var result = (CoinTemplate) o.Value.ToObject(CoinTemplate.Families[family]);

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
