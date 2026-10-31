using System.Globalization;
using System.Numerics;
using Newtonsoft.Json;

namespace Miningcore.Blockchain.Xelis;

// Modern daemons send decimal strings; legacy daemons send JSON integers.
// Never route consensus difficulty through floating point or accept exponent notation.
public sealed class XelisDifficultyJsonConverter : JsonConverter<BigInteger>
{
    public override BigInteger ReadJson(JsonReader reader, Type objectType, BigInteger existingValue,
        bool hasExistingValue, JsonSerializer serializer)
    {
        var text = reader.TokenType switch
        {
            JsonToken.String => (string) reader.Value,
            JsonToken.Integer => Convert.ToString(reader.Value, CultureInfo.InvariantCulture),
            _ => null,
        };

        if(string.IsNullOrEmpty(text) || text.Length > 78 || text.Any(c => c < '0' || c > '9') ||
            !BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < BigInteger.One || value > XelisConstants.Diff1Target)
            throw new JsonSerializationException("Expected a positive 256-bit Xelis difficulty");

        return value;
    }

    public override void WriteJson(JsonWriter writer, BigInteger value, JsonSerializer serializer) =>
        writer.WriteValue(value.ToString(CultureInfo.InvariantCulture));
}
