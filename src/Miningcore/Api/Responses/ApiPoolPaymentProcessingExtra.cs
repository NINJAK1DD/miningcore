using System.Collections.Frozen;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace Miningcore.Api.Responses;

/// <summary>
/// Allowlisted public union of blockchain payment-processing settings.
/// Presence and configured key spelling are tracked separately so explicit
/// nulls and the established REST wire contract survive the typed boundary.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(
    typeof(ApiPoolPaymentProcessingExtraSystemTextJsonConverter))]
[Newtonsoft.Json.JsonConverter(
    typeof(ApiPoolPaymentProcessingExtraNewtonsoftJsonConverter))]
public sealed class ApiPoolPaymentProcessingExtra
{
    private readonly Dictionary<ApiPoolPaymentProcessingExtraField,
        ApiPoolPaymentProcessingExtraProperty> properties = new();

    public string WalletName { get; internal set; }
    public long? BlockRewardsLockTime { get; internal set; }
    public bool? KeepTransactionFees { get; internal set; }
    public bool? MinersPayTxFees { get; internal set; }
    public decimal? MinimumPaymentToPaymentId { get; internal set; }
    public int? MaximumDestinationPerTransfer { get; internal set; }
    public int? MinimumConfirmations { get; internal set; }
    public bool? KeepUncles { get; internal set; }
    public ulong? Gas { get; internal set; }
    public ulong? MaxFeePerGas { get; internal set; }
    public uint? BlockSearchOffset { get; internal set; }
    public string WalletAccount { get; internal set; }
    public string VersionEnablingMaxFee { get; internal set; }
    public ulong? MaxFee { get; internal set; }
    public decimal? MaximumTransactionFees { get; internal set; }
    public int? MaxDegreeOfParallelPayouts { get; internal set; }
    public bool? RevealPoolAddress { get; internal set; }
    public bool? HideMinerAddress { get; internal set; }

    internal IEnumerable<KeyValuePair<ApiPoolPaymentProcessingExtraField,
        ApiPoolPaymentProcessingExtraProperty>> PresentProperties =>
        properties;

    internal bool IsPresent(ApiPoolPaymentProcessingExtraField field) =>
        properties.ContainsKey(field);

    internal static bool IsSupportedWireValue(JToken value) =>
        value is JValue && value.Type is (JTokenType.Boolean or
            JTokenType.Integer or JTokenType.Float or JTokenType.String or
            JTokenType.Null);

    internal static string GetPreParsedDateError(string name) =>
        $"Payment property '{name}' was parsed as a Date before reaching " +
        "the public DTO. Deserialize from JSON text or materialize the " +
        "token with DateParseHandling.None; the original date-looking " +
        "string cannot be recovered.";

