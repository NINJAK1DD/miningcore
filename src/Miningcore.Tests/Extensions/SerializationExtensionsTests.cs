using System;
using System.Collections.Generic;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Extensions;
using NBitcoin;
using Newtonsoft.Json;
using Xunit;

namespace Miningcore.Tests.Extensions;

public class SerializationExtensionsTests : TestBase
{
    class Foo
    {
        public int Bar { get; set; }

        [JsonExtensionData]
        public IDictionary<string, object> Extra { get; set; }
    }

    class Empty
    {
    }

    [Fact]
    public void SafeExtensionData_Empty()
    {
        var result = JsonConvert.DeserializeObject<Foo>("{ \"bar\": 1, \"foo\": {} }");

        var extra = result!.Extra.SafeExtensionDataAs<Empty>();
        Assert.NotNull(extra);
    }

    class Simple
    {
        public int Baz { get; set; }
    }

    [Fact]
    public void SafeExtensionData_Embedded()
    {
        var result = JsonConvert.DeserializeObject<Foo>("{ \"bar\": 1, \"baz\": 42 }");

        var extra = result!.Extra.SafeExtensionDataAs<Simple>();
        Assert.NotNull(extra);
        Assert.Equal(42, extra.Baz);
    }

    [Fact]
    public void TryExtensionData_ReturnsSuccessfulBinding()
    {
        var result = JsonConvert.DeserializeObject<Foo>(
            "{ \"bar\": 1, \"baz\": 42 }");

        var success = result!.Extra.TryExtensionDataAs(
            out Simple extra, out var error);

        Assert.True(success);
        Assert.NotNull(extra);
        Assert.Equal(42, extra.Baz);
        Assert.Null(error);
    }

    [Fact]
    public void TryExtensionData_PreservesBindingFailure()
    {
        var result = JsonConvert.DeserializeObject<Foo>(
            "{ \"bar\": 1, \"baz\": \"not-an-integer\" }");

        var success = result!.Extra.TryExtensionDataAs(
            out Simple extra, out var error);

        Assert.False(success);
        Assert.Null(extra);
        Assert.NotNull(error);
        Assert.Contains("baz", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Extra.SafeExtensionDataAs<Simple>());
    }

    [Fact]
    public void TryExtensionData_NullSourceIsSuccessfulNoOp()
    {
        IDictionary<string, object> source = null;

        var success = source.TryExtensionDataAs(
            out Simple extra, out var error);

        Assert.True(success);
        Assert.Null(extra);
        Assert.Null(error);
    }

    class Wrapped
    {
        public int Baz { get; set; }
    }

    class Aliased
    {
        [JsonProperty("api_key")]
        public string ApiKey { get; set; }

        [JsonIgnore]
        public string Ignored { get; set; }

        public string ReadOnly => "not-bindable";
    }

    [JsonConverter(typeof(UnsupportedContractConverter))]
    class ConvertedContract
    {
        public string Value { get; set; }
    }

    class ExtensionDataContract
    {
        [JsonExtensionData]
        public IDictionary<string, object> Extra { get; set; }
    }

    class PopulatedGetterContract
    {
        public IList<string> Values { get; } = new List<string>();
    }

    class CollidingAliasContract
    {
        [JsonProperty("api_key")]
        public string First { get; set; }

        [JsonProperty("API_KEY")]
        public string Second { get; set; }
    }

    class DuplicateAliasContract
    {
        [JsonProperty("duplicate")]
        public string First { get; set; }

        [JsonProperty("duplicate")]
        public string Second { get; set; }
    }

    class ConstructorBoundContract
    {
        [JsonConstructor]
        public ConstructorBoundContract(string walletPassword)
        {
            WalletPassword = walletPassword;
        }

        public string WalletPassword { get; }
    }

    class PropertyConvertedContract
    {
        [JsonConverter(typeof(UnsupportedContractConverter))]
        public string Value { get; set; }
    }

    [JsonObject(MemberSerialization.Fields)]
    class FieldSerializedContract
    {
        public string Value = string.Empty;
    }

    class GetterOnlyArrayContract
    {
        public int[] Values { get; } = Array.Empty<int>();
        public string Writable { get; set; }
    }

