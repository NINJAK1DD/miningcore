using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reactive;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Alephium;
using Miningcore.Blockchain.Beam;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Conceal;
using Miningcore.Blockchain.Cryptonote;
using Miningcore.Blockchain.Ergo;
using Miningcore.Blockchain.Ethereum;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Blockchain.Zano;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Progpow;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain;
using Miningcore.Tests.Util;
using Miningcore.Time;
using Miningcore.VarDiff;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Mining;

// Exercise concrete pools, their real request handlers and their real broadcast /
// difficulty hooks. Only daemon responses and ready job templates are fixtures;
// the synchronization, protocol mutations, publication and connection are real.
public class PoolFamilyAssignmentTests : TestBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string[] Families = { "Alephium", "Beam", "Bitcoin", "Conceal", "Cryptonote",
        "Equihash", "Ergo", "Ethereum", "Handshake", "Kaspa", "Nexa", "Progpow", "Satoshicash", "Warthog", "Xelis", "Zano" };
    private static bool NeedsNative(string family) => family is "Conceal" or "Cryptonote" or "Zano";
    public static IEnumerable<object[]> ManagedFamilies => Cases(false);
    public static IEnumerable<object[]> NativeFamilies => Cases(true);
    private static IEnumerable<object[]> Cases(bool native) => Families.Where(f => NeedsNative(f) == native)
        .SelectMany(f => new[] { new object[] { f, false }, new object[] { f, true } });

    [Theory]
    [MemberData(nameof(ManagedFamilies))]
    public Task ConcretePool_AuthorizationIdleAndBroadcastCompleteUnderContention(string family, bool idleOwnsGate) =>
        Race(family, idleOwnsGate);

    [LinuxNativeTheory]
    [MemberData(nameof(NativeFamilies))]
    public Task NativeConcretePool_AuthorizationIdleAndBroadcastCompleteUnderContention(string family, bool idleOwnsGate) =>
        Race(family, idleOwnsGate);

    [Fact]
    public Task Ethereum_AuthorizationCancellationDoesNotCommitQueuedDifficulty() => CanceledAuthorization("Ethereum");

    [LinuxNativeTheory]
    [InlineData("Conceal")]
    [InlineData("Cryptonote")]
    [InlineData("Zano")]
    public Task NativePool_AuthorizationCancellationDoesNotCommitQueuedDifficulty(string family) => CanceledAuthorization(family);

    private async Task CanceledAuthorization(string family)
    {
        await using var fixture = await Fixture.Create(this, family);
        var worker = fixture.Connection.Context;
        var original = worker.VarDiff;
        using var cancel = new CancellationTokenSource();
        await worker.AssignmentGate.WaitAsync();
        try
        {
            var authorize = fixture.Authorize(cancel.Token);
            Assert.False(authorize.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorize.WaitAsync(Timeout));
            Assert.Same(original, worker.VarDiff);
            Assert.Equal(10, worker.Difficulty);
            Assert.False(worker.HasPendingDifficulty);
        }
        finally { worker.AssignmentGate.Release(); }
        await fixture.Flush();
        Assert.Equal(1, worker.AssignmentGate.CurrentCount);
        Assert.True(fixture.Connection.IsAlive);
        Assert.Empty(fixture.Errors.Logs);
    }

    private async Task Race(string family, bool idleOwnsGate)
    {
        await using var fixture = await Fixture.Create(this, family);
        var worker = fixture.Connection.Context;
        Task idle;
        if(idleOwnsGate)
        {
            fixture.Time.Block = true;
            idle = Task.Run(fixture.Idle);
            await fixture.Time.Entered.Task.WaitAsync(Timeout);
        }
        else
        {
            await worker.AssignmentGate.WaitAsync();
            idle = fixture.Idle();
        }
        Task authorize = null, broadcast = null;
        try
        {
            authorize = fixture.Authorize();
            broadcast = fixture.Broadcast();
            if(!idleOwnsGate)
            {
                await idle.WaitAsync(Timeout); // Busy idle skips, never joins the queue.
                Assert.False(authorize.IsCompleted);
                Assert.Null(worker.VarDiff.LastUpdate);
            }
        }
        finally
        {
            if(idleOwnsGate)
            {
                fixture.Time.Block = false;
                fixture.Time.Release.Set();
            }
            else
                worker.AssignmentGate.Release();
        }
        await Task.WhenAll(idle, authorize, broadcast).WaitAsync(Timeout);
        await fixture.Flush();
        Assert.True(worker.IsAuthorized);
        Assert.Equal(55, worker.Difficulty);
        Assert.Null(worker.VarDiff);
        Assert.False(worker.HasPendingDifficulty);
        Assert.False(worker.ApplyPendingDifficulty());
        Assert.True(fixture.Connection.IsAlive);
        Assert.False(fixture.Connection.IsDisconnectRequested);
        Assert.Equal(1, worker.AssignmentGate.CurrentCount);
        Assert.Empty(fixture.Errors.Logs); // Broadcast Guard must not hide a failure.
        Assert.True(fixture.Messages.Count >= 2);
        await fixture.Idle().WaitAsync(Timeout);
        Assert.Equal(55, worker.Difficulty);
    }

    [Fact]
    public void ConcreteFamilyMatrix_CoversEveryGenericCoinFamilyPool()
    {
        var expected = typeof(PoolBase).Assembly.GetTypes().Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(PoolBase)) &&
            t.GetCustomAttribute<CoinFamilyAttribute>(inherit: false) != null &&
            t.Name is not ("BitcoinBlake2bPool" or "MergedMiningBitcoinPool"))
            .Select(t => t.Name[..^4]).OrderBy(x => x);
        Assert.Equal(expected, Families.OrderBy(x => x));
    }

    private sealed class LinuxNativeTheoryAttribute : TheoryAttribute
    {
        public LinuxNativeTheoryAttribute()
        {
            if(!OperatingSystem.IsLinux())
                Skip = "Native address/blob fixtures require the Linux test lab";
        }
    }

    private sealed class TimestampProvider : TimeProvider
    {
        internal volatile bool Block;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal long Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            if(Block)
            {
                Entered.TrySetResult();
                if(!Release.Wait(Timeout))
                    throw new TimeoutException("Concrete pool idle calculation did not release");
            }
            return Timestamp;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private ILifetimeScope scope;
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        private readonly CancellationTokenSource stop = new();
        private readonly TcpClient client = new(AddressFamily.InterNetwork);
        private Task dispatch;
        private readonly DaemonStub daemon = new();
        private readonly LogFactory logs = new();
        private string family;
        private object job;
        private readonly TaskCompletionSource flushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TimestampProvider Time = new();
        internal readonly NLog.Targets.MemoryTarget Errors = new() { Layout = "${level}|${message}" };
        internal readonly List<object> Messages = new();
        internal PoolBase Pool { get; private set; }
        internal StratumConnection Connection { get; private set; }

        internal static async Task<Fixture> Create(PoolFamilyAssignmentTests test, string family)
        {
            var fixture = new Fixture();
            try
            {
                fixture.Initialize(test, family);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private void Initialize(PoolFamilyAssignmentTests test, string family)
        {
            this.family = family;
            var clock = new MockMasterClock { CurrentTime = DateTime.UnixEpoch.AddDays(20000) };
            var streams = new RecyclableMemoryStreamManager();
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new RestStub()));
            scope = test.container.BeginLifetimeScope(b =>
            {
                b.RegisterInstance(Substitute.For<IBlockRepository>());
                b.RegisterInstance(Substitute.For<IShareRepository>());
                b.RegisterInstance(factory);
            });
            var extraNonce = Substitute.For<IExtraNonceProvider>();
            extraNonce.Next().Returns("00000001");
            extraNonce.ByteSize.Returns(4);
            var managerType = typeof(PoolBase).Assembly.GetType($"Miningcore.Blockchain.{family}.{family}JobManager", true);
            var constructor = managerType.GetConstructors().Single();
            var manager = constructor.Invoke(constructor.GetParameters().Select(p =>
                p.ParameterType == typeof(IComponentContext) ? (object) scope :
                p.ParameterType == typeof(IMasterClock) ? clock :
                p.ParameterType == typeof(IExtraNonceProvider) ? extraNonce :
                p.ParameterType == typeof(IHttpClientFactory) ? factory : p.HasDefaultValue ? p.DefaultValue : scope.Resolve(p.ParameterType)).ToArray());
            var poolType = typeof(PoolBase).Assembly.GetType($"Miningcore.Blockchain.{family}.{family}Pool", true);
            Pool = (PoolBase) Activator.CreateInstance(poolType, scope, test.jsonSerializerSettings,
                Substitute.For<IConnectionFactory>(), Substitute.For<IStatsRepository>(), test.container.Resolve<IMapper>(),
                clock, Substitute.For<IMessageBus>(), streams, new NicehashService(factory, cache));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint) listener.LocalEndpoint;
            var options = new VarDiffConfig { MinDiff = 1, TargetTime = 10, RetargetTime = 5, VariancePercent = 1 };
            var port = new PoolEndpoint { Difficulty = 10, VarDiff = options };
            var coin = Coin(family);
            var config = new PoolConfig { Id = "concrete-" + family, EnableInternalStratum = true, Template = coin,
                Extra = new Dictionary<string, object> { ["z-address"] = "fixture", ["enableStaticDifficulty"] = true },
                Ports = new Dictionary<int, PoolEndpoint> { [endpoint.Port] = port } };
            var cluster = new ClusterConfig { Banning = new ClusterBanningConfig { BanOnLoginFailure = false } };
            Pool.Configure(config, cluster);
            Pool.VarDiffTimeProvider = Time;
            SetField(Pool, "manager", manager);
            SetField(manager, "poolConfig", config);
            SetField(manager, "clusterConfig", cluster);
            SetField(manager, "coin", coin);
            SetField(manager, "network", "mainnet", optional: true);
            SetField(manager, "maxActiveJobs", 8, optional: true);
            SetField(manager, "PoolNoncePrefix", "00000001", optional: true);
            var logConfig = new NLog.Config.LoggingConfiguration();
            logConfig.AddRule(LogLevel.Error, LogLevel.Fatal, Errors);
            logs.Configuration = logConfig;
            var logger = logs.GetLogger(config.Id);
            SetField(Pool, "logger", logger);
            SetField(manager, "logger", logger);
            var rpc = new RpcClient(new DaemonEndpointConfig { Host = "127.0.0.1", Port = daemon.Port,
                User = "fixture", Password = "fixture" }, test.jsonSerializerSettings, Substitute.For<IMessageBus>(), config.Id);
            if(family is "Beam") SetField(manager, "walletRpc", rpc);
            else if(family is "Alephium")
            {
                var api = Substitute.For<AlephiumClient>("http://fixture", new HttpClient(new RestStub()));
                api.GetAddressesAddressGroupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new Group { Group1 = 0 });
                SetField(manager, "rpc", api);
            }
            else if(family is "Ergo")
            {
                var api = Substitute.For<ErgoClient>("http://fixture", new HttpClient(new RestStub()));
                api.CheckAddressValidityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AddressValidity { IsValid = true });
                SetField(manager, "rpc", api);
            }
            else if(family is "Warthog")
                SetField(manager, "restClient", new Miningcore.Rest.SimpleRestClient(factory, "http://fixture"));
            else if(family is not ("Ethereum" or "Kaspa" or "Conceal" or "Cryptonote" or "Zano"))
                SetField(manager, "rpc", rpc);
            job = ReadyJob(family, config, cluster);
            SetField(manager, "currentJob", job);
            if(family == "Equihash")
                SetProperty(manager, "ChainConfig", ((EquihashCoinTemplate) coin).GetNetwork(NBitcoin.ChainName.Mainnet));
            client.Connect(endpoint);
            var socket = listener.AcceptSocket();
            Connection = new StratumConnection(logger, streams, clock, "concrete", false);
            Invoke(Pool, "OnConnect", Connection, endpoint);
            var worker = Connection.Context;
            worker.IsSubscribed = worker.IsAuthorized = true;
            worker.UserAgent = "fixture";
            SetProperty(worker, "ExtraNonce1", "00000001", optional: true);
            SetProperty(worker, "ProtocolVersion", 2, optional: true);
            Invoke(Pool, "RegisterConnection", Connection);
            Connection.SendMessageOverride = (message, _) =>
            {
                Messages.Add(message);
                if(message is JsonRpcRequest<object[]> { Method: "test.assignment.fence" })
                    flushed.TrySetResult();
                return Task.CompletedTask;
            };
            dispatch = Connection.DispatchAsync(socket, stop.Token, new StratumEndpoint(endpoint, port),
                (IPEndPoint) socket.RemoteEndPoint, null, (_, _, _) => Task.CompletedTask, _ => { }, (_, _) => { });
            Assert.Null(VarDiffManager.Update(worker, options, clock));
            Time.Timestamp = 5 * TimeSpan.TicksPerSecond;
        }

        internal Task Idle() => (Task) Invoke(Pool, "UpdateVarDiffAsync", Connection, true, stop.Token);
        internal Task Authorize(CancellationToken? ct = null)
        {
            var address = family switch
            {
                "Ethereum" => "0x" + new string('1', 40),
                "Kaspa" => "kaspa:qzdtdjatlzecrt9u4v22p5vgud6w6ylvemly9df6zpu0gp0yks9xxp24q79pu",
                "Conceal" or "Cryptonote" or "Zano" => "48nhyWcSey31ngSEhV8j8NPm6B8PistCQJBjjDjmTvRSTWYg6iocAw131vE2JPh3ps33vgQDKLrUx3fcErusYWcMJBxpm1d",
                _ => "fixture",
            };
            var login = family is "Conceal" or "Cryptonote";
            var request = new JsonRpcRequest(login ? "login" : family == "Beam" ? "login" : "mining.authorize",
                login ? JObject.FromObject(new { login = address, pass = "d=55", agent = "fixture" }) : (object) new[] { address + ".worker", "d=55" }, 1);
            if(family == "Beam")
                request = JsonConvert.DeserializeObject<JsonRpcRequest>("{\"id\":1,\"method\":\"login\",\"api_key\":\"fixture.worker\",\"pass\":\"d=55\"}");
            if(family == "Xelis")
                request.Params = new[] { address + ".worker", "worker", "d=55" };
            return (Task) Invoke(Pool, login || family == "Beam" ? "OnLoginAsync" : "OnAuthorizeAsync", Connection,
                new Timestamped<JsonRpcRequest>(request, DateTimeOffset.UtcNow), ct ?? stop.Token);
        }
        internal Task Broadcast()
        {
            var method = FindMethod(Pool.GetType(), "OnNewJobAsync");
            if(method.GetParameters().Length == 0)
                return (Task) method.Invoke(Pool, null);
            object parameters = method.GetParameters()[0].ParameterType == typeof(Unit) ? Unit.Default :
                family == "Alephium" ? ((AlephiumJob) job).GetJobParams() :
                family == "Progpow" ? ((Miningcore.Blockchain.Progpow.ProgpowJob) job).GetJobParams(false) : new object[] { "ready", false };
            return (Task) method.Invoke(Pool, new[] { parameters });
        }
        internal async Task Flush()
        {
            await Connection.NotifyAsync("test.assignment.fence", Array.Empty<object>());
            await flushed.Task.WaitAsync(Timeout);
        }
        public async ValueTask DisposeAsync()
        {
            Time.Block = false;
            Time.Release.Set();
            stop.Cancel();
            client.Dispose();
            try
            {
                if(dispatch != null)
                    await dispatch.WaitAsync(Timeout);
            }
            finally
            {
                try { await daemon.DisposeAsync(); }
                finally
                {
                    Time.Release.Dispose();
                    stop.Dispose();
                    scope?.Dispose();
                    cache.Dispose();
                    logs.Dispose();
                }
            }
        }
    }

    private static CoinTemplate Coin(string family)
    {
        var name = family switch { "Progpow" => "ravencoin", "Cryptonote" => "monero", "Equihash" => "zcash", _ => family.ToLowerInvariant() };
        var original = ModuleInitializer.CoinTemplates[name];
        var coin = (CoinTemplate) typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(original, null);
        // Native authorization fixtures use the known-valid prefix-18 address.
        foreach(var property in coin.GetType().GetProperties().Where(p => p.Name.StartsWith("AddressPrefix") && p.CanWrite))
            property.SetValue(coin, Convert.ChangeType(18, property.PropertyType));
        return coin;
    }
    private static object ReadyJob(string family, PoolConfig config, ClusterConfig cluster)
    {
        if(family == "Progpow") return JobNotificationSnapshotTests.CreateProgpowJob("progpow");
        if(family is "Equihash" or "Xelis" or "Warthog" or "Ergo" or "Satoshicash")
        {
            var job = JobNotificationSnapshotTests.CreateArrayJob(family.ToLowerInvariant()).Job;
            SetProperty(job, "JobId", "ready", optional: true);
            return job;
        }
        if(family == "Ethereum") return new EthereumJob("ready", new EthereumBlockTemplate
        { Target = new string('f', 64), Header = "0x" + new string('1', 64), Seed = "0x" + new string('2', 64), Height = 101 }, LogManager.GetCurrentClassLogger(), null);
        if(family == "Alephium")
        {
            var job = new AlephiumJob();
            job.Init(new AlephiumBlockTemplate { JobId = "ready", TargetBlob = new string('f', 64), HeaderBlob = "aabb", Height = 101 });
            return job;
        }
        if(family == "Beam")
        {
            var job = new BeamJob();
            SetProperty(job, "JobId", "ready");
            SetProperty(job, "BlockTemplate", new Miningcore.Blockchain.Beam.DaemonResponses.BeamBlockTemplate { Height = 101, Input = "aabb" });
            return job;
        }
        if(NeedsNative(family)) return NativeJob(family, config, cluster);
        var type = typeof(PoolBase).Assembly.GetType($"Miningcore.Blockchain.{family}.{family}Job", true);
        var result = RuntimeHelpers.GetUninitializedObject(type);
        SetProperty(result, "JobId", "ready");
        SetField(result, "jobParams", family == "Kaspa" ? new object[] { "ready", "aabb", new ulong[4], 123L } :
            new object[] { "ready", "prev", "prefix", "suffix", Array.Empty<string>(), "version", "bits", "time", false });
        return result;
    }

    private static object NativeJob(string family, PoolConfig config, ClusterConfig cluster)
    {
        var offset = NativeBlob.IndexOf("020800000000012e7f76", StringComparison.Ordinal) / 2 + 2;
        if(family == "Conceal")
            return new ConcealJob(new Miningcore.Blockchain.Conceal.DaemonResponses.GetBlockTemplateResponse
                { Blob = NativeBlob, Height = 101, ReservedOffset = offset }, new byte[3], "ready",
                (ConcealCoinTemplate) config.Template, config, cluster, "prev");
        if(family == "Cryptonote")
            return new CryptonoteJob(new Miningcore.Blockchain.Cryptonote.DaemonResponses.GetBlockTemplateResponse
                { Blob = NativeBlob, Height = 101, ReservedOffset = offset }, new byte[3], "ready",
                (CryptonoteCoinTemplate) config.Template, config, cluster, "prev", "fixture");
        var job = Substitute.For<ZanoJob>(new Miningcore.Blockchain.Zano.DaemonResponses.GetBlockTemplateResponse
            { Blob = NativeBlob, Height = 101, SeedHash = new string('0', 64) }, new byte[3],
            (ZanoCoinTemplate) config.Template, config, cluster, "prev", Substitute.For<IProgpowCache>());
        job.PrepareWorkerJob(Arg.Any<double>()).Returns(c => new ZanoWorkerJob("aabb", c.Arg<double>())
            { Height = "0x00000065", SeedHash = new string('0', 64), Target = new string('f', 64) });
        return job;
    }

    // Known-valid 330-byte CryptoNote blob, shared with the native conversion KAT.
    private const string NativeBlob = "0106e5b3afd505583cf50bcc743d04d831d2b119dc94ad88679e359076ee3f18d258ee138b3b421c0300a401d90101ff9d0106d6d6a88702023c62e43372a58cb588147e20be53a27083f5c522f33c722b082ab7518c48cda280b4c4c32102609ec96e2499ee267d70efefc49f26e330526d3ef455314b7b5ba268a6045f8c80c0fc82aa0202fe5cc0fa56c4277d1a47827edce4725571529d57f33c73ada481ef84c323f30a8090cad2c60e02d88bf5e72a611c8b8464ce29e3b1adbfe1ae163886d9150fe511171cada98fcb80e08d84ddcb0102441915aaf9fbaf70ff454c701a6ae2bd59bb94dc0b888bf7e5d06274ee9238ca80c0caf384a302024078526e2132def44bde2806242652f5944e632f7d94290dd6ee5dda1929f5ee2b016e29f25f07ec2a8df59f0e118a6c9a4b769b745dc0c729071f6e0399d2585745020800000000012e7f7600";

    private static MethodInfo FindMethod(Type type, string name) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing fixture method {type.Name}.{name}");
    private static object Invoke(object target, string name, params object[] args) => FindMethod(target.GetType(), name).Invoke(target, args);
    private static void SetProperty(object target, string name, object value, bool optional = false)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if(property == null && optional) return;
        Assert.NotNull(property);
        property.SetValue(target, value);
    }
    private static void SetField(object target, string name, object value, bool optional = false)
    {
        for(var type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if(field == null) continue;
            if(optional && !field.FieldType.IsInstanceOfType(value)) return;
            field.SetValue(target, value);
            return;
        }
        if(!optional) throw new InvalidOperationException($"Missing fixture field {target.GetType().Name}.{name}");
    }

    private sealed class RestStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0}") });
    }
    private sealed class DaemonStub : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task serve;
        internal int Port => ((IPEndPoint) listener.LocalEndpoint).Port;
        internal DaemonStub() { listener.Start(); serve = Serve(); }
        private async Task Serve()
        {
            try
            {
                while(!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    var stream = client.GetStream();
                    using var reader = new System.IO.StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var length = 0;
                    while(await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                        if(line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..]);
                    var data = new char[length];
                    await reader.ReadBlockAsync(data, stop.Token);
                    var id = JObject.Parse(new string(data))["id"];
                    var response = Encoding.UTF8.GetBytes(new JObject { ["id"] = id, ["result"] = new JObject
                        { ["isvalid"] = true, ["is_valid"] = true, ["type"] = "regular", ["address"] = "fixture" }, ["error"] = null }.ToString(Formatting.None));
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, stop.Token);
                    await stream.WriteAsync(response, stop.Token);
                }
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await serve.WaitAsync(Timeout); stop.Dispose(); }
    }
}
