using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Autofac;
using AutoMapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IO;
using Miningcore.Blockchain;
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
using Miningcore.Blockchain.Satoshicash;
using Miningcore.Blockchain.Warthog;
using Miningcore.Blockchain.Xelis;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Progpow;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
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
        var (get, template, flagIndex, _) = CreateArrayJob(family);
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
        var (get, _, flagIndex, _) = CreateArrayJob(family);
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

    public static IEnumerable<object[]> DifficultyCases()
    {
        foreach(var family in new[] { "equihash", "verus", "xelis", "warthog", "ergo", "satoshicash" })
        foreach(bool? broadcast in new bool?[] { null, true, false })
            yield return new object[] { family, broadcast };
    }

    [Theory]
    [MemberData(nameof(DifficultyCases))]
    public async Task DifficultyUpdate_PreservesWorkAndWorkerTargetsWithoutDependingOnBroadcast(
        string family, bool? broadcast)
    {
        // Exercise actual pool issuance and queueing with a ready manager job,
        // including the interval before the pool has received its first broadcast.
        var (_, template, flagIndex, job) = CreateArrayJob(family);
        var (poolType, managerType, contextType) = family switch
        {
            "equihash" or "verus" => (typeof(EquihashPool), typeof(EquihashJobManager), typeof(EquihashWorkerContext)),
            "xelis" => (typeof(XelisPool), typeof(XelisJobManager), typeof(XelisWorkerContext)),
            "warthog" => (typeof(WarthogPool), typeof(WarthogJobManager), typeof(WarthogWorkerContext)),
            "ergo" => (typeof(ErgoPool), typeof(ErgoJobManager), typeof(ErgoWorkerContext)),
            _ => (typeof(SatoshicashPool), typeof(SatoshicashJobManager), typeof(SatoshicashWorkerContext)),
        };
        var clock = MockMasterClock.FromTicks(DateTime.UtcNow.Ticks);
        var streams = container.Resolve<RecyclableMemoryStreamManager>();
        using var scope = container.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(Substitute.For<IHttpClientFactory>());
            builder.RegisterInstance(Substitute.For<Miningcore.Persistence.Repositories.IBlockRepository>());
            builder.RegisterInstance(Substitute.For<IShareRepository>());
        });
        var manager = scope.Resolve(managerType,
            new TypedParameter(typeof(IExtraNonceProvider), Substitute.For<IExtraNonceProvider>()));
        FindField(managerType, "currentJob").SetValue(manager, job);
        if(manager is EquihashJobManager)
        {
            var zcash = Assert.IsType<EquihashCoinTemplate>(ModuleInitializer.CoinTemplates["zcash"]);
            typeof(EquihashJobManager).GetProperty(nameof(EquihashJobManager.ChainConfig))!
                .SetValue(manager, zcash.GetNetwork(ChainName.Mainnet));
        }

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var nicehash = new NicehashService(Substitute.For<IHttpClientFactory>(), memoryCache);
        var pool = Activator.CreateInstance(poolType, scope, jsonSerializerSettings,
            Substitute.For<IConnectionFactory>(), Substitute.For<IStatsRepository>(),
            container.Resolve<IMapper>(), clock, Substitute.For<IMessageBus>(), streams,
            nicehash);
        FindField(poolType, "manager").SetValue(pool, manager);
        if(manager is EquihashJobManager)
            FindField(poolType, "coin").SetValue(pool,
                new EquihashCoinTemplate { Symbol = family == "verus" ? "VRSC" : "ZEC" });
        var broadcastField = FindField(poolType, "currentJobParams");
        Assert.Null(broadcastField.GetValue(pool));
        if(broadcast.HasValue)
        {
            var broadcastParams = (object[]) template.Clone();
            broadcastParams[flagIndex] = broadcast.Value;
            broadcastField.SetValue(pool, broadcastParams);
        }
        var originalCache = JsonConvert.SerializeObject(template);
        var originalBroadcast = JsonConvert.SerializeObject(broadcastField.GetValue(pool));
        var update = poolType.GetMethod("OnVarDiffUpdateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var snapshots = new List<object[]>();

        foreach(var difficulty in new[] { 2d, 4d })
        {
            var context = (WorkerContextBase) Activator.CreateInstance(contextType);
            context.Init(1, null, clock);
            var connection = new StratumConnection(NLog.LogManager.GetCurrentClassLogger(),
                streams, clock, "difficulty-snapshot", false);
            connection.SetContext(context);
            await (Task) update.Invoke(pool, new object[] { connection, difficulty, CancellationToken.None });
            Assert.Equal(difficulty, context.Difficulty);

            var queue = Assert.IsType<BufferBlock<object>>(FindField(typeof(StratumConnection), "sendQueue")
                .GetValue(connection));
            Assert.True(queue.TryReceive(out var difficultyMessage));
            Assert.True(queue.TryReceive(out var jobMessage));
            Assert.False(queue.TryReceive(out _));
            var diff = Assert.IsType<JsonRpcRequest<object[]>>(difficultyMessage);
            Assert.Equal(family is "equihash" or "verus" ? EquihashStratumMethods.SetTarget :
                BitcoinStratumMethods.SetDifficulty, diff.Method);
            // Ergo's factory is array-typed; the other pools expose object.
            var (method, parameters) = jobMessage switch
            {
                JsonRpcRequest<object[]> notify => (notify.Method, notify.Params),
                JsonRpcRequest<object> notify => (notify.Method, notify.Params),
                _ => throw new InvalidOperationException("Unexpected queued job notification"),
            };
            Assert.Equal(BitcoinStratumMethods.MiningNotify, method);
            var snapshot = Assert.IsType<object[]>(parameters);
            Assert.False(Assert.IsType<bool>(snapshot[flagIndex]));
            Assert.NotSame(template, snapshot);
            if(family == "ergo")
            {
                Assert.Equal((BitcoinConstants.Diff1 / (int) difficulty).ToString(), snapshot[6]);
                Assert.Null(template[6]);
            }
            if(family == "verus")
                Assert.Equal("solution", snapshot[^1]);
            snapshots.Add(snapshot);
        }

        Assert.NotSame(snapshots[0], snapshots[1]);
        var firstJson = JsonConvert.SerializeObject(snapshots[0]);
        Array.Fill(snapshots[1], "other-worker-mutation");
        Assert.Equal(firstJson, JsonConvert.SerializeObject(snapshots[0]));
        Assert.Equal(originalCache, JsonConvert.SerializeObject(template));
        Assert.Equal(originalBroadcast, JsonConvert.SerializeObject(broadcastField.GetValue(pool)));
    }

    private static FieldInfo FindField(Type type, string name)
    {
        for(var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if(field != null)
                return field;
        }
        throw new InvalidOperationException($"Test fixture field {type.Name}.{name} no longer exists");
    }

    private static (Func<bool, object[]> Get, object[] Template, int FlagIndex, object Job) CreateArrayJob(string family)
    {
        if(family == "ergo")
        {
            var ergo = new ErgoJob();
            ergo.Init(new WorkMessage { Height = 101, Msg = "aabb", B = 1000000 }, 2, 4, "ergo-job");
            var cache = (object[]) FindField(typeof(ErgoJob), "jobParams").GetValue(ergo);
            return (ergo.GetJobParams, cache, 8, ergo);
        }

        // Seed only the notification cache so these boundary tests exercise the
        // production methods without a daemon, native solver or fabricated PoW.
        if(family is "equihash" or "bitcoin-gold" or "verus")
        {
            var cache = new object[] { "job", "version", "prev", "merkle", "reserved", "time", "bits", false };
            if(family == "verus")
            {
                // Verus initializes clean_jobs=true before its trailing solution.
                cache[^1] = true;
                cache = cache.Append("solution").ToArray();
            }
            EquihashJob job = family switch
            {
                "bitcoin-gold" => new BitcoinGoldSnapshotJob(cache),
                "verus" => new VeruscoinSnapshotJob(cache),
                _ => new EquihashSnapshotJob(cache),
            };
            return (clean => (object[]) job.GetJobParams(clean), cache, 7, job);
        }

        if(family == "xelis")
        {
            var cache = new object[] { "job", "time", "prev", "xel/v2", false };
            var job = new XelisSnapshotJob(cache);
            return (clean => (object[]) job.GetJobParams(clean), cache, 4, job);
        }

        if(family == "satoshicash")
        {
            var cache = new object[] { "job", "prev", "prefix", "suffix", Array.Empty<string>(), "version", "bits", "time", false };
            var job = new SatoshicashSnapshotJob(cache);
            return (clean => (object[]) job.GetJobParams(clean), cache, 8, job);
        }

        var warthogCache = new object[] { "job", "prev", "merkle", "version", "bits", "time", false };
        var warthog = new WarthogSnapshotJob(warthogCache);
        return (clean => (object[]) warthog.GetJobParams(clean), warthogCache, 6, warthog);
    }

    private sealed class SatoshicashSnapshotJob : SatoshicashJob
    {
        public SatoshicashSnapshotJob(object[] cache) => jobParams = cache;
    }

    private sealed class EquihashSnapshotJob : EquihashJob
    {
        public EquihashSnapshotJob(object[] cache) => jobParams = cache;
    }

    private sealed class BitcoinGoldSnapshotJob : BitcoinGoldJob
    {
        public BitcoinGoldSnapshotJob(object[] cache) => jobParams = cache;
    }

    private sealed class VeruscoinSnapshotJob : VeruscoinJob
    {
        public VeruscoinSnapshotJob(object[] cache) => jobParams = cache;
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