    [Fact]
    public void ExtensionDataContract_UsesTheRuntimeBinderNames()
    {
        var properties = SerializationExtensions.
            GetExtensionDataPropertyContracts(typeof(Aliased));
        var property = Assert.Single(properties);

        Assert.Equal(nameof(Aliased.ApiKey), property.ClrName);
        Assert.Equal("api_key", property.JsonName);

        var bound = new Dictionary<string, object>
        {
            ["api_key"] = "accepted-alias",
        }.SafeExtensionDataAs<Aliased>();

        Assert.NotNull(bound);
        Assert.Equal("accepted-alias", bound.ApiKey);

        var unaliased = new Dictionary<string, object>
        {
            [nameof(Aliased.ApiKey)] = "clr-name-is-not-the-contract-name",
        }.SafeExtensionDataAs<Aliased>();

        Assert.NotNull(unaliased);
        Assert.Null(unaliased.ApiKey);
    }

    [Fact]
    public void ExtensionDataContract_RejectsNonObjectContractsStrictly()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SerializationExtensions.GetExtensionDataPropertyContracts(
                typeof(string)));

        Assert.Contains("non-object JSON contract",
            exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(ConvertedContract), "type-level JSON converter")]
    [InlineData(typeof(ExtensionDataContract), "extension-data capture")]
    [InlineData(typeof(PopulatedGetterContract),
        "getter-only collection property")]
    [InlineData(typeof(CollidingAliasContract),
        "case-insensitive JSON property collision")]
    [InlineData(typeof(ConstructorBoundContract),
        "constructor-based JSON deserialization")]
    [InlineData(typeof(PropertyConvertedContract),
        "property-level JSON converter")]
    [InlineData(typeof(FieldSerializedContract),
        "field-based JSON member serialization")]
    public void ExtensionDataContract_RejectsUnsupportedAdvancedContracts(
        Type type, string expectedMessage)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SerializationExtensions.GetExtensionDataPropertyContracts(type));

        Assert.Contains(expectedMessage, exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExtensionDataContract_WrapsResolverFailuresWithTypeContext()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SerializationExtensions.GetExtensionDataPropertyContracts(
                typeof(DuplicateAliasContract)));

        Assert.Contains(typeof(DuplicateAliasContract).FullName,
            exception.Message, StringComparison.Ordinal);
        Assert.Contains("unambiguous JSON contract", exception.Message,
            StringComparison.Ordinal);
        Assert.IsType<JsonSerializationException>(exception.InnerException);
    }

    [Fact]
    public void ExtensionDataContract_DoesNotRejectGetterOnlyArrays()
    {
        var property = Assert.Single(SerializationExtensions.
            GetExtensionDataPropertyContracts(
                typeof(GetterOnlyArrayContract)));

        Assert.Equal(nameof(GetterOnlyArrayContract.Writable),
            property.ClrName);
    }

    class UnsupportedContractConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => true;

        public override object ReadJson(JsonReader reader, Type objectType,
            object existingValue, JsonSerializer serializer) =>
            throw new NotSupportedException();

        public override void WriteJson(JsonWriter writer, object value,
            JsonSerializer serializer) => throw new NotSupportedException();
    }

    [Fact]
    public void SafeExtensionData_Wrapped()
    {
        var result = JsonConvert.DeserializeObject<Foo>("{ \"bar\": 1, \"foo\": { \"baz\": 42 } }");

        var extra = result!.Extra.SafeExtensionDataAs<Wrapped>("foo");
        Assert.NotNull(extra);
        Assert.Equal(42, extra.Baz);
    }

    [Fact]
    public void SafeExtensionData_Double_Wrapped()
    {
        var result = JsonConvert.DeserializeObject<Foo>("{ \"bar\": 1, \"foo\": { \"qux\": { \"baz\": 42 } } }");

        var extra = result!.Extra.SafeExtensionDataAs<Wrapped>("foo", "qux");
        Assert.NotNull(extra);
        Assert.Equal(42, extra.Baz);
    }

    [Fact]
    public void SafeExtensionData_Triple_Wrapped()
    {
        var result = JsonConvert.DeserializeObject<Foo>("{ \"bar\": 1, \"foo\": { \"qux\": { \"thud\": { \"baz\": 42 } } } }");

        var extra = result!.Extra.SafeExtensionDataAs<Wrapped>("foo", "qux", "thud");
        Assert.NotNull(extra);
        Assert.Equal(42, extra.Baz);
    }
}
