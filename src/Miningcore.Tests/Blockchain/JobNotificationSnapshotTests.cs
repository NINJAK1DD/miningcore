using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Equihash;
using Miningcore.Blockchain.Equihash.Custom.BitcoinGold;
using Miningcore.Blockchain.Equihash.Custom.Veruscoin;
using Miningcore.Blockchain.Ergo;
using Miningcore.Blockchain.Progpow;
using Miningcore.Blockchain.Progpow.Custom.Firo;
using Miningcore.Blockchain.Progpow.Custom.Kiiro;
using Miningcore.Blockchain.Progpow.Custom.Realichain;
using Miningcore.Blockchain.Progpow.Custom.Telestai;
using Miningcore.Blockchain.Warthog;
using Miningcore.Blockchain.Xelis;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Progpow;
using Miningcore.Tests.Util;
using NBitcoin;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain;

public class JobNotificationSnapshotTests : TestBase
{
    public static IEnumerable<object[]> ArrayCases()
    {
        foreach(var family in new[] { "equihash", "bitcoin-gold", "verus", "xelis", "warthog", "ergo" })
        foreach(var clean in new[] { true, false })
            yield return new object[] { family, clean };
    }

    [Theory]
    [MemberData(nameof(ArrayCases))]
    public void ArrayNotification_DeferredSerializationAndCallerMutationAreIsolated(string family, bool clean)
    {
        var (get, template, flagIndex) = CreateArrayJob(family);
        var expected = (object[]) template.Clone();
        expected[flagIndex] = clean;
        var expectedJson = JsonConvert.SerializeObject(expected);
        var originalCache = JsonConvert.SerializeObject(template);
        var first = get(clean);
        var second = get(!clean);
        // The later call is intentionally discarded to exercise retained snapshots.
        _ = get(clean);
        Assert.Equal(expectedJson, JsonConvert.SerializeObject(first));
        Assert.Equal(!clean, second[flagIndex]);
        Assert.NotSame(first, second);

        Array.Fill(second, "caller-modified");
        Assert.Equal(expectedJson, JsonConvert.SerializeObject(first));
        Assert.Equal(expectedJson, JsonConvert.SerializeObject(get(clean)));
        Assert.Equal(originalCache, JsonConvert.SerializeObject(template));
        for(var i = 0; i < first.Length; i++)
        {
            if(i != flagIndex)
                Assert.Same(template[i], first[i]);
        }
    }

    [Theory]
    [MemberData(nameof(ArrayCases))]
    public void ArrayNotification_ParallelCallsRetainEachRequestedFlag(string family, bool clean)
    {
        var (get, _, flagIndex) = CreateArrayJob(family);
        var results = new object[128][];
        Parallel.For(0, results.Length, i => results[i] = get(i % 2 == 0 ? clean : !clean));
        Assert.Equal(results.Length, results.Distinct(ReferenceEqualityComparer.Instance).Count());
        for(var i = 0; i < results.Length; i++)
            Assert.Equal(i % 2 == 0 ? clean : !clean, results[i][flagIndex]);
    }

