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
    private readonly Dictionary<ApiPoolPaymentProcessingExtraField, string>
        propertyNames = new();

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
        string>> PresentProperties => propertyNames;

    internal bool IsPresent(ApiPoolPaymentProcessingExtraField field) =>
        propertyNames.ContainsKey(field);

    internal void SetWalletName(string name, string value)
    {
        Register(ApiPoolPaymentProcessingExtraField.WalletName, name);
        WalletName = value;
    }

    internal void SetBlockRewardsLockTime(string name, long? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.BlockRewardsLockTime,
            name);
        BlockRewardsLockTime = value;
    }

    internal void SetKeepTransactionFees(string name, bool? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.KeepTransactionFees,
            name);
        KeepTransactionFees = value;
    }

    internal void SetMinersPayTxFees(string name, bool? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.MinersPayTxFees, name);
        MinersPayTxFees = value;
    }

    internal void SetMinimumPaymentToPaymentId(string name, decimal? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.MinimumPaymentToPaymentId,
            name);
        MinimumPaymentToPaymentId = value;
    }

    internal void SetMaximumDestinationPerTransfer(string name, int? value)
    {
        Register(
            ApiPoolPaymentProcessingExtraField.MaximumDestinationPerTransfer,
            name);
        MaximumDestinationPerTransfer = value;
    }

    internal void SetMinimumConfirmations(string name, int? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.MinimumConfirmations,
            name);
        MinimumConfirmations = value;
    }

    internal void SetKeepUncles(string name, bool? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.KeepUncles, name);
        KeepUncles = value;
    }

    internal void SetGas(string name, ulong? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.Gas, name);
        Gas = value;
    }

    internal void SetMaxFeePerGas(string name, ulong? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.MaxFeePerGas, name);
        MaxFeePerGas = value;
    }

    internal void SetBlockSearchOffset(string name, uint? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.BlockSearchOffset, name);
        BlockSearchOffset = value;
    }

    internal void SetWalletAccount(string name, string value)
    {
        Register(ApiPoolPaymentProcessingExtraField.WalletAccount, name);
        WalletAccount = value;
    }

    internal void SetVersionEnablingMaxFee(string name, string value)
    {
        Register(ApiPoolPaymentProcessingExtraField.VersionEnablingMaxFee,
            name);
        VersionEnablingMaxFee = value;
    }

    internal void SetMaxFee(string name, ulong? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.MaxFee, name);
        MaxFee = value;
    }

    internal void SetMaximumTransactionFees(string name, decimal? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.MaximumTransactionFees,
            name);
        MaximumTransactionFees = value;
    }

    internal void SetMaxDegreeOfParallelPayouts(string name, int? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.MaxDegreeOfParallelPayouts,
            name);
        MaxDegreeOfParallelPayouts = value;
    }

    internal void SetRevealPoolAddress(string name, bool? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.RevealPoolAddress, name);
        RevealPoolAddress = value;
    }

    internal void SetHideMinerAddress(string name, bool? value)
    {
        Register(ApiPoolPaymentProcessingExtraField.HideMinerAddress, name);
        HideMinerAddress = value;
    }

    private void Register(ApiPoolPaymentProcessingExtraField field,
        string name)
    {
        if(string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A public payment property requires a name",
                nameof(name));

        if(!propertyNames.TryAdd(field, name))
        {
            throw new InvalidOperationException(
                $"Public payment property '{field}' was assigned more than once");
        }
    }
}

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
    public static bool TryResolve(string name,
        out ApiPoolPaymentProcessingExtraField field)
    {
        foreach(var candidate in Enum.GetValues<
                    ApiPoolPaymentProcessingExtraField>())
        {
            if(string.Equals(name, candidate.ToString(),
                   StringComparison.OrdinalIgnoreCase))
            {
                field = candidate;
                return true;
            }
        }

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

            SetFromSystemTextJson(result, field, property.Name,
                property.Value, options);
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
            writer.WritePropertyName(property.Value);
            WriteSystemTextJsonValue(writer, value, property.Key, options);
        }

        writer.WriteEndObject();
    }

    private static void SetFromSystemTextJson(
        ApiPoolPaymentProcessingExtra target,
        ApiPoolPaymentProcessingExtraField field, string name,
        JsonElement value, JsonSerializerOptions options)
    {
        switch(field)
        {
            case ApiPoolPaymentProcessingExtraField.WalletName:
                target.SetWalletName(name,
                    value.Deserialize<string>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.BlockRewardsLockTime:
                target.SetBlockRewardsLockTime(name,
                    value.Deserialize<long?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.KeepTransactionFees:
                target.SetKeepTransactionFees(name,
                    value.Deserialize<bool?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.MinersPayTxFees:
                target.SetMinersPayTxFees(name,
                    value.Deserialize<bool?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.MinimumPaymentToPaymentId:
                target.SetMinimumPaymentToPaymentId(name,
                    value.Deserialize<decimal?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.
                MaximumDestinationPerTransfer:
                target.SetMaximumDestinationPerTransfer(name,
                    value.Deserialize<int?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.MinimumConfirmations:
                target.SetMinimumConfirmations(name,
                    value.Deserialize<int?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.KeepUncles:
                target.SetKeepUncles(name,
                    value.Deserialize<bool?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.Gas:
                target.SetGas(name, value.Deserialize<ulong?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.MaxFeePerGas:
                target.SetMaxFeePerGas(name,
                    value.Deserialize<ulong?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.BlockSearchOffset:
                target.SetBlockSearchOffset(name,
                    value.Deserialize<uint?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.WalletAccount:
                target.SetWalletAccount(name,
                    value.Deserialize<string>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.VersionEnablingMaxFee:
                target.SetVersionEnablingMaxFee(name,
                    value.Deserialize<string>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.MaxFee:
                target.SetMaxFee(name, value.Deserialize<ulong?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.MaximumTransactionFees:
                target.SetMaximumTransactionFees(name,
                    value.Deserialize<decimal?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.
                MaxDegreeOfParallelPayouts:
                target.SetMaxDegreeOfParallelPayouts(name,
                    value.Deserialize<int?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.RevealPoolAddress:
                target.SetRevealPoolAddress(name,
                    value.Deserialize<bool?>(options));
                break;
            case ApiPoolPaymentProcessingExtraField.HideMinerAddress:
                target.SetHideMinerAddress(name,
                    value.Deserialize<bool?>(options));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field,
                    null);
        }
    }

    private static void WriteSystemTextJsonValue(Utf8JsonWriter writer,
        ApiPoolPaymentProcessingExtra source,
        ApiPoolPaymentProcessingExtraField field,
        JsonSerializerOptions options)
    {
        switch(field)
        {
            case ApiPoolPaymentProcessingExtraField.WalletName:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.WalletName, options);
                break;
            case ApiPoolPaymentProcessingExtraField.BlockRewardsLockTime:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.BlockRewardsLockTime, options);
                break;
            case ApiPoolPaymentProcessingExtraField.KeepTransactionFees:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.KeepTransactionFees, options);
                break;
            case ApiPoolPaymentProcessingExtraField.MinersPayTxFees:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.MinersPayTxFees, options);
                break;
            case ApiPoolPaymentProcessingExtraField.MinimumPaymentToPaymentId:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.MinimumPaymentToPaymentId, options);
                break;
            case ApiPoolPaymentProcessingExtraField.
                MaximumDestinationPerTransfer:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.MaximumDestinationPerTransfer, options);
                break;
            case ApiPoolPaymentProcessingExtraField.MinimumConfirmations:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.MinimumConfirmations, options);
                break;
            case ApiPoolPaymentProcessingExtraField.KeepUncles:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.KeepUncles, options);
                break;
            case ApiPoolPaymentProcessingExtraField.Gas:
                System.Text.Json.JsonSerializer.Serialize(writer, source.Gas,
                    options);
                break;
            case ApiPoolPaymentProcessingExtraField.MaxFeePerGas:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.MaxFeePerGas, options);
                break;
            case ApiPoolPaymentProcessingExtraField.BlockSearchOffset:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.BlockSearchOffset, options);
                break;
            case ApiPoolPaymentProcessingExtraField.WalletAccount:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.WalletAccount, options);
                break;
            case ApiPoolPaymentProcessingExtraField.VersionEnablingMaxFee:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.VersionEnablingMaxFee, options);
                break;
            case ApiPoolPaymentProcessingExtraField.MaxFee:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.MaxFee, options);
                break;
            case ApiPoolPaymentProcessingExtraField.MaximumTransactionFees:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.MaximumTransactionFees, options);
                break;
            case ApiPoolPaymentProcessingExtraField.
                MaxDegreeOfParallelPayouts:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.MaxDegreeOfParallelPayouts, options);
                break;
            case ApiPoolPaymentProcessingExtraField.RevealPoolAddress:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.RevealPoolAddress, options);
                break;
            case ApiPoolPaymentProcessingExtraField.HideMinerAddress:
                System.Text.Json.JsonSerializer.Serialize(writer,
                    source.HideMinerAddress, options);
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
        if(reader.TokenType == Newtonsoft.Json.JsonToken.Null)
            return null;

        var source = JObject.Load(reader);
        var result = new ApiPoolPaymentProcessingExtra();

        foreach(var property in source.Properties())
        {
            if(!ApiPoolPaymentProcessingExtraFieldNames.TryResolve(
                   property.Name, out var field))
            {
                continue;
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
            writer.WritePropertyName(property.Value);
            WriteNewtonsoftJsonValue(writer, value, property.Key, serializer);
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
                target.SetWalletName(name, value.ToObject<string>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.BlockRewardsLockTime:
                target.SetBlockRewardsLockTime(name,
                    value.ToObject<long?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.KeepTransactionFees:
                target.SetKeepTransactionFees(name,
                    value.ToObject<bool?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.MinersPayTxFees:
                target.SetMinersPayTxFees(name,
                    value.ToObject<bool?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.MinimumPaymentToPaymentId:
                target.SetMinimumPaymentToPaymentId(name,
                    value.ToObject<decimal?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.
                MaximumDestinationPerTransfer:
                target.SetMaximumDestinationPerTransfer(name,
                    value.ToObject<int?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.MinimumConfirmations:
                target.SetMinimumConfirmations(name,
                    value.ToObject<int?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.KeepUncles:
                target.SetKeepUncles(name,
                    value.ToObject<bool?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.Gas:
                target.SetGas(name, value.ToObject<ulong?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.MaxFeePerGas:
                target.SetMaxFeePerGas(name,
                    value.ToObject<ulong?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.BlockSearchOffset:
                target.SetBlockSearchOffset(name,
                    value.ToObject<uint?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.WalletAccount:
                target.SetWalletAccount(name,
                    value.ToObject<string>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.VersionEnablingMaxFee:
                target.SetVersionEnablingMaxFee(name,
                    value.ToObject<string>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.MaxFee:
                target.SetMaxFee(name, value.ToObject<ulong?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.MaximumTransactionFees:
                target.SetMaximumTransactionFees(name,
                    value.ToObject<decimal?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.
                MaxDegreeOfParallelPayouts:
                target.SetMaxDegreeOfParallelPayouts(name,
                    value.ToObject<int?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.RevealPoolAddress:
                target.SetRevealPoolAddress(name,
                    value.ToObject<bool?>(serializer));
                break;
            case ApiPoolPaymentProcessingExtraField.HideMinerAddress:
                target.SetHideMinerAddress(name,
                    value.ToObject<bool?>(serializer));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field,
                    null);
        }
    }

    private static void WriteNewtonsoftJsonValue(
        Newtonsoft.Json.JsonWriter writer,
        ApiPoolPaymentProcessingExtra source,
        ApiPoolPaymentProcessingExtraField field,
        Newtonsoft.Json.JsonSerializer serializer)
    {
        switch(field)
        {
            case ApiPoolPaymentProcessingExtraField.WalletName:
                serializer.Serialize(writer, source.WalletName);
                break;
            case ApiPoolPaymentProcessingExtraField.BlockRewardsLockTime:
                serializer.Serialize(writer, source.BlockRewardsLockTime);
                break;
            case ApiPoolPaymentProcessingExtraField.KeepTransactionFees:
                serializer.Serialize(writer, source.KeepTransactionFees);
                break;
            case ApiPoolPaymentProcessingExtraField.MinersPayTxFees:
                serializer.Serialize(writer, source.MinersPayTxFees);
                break;
            case ApiPoolPaymentProcessingExtraField.MinimumPaymentToPaymentId:
                serializer.Serialize(writer,
                    source.MinimumPaymentToPaymentId);
                break;
            case ApiPoolPaymentProcessingExtraField.
                MaximumDestinationPerTransfer:
                serializer.Serialize(writer,
                    source.MaximumDestinationPerTransfer);
                break;
            case ApiPoolPaymentProcessingExtraField.MinimumConfirmations:
                serializer.Serialize(writer, source.MinimumConfirmations);
                break;
            case ApiPoolPaymentProcessingExtraField.KeepUncles:
                serializer.Serialize(writer, source.KeepUncles);
                break;
            case ApiPoolPaymentProcessingExtraField.Gas:
                serializer.Serialize(writer, source.Gas);
                break;
            case ApiPoolPaymentProcessingExtraField.MaxFeePerGas:
                serializer.Serialize(writer, source.MaxFeePerGas);
                break;
            case ApiPoolPaymentProcessingExtraField.BlockSearchOffset:
                serializer.Serialize(writer, source.BlockSearchOffset);
                break;
            case ApiPoolPaymentProcessingExtraField.WalletAccount:
                serializer.Serialize(writer, source.WalletAccount);
                break;
            case ApiPoolPaymentProcessingExtraField.VersionEnablingMaxFee:
                serializer.Serialize(writer, source.VersionEnablingMaxFee);
                break;
            case ApiPoolPaymentProcessingExtraField.MaxFee:
                serializer.Serialize(writer, source.MaxFee);
                break;
            case ApiPoolPaymentProcessingExtraField.MaximumTransactionFees:
                serializer.Serialize(writer, source.MaximumTransactionFees);
                break;
            case ApiPoolPaymentProcessingExtraField.
                MaxDegreeOfParallelPayouts:
                serializer.Serialize(writer,
                    source.MaxDegreeOfParallelPayouts);
                break;
            case ApiPoolPaymentProcessingExtraField.RevealPoolAddress:
                serializer.Serialize(writer, source.RevealPoolAddress);
                break;
            case ApiPoolPaymentProcessingExtraField.HideMinerAddress:
                serializer.Serialize(writer, source.HideMinerAddress);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field,
                    null);
        }
    }
}
