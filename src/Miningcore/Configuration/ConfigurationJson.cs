using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Miningcore.Configuration;

/// <summary>
/// Defines the JSON policy shared by cluster and coin-template configuration.
/// Configuration strings are identifiers and operator-controlled values, not
/// implicit dates. Callers retain ownership of the supplied text reader.
/// </summary>
internal static class ConfigurationJson
{
    internal static JsonTextReader CreateReader(TextReader reader) =>
        new(reader)
        {
            CloseInput = false,
            DateParseHandling = DateParseHandling.None,
        };

    internal static JsonSerializerSettings CreateSerializerSettings() =>
        new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            DateParseHandling = DateParseHandling.None,
        };
}
