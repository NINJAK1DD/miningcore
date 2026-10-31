using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Beam;
using Miningcore.Blockchain.Conceal;
using Miningcore.Blockchain.Warthog;
using Miningcore.Blockchain.Warthog.DaemonRequests;
using Miningcore.Blockchain.Warthog.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Rest;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Rpc;

[Collection(RpcDiagnosticCollection.Name)]
public class DaemonRestConsumerTests
{
    [Fact]
    public async Task BeamExplorerCategoryUsesExplorerRestEndpoint()
    {
        var requestedPath = "";
        await using var app = await StartServer(context =>
        {
            requestedPath = context.Request.Path;
            return context.Response.WriteAsync("{}");
        });
        using var clients = CreateClients();
        using var container = CreateContainer();
        var manager = new BeamJobManager(container, Substitute.For<IMasterClock>(),
            clients.GetRequiredService<IHttpClientFactory>(), Substitute.For<IMessageBus>(),
            Substitute.For<IExtraNonceProvider>());
        manager.Configure(new PoolConfig
        {
            Id = "beam-rest-test", Template = new BeamCoinTemplate(),
            Daemons = new[]
            {
                new DaemonEndpointConfig { Host = "127.0.0.1", Port = 1 },
                Endpoint(app, BeamConstants.ExplorerDaemonCategory),
            },
        }, new ClusterConfig());

        await RestClient(manager, "explorerRestClient").Get<object>(BeamExplorerCommands.GetStatus,
            CancellationToken.None);
        Assert.Equal("/" + BeamExplorerCommands.GetStatus.TrimStart('/'), requestedPath);
    }

