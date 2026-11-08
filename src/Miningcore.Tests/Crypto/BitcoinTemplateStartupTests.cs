using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Equihash;
using Miningcore.Blockchain.Handshake;
using Miningcore.Blockchain.Nexa;
using Miningcore.Blockchain.Progpow;
using Miningcore.Blockchain.Satoshicash;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Crypto;

public class BitcoinTemplateStartupTests : TestBase
{
    private interface IProbe
    {
        bool ConfiguredDaemons { get; }
        void Configure(PoolConfig pc, ClusterConfig cc);
    }

    private sealed class HandshakeProbe() : HandshakeJobManager(Substitute.For<IComponentContext>(), new StandardClock(),
        Substitute.For<IMessageBus>(), Substitute.For<IExtraNonceProvider>()), IProbe
    {
        public bool ConfiguredDaemons { get; private set; }
        protected override void ConfigureDaemons() => ConfiguredDaemons = true;
    }

    private sealed class NexaProbe() : NexaJobManager(Substitute.For<IComponentContext>(), new StandardClock(),
        Substitute.For<IMessageBus>(), Substitute.For<IExtraNonceProvider>()), IProbe
    {
        public bool ConfiguredDaemons { get; private set; }
        protected override void ConfigureDaemons() => ConfiguredDaemons = true;
    }

    private sealed class SatoshicashProbe() : SatoshicashJobManager(Substitute.For<IComponentContext>(), new StandardClock(),
        Substitute.For<IMessageBus>(), Substitute.For<IExtraNonceProvider>()), IProbe
    {
        public bool ConfiguredDaemons { get; private set; }
        protected override void ConfigureDaemons() => ConfiguredDaemons = true;
    }

    private sealed class ProgpowProbe() : ProgpowJobManager(Substitute.For<IComponentContext>(), new StandardClock(),
        Substitute.For<IMessageBus>(), Substitute.For<IExtraNonceProvider>()), IProbe
    {
        public bool ConfiguredDaemons { get; private set; }
        protected override void ConfigureDaemons() => ConfiguredDaemons = true;
    }

    private sealed class EquihashProbe() : EquihashJobManager(Substitute.For<IComponentContext>(), new StandardClock(),
        Substitute.For<IMessageBus>(), Substitute.For<IExtraNonceProvider>()), IProbe
    {
        public bool ConfiguredDaemons { get; private set; }
        protected override void ConfigureDaemons() => ConfiguredDaemons = true;
    }

    private static readonly string[] families = ["handshake", "nexa", "satoshicash", "progpow"];
    private static readonly string[] slots = ["merkleTreeHasherValue", "coinbaseHasherValue", "headerHasherValue",
        "shareHasherValue", "blockHasherValue", "posBlockHasherValue"];