    internal void SetWalletName(string name, string value, JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.WalletName, name,
            wireValue);
        WalletName = value;
    }

    internal void SetBlockRewardsLockTime(string name, long? value,
        JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.BlockRewardsLockTime,
            name, wireValue);
        BlockRewardsLockTime = value;
    }

    internal void SetKeepTransactionFees(string name, bool? value,
        JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.KeepTransactionFees,
            name, wireValue);
        KeepTransactionFees = value;
    }

    internal void SetMinersPayTxFees(string name, bool? value,
        JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.MinersPayTxFees, name,
            wireValue);
        MinersPayTxFees = value;
    }

    internal void SetMinimumPaymentToPaymentId(string name, decimal? value,
        JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.MinimumPaymentToPaymentId,
            name, wireValue);
        MinimumPaymentToPaymentId = value;
    }

    internal void SetMaximumDestinationPerTransfer(string name, int? value,
        JToken wireValue)
    {
        Register(
            ApiPoolPaymentProcessingExtraField.MaximumDestinationPerTransfer,
            name, wireValue);
        MaximumDestinationPerTransfer = value;
    }

    internal void SetMinimumConfirmations(string name, int? value,
        JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.MinimumConfirmations,
            name, wireValue);
        MinimumConfirmations = value;
    }

    internal void SetKeepUncles(string name, bool? value, JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.KeepUncles, name,
            wireValue);
        KeepUncles = value;
    }

    internal void SetGas(string name, ulong? value, JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.Gas, name, wireValue);
        Gas = value;
    }

    internal void SetMaxFeePerGas(string name, ulong? value,
        JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.MaxFeePerGas, name,
            wireValue);
        MaxFeePerGas = value;
    }

    internal void SetBlockSearchOffset(string name, uint? value,
        JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.BlockSearchOffset, name,
            wireValue);
        BlockSearchOffset = value;
    }

    internal void SetWalletAccount(string name, string value,
        JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.WalletAccount, name,
            wireValue);
        WalletAccount = value;
    }

    internal void SetVersionEnablingMaxFee(string name, string value,
        JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.VersionEnablingMaxFee,
            name, wireValue);
        VersionEnablingMaxFee = value;
    }

    internal void SetMaxFee(string name, ulong? value, JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.MaxFee, name, wireValue);
        MaxFee = value;
    }

    internal void SetMaximumTransactionFees(string name, decimal? value,
        JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.MaximumTransactionFees,
            name, wireValue);
        MaximumTransactionFees = value;
    }

    internal void SetMaxDegreeOfParallelPayouts(string name, int? value,
        JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.MaxDegreeOfParallelPayouts,
            name, wireValue);
        MaxDegreeOfParallelPayouts = value;
    }

    internal void SetRevealPoolAddress(string name, bool? value,
        JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.RevealPoolAddress, name,
            wireValue);
        RevealPoolAddress = value;
    }

    internal void SetHideMinerAddress(string name, bool? value,
        JToken wireValue)
    {
        Register(ApiPoolPaymentProcessingExtraField.HideMinerAddress, name,
            wireValue);
        HideMinerAddress = value;
    }

    private void Register(ApiPoolPaymentProcessingExtraField field,
        string name, JToken wireValue)
    {
        if(string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A public payment property requires a name",
                nameof(name));

        // Configuration and DTO readers preserve JSON strings without
        // manufacturing Date tokens. Objects, arrays, Date values and other
        // non-JSON scalar tokens are never retained behind an approved name.
        if(wireValue?.Type == JTokenType.Date)
        {
            throw new ArgumentException(GetPreParsedDateError(name),
                nameof(wireValue));
        }

        if(!IsSupportedWireValue(wireValue))
        {
            throw new ArgumentException(
                "A public payment property requires a scalar JSON value",
                nameof(wireValue));
        }

        var property = new ApiPoolPaymentProcessingExtraProperty(name,
            wireValue.DeepClone());

        if(!properties.TryAdd(field, property))
        {
            throw new InvalidOperationException(
                $"Public payment property '{field}' was assigned more than once");
        }
    }
}

internal sealed record ApiPoolPaymentProcessingExtraProperty(string Name,
    JToken WireValue);

internal enum ApiPoolPaymentProcessingExtraField
{
    WalletName,
    BlockRewardsLockTime,
    KeepTransactionFees,
    MinersPayTxFees,
    MinimumPaymentToPaymentId,
    MaximumDestinationPerTransfer,
    MinimumConfirmations,
    KeepUncles,
    Gas,
    MaxFeePerGas,
    BlockSearchOffset,
    WalletAccount,
    VersionEnablingMaxFee,
    MaxFee,
    MaximumTransactionFees,
    MaxDegreeOfParallelPayouts,
    RevealPoolAddress,
    HideMinerAddress,
}

internal static class ApiPoolPaymentProcessingExtraFieldNames
{
    private static readonly FrozenDictionary<string,
        ApiPoolPaymentProcessingExtraField> Fields = Enum.GetValues<
            ApiPoolPaymentProcessingExtraField>()
        .ToFrozenDictionary(value => value.ToString(), value => value,
            StringComparer.OrdinalIgnoreCase);

