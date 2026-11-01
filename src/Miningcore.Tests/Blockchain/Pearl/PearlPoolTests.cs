using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reactive;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IO;
using Miningcore.Blockchain.Pearl;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

// Pearl's named-object, per-session header protocol has its own concrete-pool
// assignment contract. The generic family matrix assumes positional Stratum.
public class PearlPoolTests : TestBase
{
    internal static readonly Type CoveredPool = typeof(PearlPool);

    [Fact]
    public async Task ConcretePoolAuthorizesPublishesImmutableTargetsAndSerializesReplacement()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var scope = container.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(Substitute.For<IBlockRepository>());
            builder.RegisterInstance(Substitute.For<IShareRepository>());
        });
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var bus = Substitute.For<IMessageBus>();
        var logger = LogManager.CreateNullLogger();
        var config = new PoolConfig {Id = "pearl-test", Address = PearlWorkTests.Address,
            Template = new PearlCoinTemplate {Name = "Pearl", Symbol = "PRL", Family = CoinFamily.Pearl},
            EnableInternalStratum = true, Ports = new Dictionary<int, PoolEndpoint> {{19503, new() {Difficulty = 1}}},
            Daemons = new[] {new DaemonEndpointConfig {Host = "127.0.0.1", Port = 1}}};
        var manager = new PearlJobManager(config, PearlNetwork.Regtest, jsonSerializerSettings, bus, logger);
        using var stream = GetType().Assembly.GetManifestResourceStream("Miningcore.Tests.Fixtures.Pearl.regtest-template.json");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var template = JsonConvert.DeserializeObject<PearlBlockTemplate>(json);
        Set(manager, "current", new PearlJobManager.Snapshot("fixture", template.PreviousBlockhash, json,
            TimeProvider.System.GetTimestamp(), template.Height, 1));
        var pool = new PearlPool(scope, jsonSerializerSettings, Substitute.For<IConnectionFactory>(),
            Substitute.For<IStatsRepository>(), container.Resolve<IMapper>(), clock, bus,
            new RecyclableMemoryStreamManager(), new NicehashService(Substitute.For<IHttpClientFactory>(), cache));
        pool.Configure(config, new ClusterConfig());
        Set(pool, "manager", manager);
        Set(pool, "config", new PearlPoolConfig());
        Set(pool, "network", PearlNetwork.Regtest);
        Set(pool, "logger", logger);
        var worker = new PearlWorkerContext();
        worker.Init(1, null, clock);
        var connection = new StratumConnection(logger, new RecyclableMemoryStreamManager(), clock, "pearl", false);
        connection.SetContext(worker);
        var messages = new List<JObject>();
        connection.SendMessageOverride = (value, _) =>
        {
            messages.Add(JObject.FromObject(value, JsonSerializer.Create(jsonSerializerSettings)));
            return Task.CompletedTask;
        };
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint) listener.LocalEndpoint);
        using var socket = await listener.AcceptSocketAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispatch = connection.DispatchAsync(socket, stop.Token,
            new StratumEndpoint((IPEndPoint) listener.LocalEndpoint, new PoolEndpoint()),
            (IPEndPoint) socket.RemoteEndPoint, null, (_, _, _) => Task.CompletedTask, _ => { }, (_, _) => { });
        async Task Flush(int count)
        {
            while(messages.Count < count) await Task.Delay(10, stop.Token);
        }
        Task Authorize(string wallet) => Invoke(pool, "OnRequestAsync", connection,
            new Timestamped<JsonRpcRequest>(new JsonRpcRequest {Id = 1, Method = "mining.authorize",
                Params = new JObject {["wallet"] = wallet, ["agent"] = "test", ["type"] = "v2"}}, clock.Now), CancellationToken.None);
        await Authorize("invalid");
        await Flush(1);
        Assert.False(worker.IsAuthorized);
        Assert.NotNull(messages[0]["error"]);
        messages.Clear();
        await Invoke(pool, "OnRequestAsync", connection, new Timestamped<JsonRpcRequest>(
            new JsonRpcRequest {Id = 2, Method = "mining.submit", Params = new JObject()}, clock.Now), CancellationToken.None);
        await Flush(1);
        Assert.NotNull(messages[0]["error"]);
        Assert.False(worker.IsAuthorized);
        messages.Clear();
        await Authorize(PearlWorkTests.Address + ".rig_1");
        await Flush(2);
        Assert.True(worker.IsAuthorized);
        Assert.Equal("rig_1", worker.Worker);
        Assert.Equal("mining.notify", messages[0].Value<string>("method"));
        Assert.True(messages[1].Value<bool>("result"));
        var original = (JObject) messages[0]["params"];
        var first = worker.Find(original.Value<string>("job_id"), template.PreviousBlockhash, 120, 30);
        Assert.Equal(76 * 2, original.Value<string>("header").Length);
        Assert.Equal(PearlWork.Target(1), new BigInteger(Convert.FromHexString(original.Value<string>("target")), true, true));

        // A held assignment gate must keep a target replacement off the wire.
        await worker.AssignmentGate.WaitAsync();
        var replace = Invoke(pool, "RunAssignmentAsync", connection,
            (Func<Task>) (() => Invoke(pool, "OnVarDiffUpdateAsync", connection, 2d, CancellationToken.None)), CancellationToken.None, false);
        Assert.False(replace.IsCompleted);
        Assert.Equal(2, messages.Count);
        worker.AssignmentGate.Release();
        await replace.WaitAsync(TimeSpan.FromSeconds(5));
        await Flush(3);
        var replacement = (JObject) messages[2]["params"];
        Assert.NotEqual(original.Value<string>("job_id"), replacement.Value<string>("job_id"));
        Assert.NotEqual(original.Value<string>("header"), replacement.Value<string>("header"));
        Assert.Equal(PearlWork.Target(2), new BigInteger(Convert.FromHexString(replacement.Value<string>("target")), true, true));
        Assert.Equal(1, first.Difficulty);
        Assert.Equal(PearlWork.Target(1), first.Target);
        Assert.Same(first, worker.Find(first.Id, template.PreviousBlockhash, 120, 30));
        Assert.Throws<StratumException>(() => worker.Find(first.Id, template.PreviousBlockhash, 120, 0));
        await Authorize(PearlWorkTests.Address);
        await Flush(4);
        Assert.Equal(4, messages.Count);
        Assert.NotNull(messages[3]["error"]);
        // A missing native service is not a bad miner proof, and does not consume replay identity.
        config.Ports[connection.LocalEndpoint.Port] = new PoolEndpoint {Difficulty = 1, DifficultyGraceSeconds = 30};
        Set(pool, "verifier", new PearlProofVerifier(Path.Combine(Path.GetTempPath(), "missing-pearl-" + Guid.NewGuid().ToString("N"))));
        var invalidBefore = worker.Stats.InvalidShares;
        await Invoke(pool, "OnRequestAsync", connection, new Timestamped<JsonRpcRequest>(
            new JsonRpcRequest {Id = 3, Method = "mining.submit", Params = new JObject
            {
                ["job_id"] = first.Id,
                ["plain_proof"] = Convert.ToBase64String(PearlPlainProofTests.RealProof())
            }}, clock.Now), CancellationToken.None);
        await Flush(5);
        Assert.NotNull(messages[4]["error"]);
        Assert.Equal(invalidBefore, worker.Stats.InvalidShares);
        Assert.Empty(first.Attempts);
        Set(manager, "current", null);
        await Invoke(pool, "SendJobAsync", connection);
        Assert.Equal(5, messages.Count);
        stop.Cancel();
        await dispatch;
    }

    private static Task Invoke(object target, string name, params object[] arguments)
    {
        for(var type = target.GetType(); type != null; type = type.BaseType)
        {
            var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if(method != null) return (Task) method.Invoke(target, arguments);
        }
        throw new MissingMethodException(name);
    }
    private static void Set(object target, string name, object value)
    {
        for(var type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if(field != null) {field.SetValue(target, value); return;}
        }
        throw new MissingFieldException(name);
    }
}