    private static IProbe CreateProbe(string family) => family switch
    {
        "handshake" => new HandshakeProbe(),
        "nexa" => new NexaProbe(),
        "satoshicash" => new SatoshicashProbe(),
        "progpow" => new ProgpowProbe(),
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    private static BitcoinTemplate Template(string family) => family == "progpow"
        ? new ProgpowCoinTemplate { Symbol = "startup-progpow", ComponentContext = ModuleInitializer.Container, Progpower = "kawpow" }
        : new BitcoinTemplate { ComponentContext = ModuleInitializer.Container };

    private static PoolConfig Pool(CoinTemplate template, string id, bool stratum) => new()
    {
        Id = id, Template = template, EnableInternalStratum = stratum, Daemons = [],
        PaymentProcessing = new PoolPaymentProcessingConfig(), Extra = new Dictionary<string, object>(),
    };

    public static IEnumerable<object[]> FamilySlots() =>
        from family in families from slot in slots select new object[] { family, slot };

    [Theory]
    [MemberData(nameof(FamilySlots))]
    public void EverySiblingRejectsEveryHasherBeforeDaemonsWithCurrentPoolContext(string family, string slot)
    {
        var template = Template(family);
        var calls = 0;
        var original = new InvalidOperationException("private-native-path-and-key");
        // Isolate the Lazy failure from the global runtime cache so rejection is
        // exercised on supported Linux as well as unsupported Windows runtimes.
        typeof(BitcoinTemplate).GetField(slot, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(template,
            new Lazy<IHashAlgorithm>(() => { calls++; throw new TrustedPoolStartupException("blake3_export required", null, original); }));
        foreach(var id in new[] { "pool-a", "pool-b" })
        foreach(var stratum in new[] { false, true })
        {
            var manager = CreateProbe(family);
            var error = Assert.Throws<TrustedPoolStartupException>(() => manager.Configure(Pool(template, id, stratum), new ClusterConfig()));
            Assert.Equal(id, error.PoolId);
            Assert.Equal("blake3_export required", error.Message);
            Assert.Same(original, error.OriginalFailure);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain(original.Message, error.ToString());
            Assert.DoesNotContain(original.Message, JsonConvert.SerializeObject(error));
            Assert.False(manager.ConfiguredDaemons);
        }
        Assert.Equal(1, calls);
    }

    public static IEnumerable<object[]> FamilyDefinitions() =>
        from family in families from nested in new[] { false, true } select new object[] { family, nested };

    [Theory]
    [MemberData(nameof(FamilyDefinitions))]
    public void EverySiblingChecksDirectAndNestedBlake3Templates(string family, bool nested)
    {
        var template = Template(family);
        template.HeaderHasher = JObject.Parse(nested
            ? "{\"hash\":\"reverse\",\"args\":[{\"hash\":\"BLAKE3\"}]}" : "{\"hash\":\"BLAKE3\"}");
        foreach(var id in new[] { "pool-a", "pool-b" })
        foreach(var stratum in new[] { false, true })
        {
            var manager = CreateProbe(family);
            var pool = Pool(template, id, stratum);
            if(Blake3NativeCapability.IsAvailable()) manager.Configure(pool, new ClusterConfig());
            else
            {
                var error = Assert.Throws<TrustedPoolStartupException>(() => manager.Configure(pool, new ClusterConfig()));
                Assert.Equal(id, error.PoolId);
                Assert.Contains("blake3_export", error.Message);
            }
            Assert.Equal(Blake3NativeCapability.IsAvailable(), manager.ConfiguredDaemons);
        }
    }

    [Theory]
    [InlineData("handshake")] [InlineData("nexa")] [InlineData("satoshicash")] [InlineData("progpow")]
    public void ShippedSiblingTemplatesStillConfigure(string family)
    {
        var templates = ModuleInitializer.CoinTemplates.Values
            .Where(x => string.Equals(x.Family.ToString(), family, StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.NotEmpty(templates);
        foreach(var template in templates)
        foreach(var stratum in new[] { false, true })
        {
            var manager = CreateProbe(family);
            manager.Configure(Pool(template, "shipped-" + template.Symbol, stratum), new ClusterConfig());
            Assert.True(manager.ConfiguredDaemons);
        }
    }

    [Fact]
    public void EveryShippedBitcoinTemplateResolvesAllSixHasherSlots()
    {
        var templates = ModuleInitializer.CoinTemplates
            .Where(x => x.Value is BitcoinTemplate).OrderBy(x => x.Key).ToArray();
        Assert.NotEmpty(templates);
        Assert.Contains(templates, x => x.Value.Symbol == "LTC");
        Assert.Contains(templates, x => x.Value.Symbol == "DOGE");
        foreach(var (name, value) in templates)
        {
            var template = (BitcoinTemplate) value;
            var failure = Record.Exception(() =>
            {
                // Include optional/PoS slots even for chains that do not use them
                // immediately. New shipped templates are picked up automatically.
                Assert.Equal(template.MerkleTreeHasher != null, template.MerkleTreeHasherValue != null);
                Assert.Equal(template.CoinbaseHasher != null, template.CoinbaseHasherValue != null);
                Assert.Equal(template.HeaderHasher != null, template.HeaderHasherValue != null);
                Assert.Equal(template.ShareHasher != null, template.ShareHasherValue != null);
                Assert.Equal(template.BlockHasher != null, template.BlockHasherValue != null);
                Assert.Equal(template.PoSBlockHasher != null, template.PoSBlockHasherValue != null);
            });
            Assert.True(failure == null, $"Shipped Bitcoin template '{name}' failed hasher preflight: {failure}");
        }
    }

    [Fact]
    public void NonBitcoinTemplateConsumerRemainsUnaffected()
    {
        var manager = new EquihashProbe();
        manager.Configure(Pool(new EquihashCoinTemplate(), "equihash", true), new ClusterConfig());
        Assert.True(manager.ConfiguredDaemons);
    }
}