    [Theory]
    [InlineData("progpow", true)]
    [InlineData("progpow", false)]
    [InlineData("firo", true)]
    [InlineData("firo", false)]
    [InlineData("kiiro", true)]
    [InlineData("kiiro", false)]
    [InlineData("realichain", true)]
    [InlineData("realichain", false)]
    [InlineData("telestai", true)]
    [InlineData("telestai", false)]
    public void ProgpowNotification_BaseAndDerivedCallsUseIndependentTypedSnapshots(string family, bool clean)
    {
        ProgpowJob job = family switch
        {
            "firo" => new FiroJob(),
            "kiiro" => new KiiroJob(),
            "realichain" => new RealichainJob(),
            "telestai" => new TelestaiJob(),
            _ => new ProgpowJob(),
        };
        var bitcoin = Assert.IsType<BitcoinTemplate>(ModuleInitializer.CoinTemplates["bitcoin"]);
        var block = new BlockTemplate
        {
            Version = 0x20000000,
            PreviousBlockhash = new string('0', 64),
            CoinbaseValue = 5_000_000_000,
            Target = "7" + new string('f', 63),
            CurTime = 1_700_000_000,
            Bits = "207fffff",
            Height = 101,
            Transactions = Array.Empty<BitcoinBlockTransaction>(),
        };
        job.Init(block, "progpow-snapshot", new PoolConfig
        {
            Coin = "snapshot",
            Template = new ProgpowCoinTemplate { Symbol = "TEST", CoinbaseTxVersion = 1 },
        }, null, new ClusterConfig(), MockMasterClock.FromTicks(
            DateTimeOffset.FromUnixTimeSeconds(block.CurTime).UtcTicks),
            new KeyId(new byte[20]), Network.RegTest, false, bitcoin.ShareMultiplier,
            bitcoin.CoinbaseHasherValue, bitcoin.HeaderHasherValue, bitcoin.BlockHasherValue,
            Substitute.For<IProgpowCache>());

        BitcoinJob baseReference = job;
        var first = Assert.IsType<ProgpowJobParams>(baseReference.GetJobParams(clean));
        var second = Assert.IsType<ProgpowJobParams>(job.GetJobParams(!clean));
        Assert.NotSame(first, second);
        Assert.Equal(clean, first.CleanJobs);
        Assert.Equal(!clean, second.CleanJobs);
        Assert.Equal(101ul, first.Height);
        second.CleanJobs = clean;
        Assert.Equal(!clean, Assert.IsType<ProgpowJobParams>(baseReference.GetJobParams(!clean)).CleanJobs);

        var results = new ProgpowJobParams[128];
        Parallel.For(0, results.Length, i => results[i] = Assert.IsType<ProgpowJobParams>(
            baseReference.GetJobParams(i % 2 == 0)));
        Assert.Equal(results.Length, results.Distinct(ReferenceEqualityComparer.Instance).Count());
        for(var i = 0; i < results.Length; i++)
        {
            Assert.Equal(i % 2 == 0, results[i].CleanJobs);
            Assert.Equal(101ul, results[i].Height);
        }
    }

    private static (Func<bool, object[]> Get, object[] Template, int FlagIndex) CreateArrayJob(string family)
    {
        if(family == "ergo")
        {
            var ergo = new ErgoJob();
            ergo.Init(new WorkMessage { Height = 101, Msg = "aabb", B = 1000000 }, 2, 4, "ergo-job");
            var cache = (object[]) typeof(ErgoJob).GetField("jobParams",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ergo);
            return (ergo.GetJobParams, cache, 8);
        }

        // Seed only the notification cache so these boundary tests exercise the
        // production methods without a daemon, native solver or fabricated PoW.
        if(family is "equihash" or "bitcoin-gold" or "verus")
        {
            EquihashJob job = family switch
            {
                "bitcoin-gold" => new BitcoinGoldJob(),
                "verus" => new VeruscoinJob(),
                _ => new EquihashJob(),
            };
            var cache = new object[] { "job", "version", "prev", "merkle", "reserved", "time", "bits", false };
            if(family == "verus")
                cache = cache.Append("solution").ToArray();
            typeof(EquihashJob).GetField("jobParams", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(job, cache);
            return (clean => (object[]) job.GetJobParams(clean), cache, 7);
        }

        if(family == "xelis")
        {
            var cache = new object[] { "job", "time", "prev", "xel/v2", false };
            var job = new XelisSnapshotJob(cache);
            return (clean => (object[]) job.GetJobParams(clean), cache, 4);
        }

        var warthogCache = new object[] { "job", "prev", "merkle", "version", "bits", "time", false };
        var warthog = new WarthogSnapshotJob(warthogCache);
        return (clean => (object[]) warthog.GetJobParams(clean), warthogCache, 6);
    }

    private sealed class XelisSnapshotJob : XelisJob
    {
        public XelisSnapshotJob(object[] cache) => jobParams = cache;
    }

    private sealed class WarthogSnapshotJob : WarthogJob
    {
        public WarthogSnapshotJob(object[] cache) => jobParams = cache;
    }
}
