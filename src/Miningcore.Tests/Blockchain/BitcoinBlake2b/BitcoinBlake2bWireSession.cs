using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

// Real newline-delimited TCP requests drive the production pool dispatcher,
// authorization RPC, per-worker job issuance and share admission. Only the
// database/message-bus sink and background job source are supplied by tests.
internal sealed class BitcoinBlake2bWireSession : IAsyncDisposable
{
    private readonly TcpClient client;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(60));
    private readonly Task dispatch;
    private readonly TestPool pool;
    private readonly ILifetimeScope scope;
    private int requestId;
    private Exception dispatchError;

    internal StratumConnection Connection { get; }
    internal int JobsCreated => pool.JobsCreated;

    internal BitcoinBlake2bWireSession(IComponentContext container, IMasterClock clock,
        PoolConfig config, BitcoinJobManager manager, IMessageBus bus,
        BitcoinBlake2bWireSession sharedPool = null, TimeProvider budgetTimeProvider = null)
    {
        var streams = container.Resolve<RecyclableMemoryStreamManager>();
        scope = ((ILifetimeScope) container).BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(Substitute.For<IBlockRepository>());
            builder.RegisterInstance(Substitute.For<IShareRepository>());
        });
        pool = sharedPool?.pool ?? new TestPool(scope, clock, bus, streams, budgetTimeProvider ?? TimeProvider.System);
        if(sharedPool == null)
        {
            pool.Configure(config, new ClusterConfig());
            pool.SetManager(manager);
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = new StratumEndpoint((IPEndPoint) listener.LocalEndpoint,
            new PoolEndpoint { Difficulty = 1e-9 });
        config.Ports ??= new Dictionary<int, PoolEndpoint>();
        config.Ports[endpoint.IPEndPoint.Port] = new PoolEndpoint { Difficulty = 1e-9 };
        client = new TcpClient(AddressFamily.InterNetwork);
        client.Connect(endpoint.IPEndPoint);
        var socket = listener.AcceptSocket();
        Connection = new StratumConnection(new NLog.NullLogger(NLog.LogManager.LogFactory),
            streams, clock, Guid.NewGuid().ToString("N"), false);
        var context = new BitcoinWorkerContext();
        context.Init(1e-9, null, clock);
        Connection.SetContext(context);
        pool.AddConnection(Connection);
        dispatch = Connection.DispatchAsync(socket, stop.Token, endpoint,
            (IPEndPoint) socket.RemoteEndPoint, null,
            pool.Dispatch,
            _ => { }, (_, error) => dispatchError = error);
        reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 1024, true);
        writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), 1024, true)
            { AutoFlush = true, NewLine = "\n" };
    }

    internal async Task<JObject> RequestAsync(string method, params object[] parameters)
    {
        var id = ++requestId;
        await writer.WriteLineAsync(JsonConvert.SerializeObject(new { id, method, @params = parameters }));
        while(true)
        {
            var response = await ReadAsync();
            if(response["id"]?.Type == JTokenType.Integer && response["id"].Value<int>() == id)
                return response;
        }
    }

    internal async Task<JObject> ReadAsync()
    {
        var line = await reader.ReadLineAsync(stop.Token);
        Assert.False(string.IsNullOrEmpty(line), $"Stratum connection closed before a complete response: {dispatchError}");
        return JObject.Parse(line);
    }

    internal Task SendRequestAsync(string method, params object[] parameters) =>
        writer.WriteLineAsync(JsonConvert.SerializeObject(new { id = ++requestId, method, @params = parameters }));

    internal Task AnnounceJobAsync(object jobParams) => pool.Announce(jobParams);
    internal object CreateJob() => pool.CreateJob(Connection);
    internal Task UpdateVarDiffAsync(double difficulty) => pool.UpdateVarDiff(Connection, difficulty);

    internal async Task AssertDisconnectedAsync()
    {
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(Connection.IsAlive);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        stop.Cancel();
        try { await dispatch.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch(OperationCanceledException) { }
        reader.Dispose();
        writer.Dispose();
        stop.Dispose();
        scope.Dispose();
    }

    private sealed class TestPool : BitcoinBlake2bPool
    {
        internal TestPool(IComponentContext ctx, IMasterClock clock,
            IMessageBus bus, RecyclableMemoryStreamManager streams, TimeProvider budgetTimeProvider) :
            base(ctx, new JsonSerializerSettings(), Substitute.For<IConnectionFactory>(),
                Substitute.For<IStatsRepository>(), AutoMapperFactory.CreateMapper(), clock,
                bus, streams, new NicehashService(Substitute.For<IHttpClientFactory>(),
                    new Microsoft.Extensions.Caching.Memory.MemoryCache(
                        new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())), budgetTimeProvider) { }

        internal int JobsCreated { get; private set; }
        protected override object CreateWorkerJob(StratumConnection connection, bool cleanJob)
        {
            var result = base.CreateWorkerJob(connection, cleanJob);
            JobsCreated++;
            return result;
        }

        internal void SetManager(BitcoinJobManager value) => manager = value;
        internal void AddConnection(StratumConnection value) => RegisterConnection(value);
        internal Task Dispatch(StratumConnection connection, JsonRpcRequest request,
            CancellationToken ct) => OnRequestAsync(connection, request, ct);
        internal Task UpdateVarDiff(StratumConnection connection, double difficulty) =>
            OnVarDiffUpdateAsync(connection, difficulty, CancellationToken.None);
        internal Task Announce(object jobParams) => OnNewJobAsync(jobParams);
        internal object CreateJob(StratumConnection connection) => CreateWorkerJob(connection, false);
    }
}
