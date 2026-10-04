using System;
using Newtonsoft.Json;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

// Expected number of hashes, NOT Bitcoin reference-target share difficulty.
// The distinct type prevents accidental assignment to the accounting scale.
[JsonConverter(typeof(Blake2bHashWorkConverter))]
public sealed record Blake2bHashWork(double ExpectedHashes);

public sealed class Blake2bHashWorkConverter : JsonConverter<Blake2bHashWork>
{
    public override Blake2bHashWork ReadJson(JsonReader reader, Type objectType,
        Blake2bHashWork existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if(reader.TokenType is not (JsonToken.Integer or JsonToken.Float))
            throw new JsonSerializationException("difficulty_blake2b must be a positive finite JSON number");
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if(!double.TryParse(Convert.ToString(reader.Value, culture),
            System.Globalization.NumberStyles.Float, culture, out var value) || !double.IsFinite(value) || value <= 0)
            throw new JsonSerializationException("difficulty_blake2b must be positive and finite");
        return new(value);
    }

    public override void WriteJson(JsonWriter writer, Blake2bHashWork value, JsonSerializer serializer)
    {
        if(value == null) writer.WriteNull();
        else if(!double.IsFinite(value.ExpectedHashes) || value.ExpectedHashes <= 0)
            throw new JsonSerializationException("difficulty_blake2b must be positive and finite");
        else writer.WriteValue(value.ExpectedHashes);
    }
}
