using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Time;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Crypto;

public class Blake2bStartupTests : TestBase
{
    private sealed class Manager() : KaspaJobManager(Substitute.For<IComponentContext>(),
        Substitute.For<IMessageBus>(), new StandardClock(), Substitute.For<IExtraNonceProvider>())
    {
        internal bool ConfiguredDaemons;
        protected override void ConfigureDaemons() => ConfiguredDaemons = true;
    }

    private static PoolConfig Pool(string symbol, string id) => new()
    {
        Id = id, Template = new KaspaCoinTemplate { Symbol = symbol },
        PaymentProcessing = new PoolPaymentProcessingConfig(), Daemons = [],
    };

    public static IEnumerable<object[]> Symbols() => ModuleInitializer.CoinTemplates.Values
        .OfType<KaspaCoinTemplate>().Select(x => x.Symbol).Distinct().Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(Symbols))]
    [InlineData("CUSTOM")]
    public void EveryFamilyChecksKeyedRuntimeBeforeDaemonsWithItsOwnPoolId(string symbol)
    {
        var required = symbol is not ("CAS" or "HTN" or "WALA");
        Assert.Equal(required, KaspaJobManager.RequiresKeyedBlake2b(symbol));
        foreach(var available in new[] { false, true })
        {
            var calls = 0;
            var cached = new Lazy<bool>(() => { calls++; return available; });
            foreach(var id in new[] { "pool-a", "pool-b" })
            {
                var manager = new Manager
                {
                    Blake3CapabilityProbe = () => true,
                    Blake2bKeyedCapabilityProbe = () => cached.Value,
                };
                var pool = Pool(symbol, id);
                if(required && !available)
                {
                    // Incomplete daemon/payment settings deliberately prove that
                    // capability rejection happens before configuration is consumed.
                    pool.PaymentProcessing = null;
                    pool.Daemons = null;
                    var error = Assert.Throws<TrustedPoolStartupException>(() => manager.Configure(pool, new ClusterConfig()));
                    Assert.Equal(id, error.PoolId);
                    Assert.Contains("blake2b_export", error.Message);
                    Assert.Contains("keyed", error.Message);
                }
                else
                    manager.Configure(pool, new ClusterConfig());
                Assert.Equal(!required || available, manager.ConfiguredDaemons);
            }
            Assert.Equal(required ? 1 : 0, calls);
        }
    }

    [Fact]
    public void ClassificationMatchesActualJobHashersIncludingBothPyrinEras()
    {
        foreach(var symbol in Symbols().Select(x => (string) x[0]).Append("CUSTOM"))
        {
            var requiresKeyed = false;
            foreach(var height in symbol == "PYI" ? new[] { 0UL, ulong.MaxValue } : new[] { 0UL })
            {
                var manager = new Manager { Blake3CapabilityProbe = () => true, Blake2bKeyedCapabilityProbe = () => true };
                manager.Configure(Pool(symbol, "classification"), new ClusterConfig());
                typeof(KaspaJobManager).GetField("network", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, "mainnet");
                // Height zero avoids constructing the large Karlsen FishHash dataset.
                var job = (KaspaJob) typeof(KaspaJobManager).GetMethod("CreateJob", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(manager, new object[] { height });
                for(var type = job.GetType(); type != null; type = type.BaseType)
                    requiresKeyed |= type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Any(field => field.GetValue(job) is Blake2b { dataKey.Length: > 0 });
            }
            Assert.Equal(requiresKeyed, KaspaJobManager.RequiresKeyedBlake2b(symbol));
        }
    }

    [Fact]
    public void MixedPoolsDoNotReuseAnExemptPoolsCapabilityDecision()
    {
        foreach(var symbol in new[] { "CAS", "KAS", "WALA", "SPR", "HTN" })
        {
            var manager = new Manager { Blake3CapabilityProbe = () => true, Blake2bKeyedCapabilityProbe = () => false };
            var pool = Pool(symbol, "mixed-" + symbol);
            if(symbol is "KAS" or "SPR")
                Assert.Equal(pool.Id, Assert.Throws<TrustedPoolStartupException>(() => manager.Configure(pool, new ClusterConfig())).PoolId);
            else
                manager.Configure(pool, new ClusterConfig());
            Assert.Equal(symbol is not ("KAS" or "SPR"), manager.ConfiguredDaemons);
        }
    }

    [Fact]
    public void CachedProbeErrorsKeepPerPoolContextWithoutLeakingNativeDetails()
    {
        const string secret = "private-native-path-and-key";
        var calls = 0;
        var probe = new Lazy<bool>(() => { calls++; throw new InvalidOperationException(secret); });
        foreach(var id in new[] { "first", "second" })
        {
            var manager = new Manager { Blake2bKeyedCapabilityProbe = () => probe.Value };
            var error = Assert.Throws<TrustedPoolStartupException>(() => manager.Configure(Pool("KAS", id), new ClusterConfig()));
            Assert.Equal(id, error.PoolId);
            Assert.DoesNotContain(secret, error.ToString());
            Assert.Null(error.InnerException);
            Assert.False(manager.ConfiguredDaemons);
        }
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("KAS")]
    [InlineData("SPR")]
    public void ActualRuntimeIsCheckedBeforeKaspaOrSpectreDaemonSetup(string symbol)
    {
        var manager = new Manager();
        var pool = Pool(symbol, "real-" + symbol);
        if(Blake2bNativeCapability.IsKeyedAvailable())
            manager.Configure(pool, new ClusterConfig());
        else
            Assert.Equal(pool.Id, Assert.Throws<TrustedPoolStartupException>(() => manager.Configure(pool, new ClusterConfig())).PoolId);
        Assert.Equal(Blake2bNativeCapability.IsKeyedAvailable(), manager.ConfiguredDaemons);
    }
}
