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
