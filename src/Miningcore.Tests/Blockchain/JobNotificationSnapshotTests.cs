using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Reactive;
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
using Miningcore.Time;
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
        var job = CreateProgpowJob(family);
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

    private static ProgpowJob CreateProgpowJob(string family)
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
        var hasher = Substitute.For<IProgpowCache>();
        hasher.SeedHash.Returns(new byte[32]);
        job.Init(block, "progpow-snapshot", new PoolConfig
        {
            Coin = "snapshot",
            Template = ModuleInitializer.CoinTemplates["ravencoin"],
        }, null, new ClusterConfig(), MockMasterClock.FromTicks(
            DateTimeOffset.FromUnixTimeSeconds(block.CurTime).UtcTicks),
            new KeyId(new byte[20]), Network.RegTest, false, bitcoin.ShareMultiplier,
            bitcoin.CoinbaseHasherValue, bitcoin.HeaderHasherValue, bitcoin.BlockHasherValue,
            hasher);

        return job;
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
            FindProperty(typeof(EquihashJobManager), nameof(EquihashJobManager.ChainConfig))
                .SetValue(manager, zcash.GetNetwork(ChainName.Mainnet));
        }

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var nicehash = new NicehashService(Substitute.For<IHttpClientFactory>(), memoryCache);
        var pool = CreatePool(family, scope, clock, streams, nicehash);
        FindField(poolType, "manager").SetValue(pool, manager);
        if(manager is EquihashJobManager)
            FindField(poolType, "coin").SetValue(pool,
                new EquihashCoinTemplate { Symbol = family == "verus" ? "VRSC" : "ZEC" });
        object[] broadcastParams = null;
        if(broadcast.HasValue)
        {
            broadcastParams = (object[]) template.Clone();
            broadcastParams[flagIndex] = broadcast.Value;
            await (Task) FindMethod(poolType, "OnNewJobAsync",
                family == "ergo" ? typeof(object[]) : typeof(object)).Invoke(pool, new object[] { broadcastParams });
        }
        var originalCache = JsonConvert.SerializeObject(template);
        var originalBroadcast = JsonConvert.SerializeObject(broadcastParams);
        var update = FindMethod(poolType, "OnVarDiffUpdateAsync", typeof(StratumConnection), typeof(double), typeof(CancellationToken));
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
        Assert.Equal(originalBroadcast, JsonConvert.SerializeObject(broadcastParams));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProgpowSubscribe_InitialWorkIsCleanRegardlessOfPriorBroadcast(bool? broadcast)
    {
        var job = CreateProgpowJob("progpow");
        var clock = MockMasterClock.FromTicks(DateTime.UtcNow.Ticks);
        var streams = container.Resolve<RecyclableMemoryStreamManager>();
        using var scope = container.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(Substitute.For<Miningcore.Persistence.Repositories.IBlockRepository>());
            builder.RegisterInstance(Substitute.For<IShareRepository>());
        });
        var extraNonce = Substitute.For<IExtraNonceProvider>();
        extraNonce.Next().Returns("00000001");
        var manager = new ProgpowJobManager(scope, clock, Substitute.For<IMessageBus>(), extraNonce);
        FindField(typeof(ProgpowJobManager), "currentJob").SetValue(manager, job);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var pool = CreatePool("progpow", scope, clock, streams,
            new NicehashService(Substitute.For<IHttpClientFactory>(), memoryCache));
        FindField(typeof(ProgpowPool), "manager").SetValue(pool, manager);
        FindField(typeof(ProgpowPool), "coin").SetValue(pool, ModuleInitializer.CoinTemplates["ravencoin"]);
        FindField(typeof(PoolBase), "poolConfig").SetValue(pool, new PoolConfig());
        if(broadcast.HasValue)
            await (Task) FindMethod(typeof(ProgpowPool), "OnNewJobAsync", typeof(object))
                .Invoke(pool, new[] { job.GetJobParams(broadcast.Value) });

        var context = new ProgpowWorkerContext();
        context.Init(1, null, clock);
        var connection = new StratumConnection(NLog.LogManager.GetCurrentClassLogger(),
            streams, clock, "progpow-subscribe", false);
        connection.SetContext(context);
        var request = new Timestamped<JsonRpcRequest>(
            new JsonRpcRequest(BitcoinStratumMethods.Subscribe, new[] { "test-miner" }, 1), DateTimeOffset.UtcNow);
        await (Task) FindMethod(typeof(ProgpowPool), "OnSubscribeAsync",
            typeof(StratumConnection), typeof(Timestamped<JsonRpcRequest>)).Invoke(pool, new object[] { connection, request });

        Assert.True(context.IsSubscribed);
        var queue = Assert.IsType<BufferBlock<object>>(FindField(typeof(StratumConnection), "sendQueue").GetValue(connection));
        Assert.True(queue.TryReceive(out var response));
        Assert.Equal(1, Assert.IsType<JsonRpcResponse<object[]>>(response).Id);
        Assert.True(queue.TryReceive(out var difficulty));
        Assert.Equal(ProgpowStratumMethods.SetDifficulty, Assert.IsType<JsonRpcRequest<object[]>>(difficulty).Method);
        Assert.True(queue.TryReceive(out var message));
        var notification = Assert.IsType<JsonRpcRequest<object>>(message);
        Assert.Equal(ProgpowStratumMethods.MiningNotify, notification.Method);
        var snapshot = Assert.IsType<object[]>(notification.Params);
        Assert.Equal(7, snapshot.Length);
        Assert.True(Assert.IsType<bool>(snapshot[4]));
        Assert.Equal(64, Assert.IsType<string>(snapshot[1]).Length);
        Assert.Equal(new string('0', 64), snapshot[2]);
        Assert.Equal(101u, snapshot[5]);
        Assert.Equal("207fffff", snapshot[6]);
        Assert.False(queue.TryReceive(out _));

        // A subsequent difficulty update still preserves the subscribed work.
        var initialJson = JsonConvert.SerializeObject(snapshot);
        await (Task) FindMethod(typeof(ProgpowPool), "OnVarDiffUpdateAsync",
            typeof(StratumConnection), typeof(double), typeof(CancellationToken))
            .Invoke(pool, new object[] { connection, 2d, CancellationToken.None });
        Assert.True(queue.TryReceive(out _));
        Assert.True(queue.TryReceive(out var update));
        var updated = Assert.IsType<object[]>(Assert.IsType<JsonRpcRequest<object>>(update).Params);
        Assert.False(Assert.IsType<bool>(updated[4]));
        Assert.NotSame(snapshot, updated);
        Assert.Equal(initialJson, JsonConvert.SerializeObject(snapshot));
        Assert.False(queue.TryReceive(out _));
    }

    private PoolBase CreatePool(string family, IComponentContext scope, IMasterClock clock,
        RecyclableMemoryStreamManager streams, NicehashService nicehash)
    {
        var cf = Substitute.For<IConnectionFactory>();
        var stats = Substitute.For<IStatsRepository>();
        var mapper = container.Resolve<IMapper>();
        var bus = Substitute.For<IMessageBus>();
        // Explicit construction makes constructor changes compile-time failures.
        PoolBase pool = family switch
        {
            "equihash" or "verus" => new EquihashPool(scope, jsonSerializerSettings, cf, stats, mapper, clock, bus, streams, nicehash),
            "xelis" => new XelisPool(scope, jsonSerializerSettings, cf, stats, mapper, clock, bus, streams, nicehash),
            "warthog" => new WarthogPool(scope, jsonSerializerSettings, cf, stats, mapper, clock, bus, streams, nicehash),
            "ergo" => new ErgoPool(scope, jsonSerializerSettings, cf, stats, mapper, clock, bus, streams, nicehash),
            "satoshicash" => new SatoshicashPool(scope, jsonSerializerSettings, cf, stats, mapper, clock, bus, streams, nicehash),
            "progpow" => new ProgpowPool(scope, jsonSerializerSettings, cf, stats, mapper, clock, bus, streams, nicehash),
            _ => throw new ArgumentException($"Unknown pool fixture {family}", nameof(family)),
        };
        FindField(typeof(StratumServer), "logger").SetValue(pool, NLog.LogManager.GetCurrentClassLogger());
        return pool;
    }

    private static MethodInfo FindMethod(Type type, string name, params Type[] parameters) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, parameters, null) ??
        throw new InvalidOperationException($"Test fixture method {type.Name}.{name} with the expected signature no longer exists");

    private static PropertyInfo FindProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
        throw new InvalidOperationException($"Test fixture property {type.Name}.{name} no longer exists");

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