    [Fact]
    public async Task BeamExplorerFailureIdentifiesPoolAndFullDaemonIndexWithoutUrl()
    {
        await using var app = await StartServer(context =>
        {
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = "http://127.0.0.1:1/capture";
            return Task.CompletedTask;
        });
        using var warnings = new PoolLogCapture("beam-rest-test");
        var services = new ServiceCollection();
        Miningcore.Program.AddHttpClients(services);
        using var clients = services.BuildServiceProvider();
        using var container = CreateContainer();
        var manager = new BeamJobManager(container, Substitute.For<IMasterClock>(),
            clients.GetRequiredService<IHttpClientFactory>(), Substitute.For<IMessageBus>(),
            Substitute.For<IExtraNonceProvider>());
        manager.Configure(new PoolConfig
        {
            Id = "beam-rest-test", Template = new BeamCoinTemplate(),
            Daemons = new[]
            {
                new DaemonEndpointConfig { Host = "127.0.0.1", Port = 1 },
                Endpoint(app, BeamConstants.ExplorerDaemonCategory),
            },
        }, new ClusterConfig());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            RestClient(manager, "explorerRestClient").Get<object>(BeamExplorerCommands.GetStatus,
                CancellationToken.None));
        Assert.Equal(HttpStatusCode.TemporaryRedirect, ex.StatusCode);
        var record = JObject.Parse(Assert.Single(warnings.Warnings)["RPC diagnostic ".Length..]);
        Assert.Equal("Rest", record.Value<string>("transport"));
        Assert.Equal("beam-rest-test", record.Value<string>("poolId"));
        Assert.Equal(2, record.Value<int>("endpointIndex"));
        Assert.Equal("redirect-refused", record.Value<string>("failure"));
        Assert.Equal(JTokenType.Null, record["method"].Type);
        Assert.DoesNotContain("127.0.0.1", warnings.Warnings.Single());
        Assert.Contains(warnings.Warnings.Single(), warnings.ReadPoolLog());
        Assert.Contains(warnings.Warnings.Single(), warnings.ReadMainLog());
    }

    [Fact]
    public async Task ConcealJsonRpcPathDoesNotPrefixRestGetInfo()
    {
        var requestedPath = "";
        await using var app = await StartServer(context =>
        {
            requestedPath = context.Request.Path;
            return context.Response.WriteAsync("{}");
        });
        using var clients = CreateClients();
        using var container = CreateContainer();
        var endpoint = Endpoint(app);
        endpoint.HttpPath = "/json_rpc";
        var manager = new ConcealJobManager(container, Substitute.For<IMasterClock>(),
            clients.GetRequiredService<IHttpClientFactory>(), Substitute.For<IMessageBus>());
        manager.Configure(new PoolConfig
        {
            Id = "conceal-rest-test", Template = new ConcealCoinTemplate(),
            Daemons = new[] { endpoint },
        }, new ClusterConfig());

        await RestClient(manager, "restClient").Get<object>(ConcealConstants.DaemonRpcGetInfoLocation,
            CancellationToken.None);
        Assert.Equal("/" + ConcealConstants.DaemonRpcGetInfoLocation, requestedPath);
        Assert.Equal("/json_rpc", endpoint.HttpPath);
    }

    [Fact]
    public async Task WarthogPayoutPostRefusesCrossOriginRedirect()
    {
        var replayed = 0;
        await using var target = await StartServer(context =>
        {
            Interlocked.Increment(ref replayed);
            return context.Response.WriteAsync("{}");
        });
        var requestedPaths = new ConcurrentQueue<string>();
        await using var daemon = await StartServer(context =>
        {
            requestedPaths.Enqueue($"{context.Request.Method} {context.Request.Path}");
            if(context.Request.Method == "POST")
            {
                context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                context.Response.Headers.Location = Address(target) + "/capture";
                return Task.CompletedTask;
            }
            return context.Response.WriteAsync("{\"code\":0,\"data\":{\"testnet\":true}}");
        });
        using var clients = CreateClients();
        using var container = CreateContainer();
        using var payout = new ExposedWarthogPayoutHandler(container,
            clients.GetRequiredService<IHttpClientFactory>());
        const string address = "751e76e8199196d454941c45d1b3a323f1433bd6ffded884";
        await payout.ConfigureAsync(new ClusterConfig(), new PoolConfig
        {
            Id = "wart-rest-test", Address = address,
            Template = new WarthogCoinTemplate { Symbol = "WART" },
            Daemons = new[] { Endpoint(daemon) },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                Extra = new Dictionary<string, object>
                {
                    ["walletPrivateKey"] = new string('0', 63) + '1',
                },
            },
        }, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => payout.Submit());
        Assert.Equal(HttpStatusCode.TemporaryRedirect, ex.StatusCode);
        Assert.Equal(0, Volatile.Read(ref replayed));
        Assert.Contains(requestedPaths, path => path == "POST " + WarthogCommands.SendTransaction);
        Assert.Contains(requestedPaths, path => path.StartsWith("GET /chain/mine/", StringComparison.Ordinal));
    }

    private static SimpleRestClient RestClient(object manager, string fieldName) =>
        (SimpleRestClient) manager.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;

    private static ServiceProvider CreateClients()
    {
        var services = new ServiceCollection();
        Miningcore.Program.AddHttpClients(services);
        return services.BuildServiceProvider();
    }

    private static IContainer CreateContainer()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        return builder.Build();
    }

    private static DaemonEndpointConfig Endpoint(WebApplication app, string category = null)
    {
        var uri = new Uri(Address(app));
        return new DaemonEndpointConfig { Host = uri.Host, Port = uri.Port, Category = category };
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private static async Task<WebApplication> StartServer(RequestDelegate handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync();
        return app;
    }

    private sealed class ExposedWarthogPayoutHandler : WarthogPayoutHandler
    {
        public ExposedWarthogPayoutHandler(IComponentContext context, IHttpClientFactory factory) :
            base(context, Substitute.For<IConnectionFactory>(), Substitute.For<IMapper>(),
                Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
                Substitute.For<IBalanceRepository>(), Substitute.For<IPaymentRepository>(),
                Substitute.For<IMasterClock>(), factory, Substitute.For<IMessageBus>()) { }

        public Task<WarthogSendTransactionResponse> Submit() =>
            SendPayoutTransactionAsync(new WarthogSendTransactionRequest
            {
                ToAddress = "synthetic-address", Signature = "synthetic-signature",
            }, CancellationToken.None);
    }

    private sealed class PoolLogCapture : IDisposable
    {
        private readonly NLog.Config.LoggingConfiguration previous = NLog.LogManager.Configuration;
        private readonly NLog.Targets.MemoryTarget target = new("pool-capture") { Layout = "${message}" };
        private readonly string directory = Path.Combine(Path.GetTempPath(), "miningcore-rest-log-" + Guid.NewGuid().ToString("N"));
        public string PoolLogPath { get; }
        public string MainLogPath => Path.Combine(directory, "main.log");

        public PoolLogCapture(string poolId)
        {
            PoolLogPath = Path.Combine(directory, poolId + ".log");
            Directory.CreateDirectory(directory);
            var config = Miningcore.Program.CreateLoggingConfiguration(new ClusterLoggingConfig
            {
                Level = "Warn", PerPoolLogFile = true, LogFile = "main.log",
                LogBaseDirectory = directory,
            }, false, new[] { new PoolConfig { Id = poolId } });
            var poolRule = Assert.Single(config.LoggingRules.Where(rule => rule.LoggerNamePattern == poolId));
            config.AddTarget(target);
            poolRule.Targets.Add(target);
            NLog.LogManager.Configuration = config;
        }

        public IList<string> Warnings => target.Logs;

        public string ReadPoolLog()
        {
            NLog.LogManager.Flush();
            NLog.LogManager.Configuration = previous;
            return File.ReadAllText(PoolLogPath);
        }

        public string ReadMainLog() => File.ReadAllText(MainLogPath);

        public void Dispose()
        {
            NLog.LogManager.Configuration = previous;
            target.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