    public static bool TryResolve(string name,
        out ApiPoolPaymentProcessingExtraField field)
    {
        if(name != null && Fields.TryGetValue(name, out field))
            return true;

        field = default;
        return false;
    }
}

internal sealed class ApiPoolPaymentProcessingExtraSystemTextJsonConverter :
    System.Text.Json.Serialization.JsonConverter<
        ApiPoolPaymentProcessingExtra>
{
    public override ApiPoolPaymentProcessingExtra Read(ref Utf8JsonReader reader,
        Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return ReadCore(ref reader);
        }
        catch(System.Text.Json.JsonException)
        {
            throw;
        }
        catch(Exception ex)
        {
            throw new System.Text.Json.JsonException(
                "Payment-processing extra data is invalid", ex);
        }
    }

    private static ApiPoolPaymentProcessingExtra ReadCore(
        ref Utf8JsonReader reader)
    {
        if(reader.TokenType == JsonTokenType.Null)
            return null;

        using var document = JsonDocument.ParseValue(ref reader);

        if(document.RootElement.ValueKind != JsonValueKind.Object)
            throw new System.Text.Json.JsonException(
                "Payment-processing extra data must be a JSON object");

        var result = new ApiPoolPaymentProcessingExtra();

        foreach(var property in document.RootElement.EnumerateObject())
        {
            if(!ApiPoolPaymentProcessingExtraFieldNames.TryResolve(
                   property.Name, out var field))
            {
                continue;
            }

            if(result.IsPresent(field))
            {
                throw new System.Text.Json.JsonException(
                    $"Payment property '{property.Name}' is duplicated by case");
            }

            // Use Json.NET conversion deliberately so external response DTO
            // consumers accept the same coercible scalar representations as
            // Miningcore's runtime payment configuration.
            var wireValue = ReadSystemTextJsonWireValue(property.Value);
            SetFromSystemTextJson(result, field, property.Name, wireValue);
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer,
        ApiPoolPaymentProcessingExtra value, JsonSerializerOptions options)
    {
        if(value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();

        foreach(var property in value.PresentProperties)
        {
            writer.WritePropertyName(property.Value.Name);
            WriteSystemTextJsonWireValue(writer, property.Value.WireValue);
        }

        writer.WriteEndObject();
    }

    private static void WriteSystemTextJsonWireValue(Utf8JsonWriter writer,
        JToken wireValue)
    {
        switch(wireValue.Type)
        {
            case JTokenType.Null:
                writer.WriteNullValue();
                break;
            case JTokenType.Boolean:
                writer.WriteBooleanValue(wireValue.Value<bool>());
                break;
            case JTokenType.String:
                // Route strings through Utf8JsonWriter so the API's configured
                // JavaScriptEncoder remains in force.
                writer.WriteStringValue(wireValue.Value<string>());
                break;
            case JTokenType.Integer:
            case JTokenType.Float:
                // Raw numeric text preserves Int64/BigInteger/double/decimal
                // fidelity and has no encoder-sensitive characters.
                writer.WriteRawValue(wireValue.ToString(
                    Newtonsoft.Json.Formatting.None));
                break;
            default:
                throw new System.Text.Json.JsonException(
                    $"Unsupported payment wire value '{wireValue.Type}'");
        }
    }

    private static JToken ReadSystemTextJsonWireValue(JsonElement value)
    {
        // GetRawText returns exactly one JsonElement value. JToken.ReadFrom
        // would require an explicit exhaustion check for arbitrary input text.
        using var textReader = new StringReader(value.GetRawText());
        using var jsonReader = new Newtonsoft.Json.JsonTextReader(textReader)
        {
            DateParseHandling = Newtonsoft.Json.DateParseHandling.None,
        };

        return JToken.ReadFrom(jsonReader);
    }

    private static void SetFromSystemTextJson(
        ApiPoolPaymentProcessingExtra target,
        ApiPoolPaymentProcessingExtraField field, string name,
        JToken value)
    {
        switch(field)
        {
            case ApiPoolPaymentProcessingExtraField.WalletName:
                target.SetWalletName(name,
                    value.ToObject<string>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.BlockRewardsLockTime:
                target.SetBlockRewardsLockTime(name,
                    value.ToObject<long?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.KeepTransactionFees:
                target.SetKeepTransactionFees(name,
                    value.ToObject<bool?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.MinersPayTxFees:
                target.SetMinersPayTxFees(name,
                    value.ToObject<bool?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.MinimumPaymentToPaymentId:
                target.SetMinimumPaymentToPaymentId(name,
                    value.ToObject<decimal?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.
                MaximumDestinationPerTransfer:
                target.SetMaximumDestinationPerTransfer(name,
                    value.ToObject<int?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.MinimumConfirmations:
                target.SetMinimumConfirmations(name,
                    value.ToObject<int?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.KeepUncles:
                target.SetKeepUncles(name,
                    value.ToObject<bool?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.Gas:
                target.SetGas(name, value.ToObject<ulong?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.MaxFeePerGas:
                target.SetMaxFeePerGas(name,
                    value.ToObject<ulong?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.BlockSearchOffset:
                target.SetBlockSearchOffset(name,
                    value.ToObject<uint?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.WalletAccount:
                target.SetWalletAccount(name,
                    value.ToObject<string>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.VersionEnablingMaxFee:
                target.SetVersionEnablingMaxFee(name,
                    value.ToObject<string>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.MaxFee:
                target.SetMaxFee(name, value.ToObject<ulong?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.MaximumTransactionFees:
                target.SetMaximumTransactionFees(name,
                    value.ToObject<decimal?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.
                MaxDegreeOfParallelPayouts:
                target.SetMaxDegreeOfParallelPayouts(name,
                    value.ToObject<int?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.RevealPoolAddress:
                target.SetRevealPoolAddress(name,
                    value.ToObject<bool?>(), value);
                break;
            case ApiPoolPaymentProcessingExtraField.HideMinerAddress:
                target.SetHideMinerAddress(name,
                    value.ToObject<bool?>(), value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field,
                    null);
        }
    }

}

internal sealed class ApiPoolPaymentProcessingExtraNewtonsoftJsonConverter :
    Newtonsoft.Json.JsonConverter<ApiPoolPaymentProcessingExtra>
{
    public override ApiPoolPaymentProcessingExtra ReadJson(
        Newtonsoft.Json.JsonReader reader, Type objectType,
        ApiPoolPaymentProcessingExtra existingValue, bool hasExistingValue,
        Newtonsoft.Json.JsonSerializer serializer)
    {
        try
        {
            return ReadJsonCore(reader, serializer);
        }
        catch(Newtonsoft.Json.JsonException)
        {
            throw;
        }
        catch(Exception ex)
        {
            throw new Newtonsoft.Json.JsonSerializationException(
                "Payment-processing extra data is invalid", ex);
        }
    }

    private static ApiPoolPaymentProcessingExtra ReadJsonCore(
        Newtonsoft.Json.JsonReader reader,
        Newtonsoft.Json.JsonSerializer serializer)
    {
        if(reader.TokenType == Newtonsoft.Json.JsonToken.Null)
            return null;

        var originalDateParseHandling = reader.DateParseHandling;
        JObject source;

        try
        {
            // The converter owns only this object. Preserve date-looking JSON
            // strings while restoring the caller's policy before returning.
            reader.DateParseHandling = Newtonsoft.Json.DateParseHandling.None;
            source = JObject.Load(reader);
        }
        finally
        {
            reader.DateParseHandling = originalDateParseHandling;
        }

        var result = new ApiPoolPaymentProcessingExtra();

        foreach(var property in source.Properties())
        {
            if(!ApiPoolPaymentProcessingExtraFieldNames.TryResolve(
                   property.Name, out var field))
            {
                continue;
            }

            if(property.Value.Type == JTokenType.Date)
            {
                // A JTokenReader replays an already-materialized Date token
                // without consulting DateParseHandling. Its original lexical
                // string, especially an explicit offset, is no longer
                // recoverable. Reject it rather than silently fabricating a
                // different public value.
                throw new Newtonsoft.Json.JsonSerializationException(
                    ApiPoolPaymentProcessingExtra.GetPreParsedDateError(
                        property.Name));
            }

            if(result.IsPresent(field))
            {
                throw new Newtonsoft.Json.JsonSerializationException(
                    $"Payment property '{property.Name}' is duplicated by case");
            }

            SetFromNewtonsoftJson(result, field, property.Name,
                property.Value, serializer);
        }

        return result;
    }

    public override void WriteJson(Newtonsoft.Json.JsonWriter writer,
        ApiPoolPaymentProcessingExtra value,
        Newtonsoft.Json.JsonSerializer serializer)
    {
        if(value == null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartObject();

        foreach(var property in value.PresentProperties)
        {
            writer.WritePropertyName(property.Value.Name);
            property.Value.WireValue.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static void SetFromNewtonsoftJson(
        ApiPoolPaymentProcessingExtra target,
        ApiPoolPaymentProcessingExtraField field, string name, JToken value,
        Newtonsoft.Json.JsonSerializer serializer)
    {
        switch(field)
        {
            case ApiPoolPaymentProcessingExtraField.WalletName:
                target.SetWalletName(name, value.ToObject<string>(serializer),
                    value);
                break;
            case ApiPoolPaymentProcessingExtraField.BlockRewardsLockTime:
                target.SetBlockRewardsLockTime(name,
                    value.ToObject<long?>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.KeepTransactionFees:
                target.SetKeepTransactionFees(name,
                    value.ToObject<bool?>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.MinersPayTxFees:
                target.SetMinersPayTxFees(name,
                    value.ToObject<bool?>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.MinimumPaymentToPaymentId:
                target.SetMinimumPaymentToPaymentId(name,
                    value.ToObject<decimal?>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.
                MaximumDestinationPerTransfer:
                target.SetMaximumDestinationPerTransfer(name,
                    value.ToObject<int?>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.MinimumConfirmations:
                target.SetMinimumConfirmations(name,
                    value.ToObject<int?>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.KeepUncles:
                target.SetKeepUncles(name,
                    value.ToObject<bool?>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.Gas:
                target.SetGas(name, value.ToObject<ulong?>(serializer),
                    value);
                break;
            case ApiPoolPaymentProcessingExtraField.MaxFeePerGas:
                target.SetMaxFeePerGas(name,
                    value.ToObject<ulong?>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.BlockSearchOffset:
                target.SetBlockSearchOffset(name,
                    value.ToObject<uint?>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.WalletAccount:
                target.SetWalletAccount(name,
                    value.ToObject<string>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.VersionEnablingMaxFee:
                target.SetVersionEnablingMaxFee(name,
                    value.ToObject<string>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.MaxFee:
                target.SetMaxFee(name, value.ToObject<ulong?>(serializer),
                    value);
                break;
            case ApiPoolPaymentProcessingExtraField.MaximumTransactionFees:
                target.SetMaximumTransactionFees(name,
                    value.ToObject<decimal?>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.
                MaxDegreeOfParallelPayouts:
                target.SetMaxDegreeOfParallelPayouts(name,
                    value.ToObject<int?>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.RevealPoolAddress:
                target.SetRevealPoolAddress(name,
                    value.ToObject<bool?>(serializer), value);
                break;
            case ApiPoolPaymentProcessingExtraField.HideMinerAddress:
                target.SetHideMinerAddress(name,
                    value.ToObject<bool?>(serializer), value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field,
                    null);
        }
    }

}
