using System;
using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miningcore.Api.Controllers;
using Miningcore.Api.Middlewares;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests;

public class AdminApiRouteIntegrationTests
{
    [Fact]
    public async Task ProductionMvcRoutes_EnforceAuthenticationVerbsCorsAndCanonicalAddress()
    {
        await using var running = await StartHostWithRetryAsync();
        using var client = new HttpClient();
        var origin = new Uri($"http://127.0.0.1:{running.Port}");

        using(var unauthenticated = await client.GetAsync(
                  new Uri(origin, "/api/admin/stats/gc")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized,
                unauthenticated.StatusCode);
            Assert.True(unauthenticated.Headers.CacheControl?.NoStore);
        }

        using(var authenticatedGc = CreateAdminRequest(HttpMethod.Get,
                  new Uri(origin, "/api/admin/stats/gc")))
        {
            authenticatedGc.Headers.Add("Origin", "https://dashboard.example");
            using var response = await client.SendAsync(authenticatedGc);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.False(response.Headers.Contains(
                "Access-Control-Allow-Origin"));
        }

        using(var legacyGet = CreateAdminRequest(HttpMethod.Get,
                  new Uri(origin,
                      "/api/admin/payment/processing/enable")))
        using(var response = await client.SendAsync(legacyGet))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using(var enable = CreateAdminRequest(HttpMethod.Put,
                  new Uri(origin,
                      "/api/admin/payment/processing/enable")))
        using(var response = await client.SendAsync(enable))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using(var removedPublicWriter = new HttpRequestMessage(HttpMethod.Post,
                  new Uri(origin,
                      $"/api/pools/{PoolId}/miners/{MixedCaseAddress}/settings")))
        {
            removedPublicWriter.Content = JsonContent(
                "{\"settings\":{\"paymentThreshold\":0.1}}");
            using var response = await client.SendAsync(removedPublicWriter);
            Assert.Equal(HttpStatusCode.NotFound,
                response.StatusCode);
        }

        using(var update = CreateAdminRequest(HttpMethod.Put,
                  new Uri(origin,
                      $"/api/admin/pools/{PoolId}/miners/{MixedCaseAddress}/settings")))
        {
            update.Content = JsonContent("{\"paymentThreshold\":0.1}");
            using var response = await client.SendAsync(update);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using(var readSettings = CreateAdminRequest(HttpMethod.Get,
                  new Uri(origin,
                      $"/api/admin/pools/{PoolId}/miners/{MixedCaseAddress}/settings")))
        using(var response = await client.SendAsync(readSettings))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(running.StoredSettings);
        Assert.Equal(MixedCaseAddress.ToLowerInvariant(),
            running.StoredSettings.Address);
        Assert.Equal(PoolId, running.StoredSettings.PoolId);
        Assert.Equal(0.1m, running.StoredSettings.PaymentThreshold);

        using var unknownPool = CreateAdminRequest(HttpMethod.Get,
            new Uri(origin,
                $"/api/admin/pools/unknown/miners/{MixedCaseAddress}/getbalance"));
        using var unknownPoolResponse = await client.SendAsync(unknownPool);
        Assert.Equal(HttpStatusCode.NotFound,
            unknownPoolResponse.StatusCode);
    }

    [Fact]
    public async Task DisabledEthereumPool_BalanceLookupCanonicalizesAddress()
    {
        await using var running = await StartHostWithRetryAsync(false);
        using var client = new HttpClient();
        var origin = new Uri($"http://127.0.0.1:{running.Port}");

        using var request = CreateAdminRequest(HttpMethod.Get,
            new Uri(origin,
                $"/api/admin/pools/{PoolId}/miners/{MixedCaseAddress}/getbalance"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MixedCaseAddress.ToLowerInvariant(),
            running.LastBalanceAddress);
    }

    [Fact]
    public async Task PublicHelp_CatalogueOmitsAdministrativeRoutes()
    {
        await using var running = await StartHostWithRetryAsync();
        using var client = new HttpClient();
        var origin = new Uri($"http://127.0.0.1:{running.Port}");

        using var response = await client.GetAsync(
            new Uri(origin, "/api/help"));
        var catalogue = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("api/pools", catalogue,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api/admin", catalogue,
            StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage CreateAdminRequest(HttpMethod method,
        Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", AdminToken);
        return request;
    }

    private static StringContent JsonContent(string json) => new(json,
        Encoding.UTF8, "application/json");

    private static async Task<RunningAdminHost> StartHostWithRetryAsync(
        bool poolEnabled = true)
    {
        Exception lastError = null;

        for(var attempt = 0; attempt < ListenerStartAttempts; attempt++)
        {
            var port = GetFreeTcpPort();
            RunningAdminHost running = null;

            try
            {
                running = CreateHost(port, poolEnabled);
                await running.StartAsync();
                return running;
            }
            catch(Exception ex) when(IsAddressInUse(ex))
            {
                lastError = ex;
                if(running != null)
                    await running.DisposeAsync();
            }
        }

        throw new InvalidOperationException(
            "Unable to start the administrative MVC route test after bounded port retries",
            lastError);
    }

    private static RunningAdminHost CreateHost(int port, bool poolEnabled)
    {
        var minerRepo = Substitute.For<IMinerRepository>();
        var paymentRepo = Substitute.For<IPaymentRepository>();
        var balanceRepo = Substitute.For<IBalanceRepository>();
        var statsRepo = Substitute.For<IStatsRepository>();
        var blockRepo = Substitute.For<IBlockRepository>();
        var shareRepo = Substitute.For<IShareRepository>();
        var clock = Substitute.For<IMasterClock>();
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        global::Miningcore.Persistence.Model.MinerSettings storedSettings = null;
        string lastBalanceAddress = null;

        connectionFactory.OpenConnectionAsync().Returns(
            Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(
            transaction);
        minerRepo.UpdateSettingsAsync(connection, transaction,
                Arg.Do<global::Miningcore.Persistence.Model.MinerSettings>(settings =>
                    storedSettings = settings))
            .Returns(Task.CompletedTask);
        minerRepo.GetSettingsAsync(connection, Arg.Any<IDbTransaction>(), PoolId,
                MixedCaseAddress.ToLowerInvariant())
            .Returns(_ => Task.FromResult(storedSettings));
        balanceRepo.GetBalanceAsync(connection, PoolId, Arg.Any<string>())
            .Returns(call =>
            {
                lastBalanceAddress = call.ArgAt<string>(2);
                return Task.FromResult(0.25m);
            });

        var clusterConfig = new ClusterConfig
        {
            Pools = new[]
            {
                new PoolConfig
                {
                    Id = PoolId,
                    Enabled = poolEnabled,
                    PaymentProcessing = new PoolPaymentProcessingConfig
                    {
                        MinimumPayment = 0.01m,
                    },
                    Template = new EthereumCoinTemplate
                    {
                        Family = CoinFamily.Ethereum,
                    },
                },
            },
        };

        var containerBuilder = new ContainerBuilder();
        containerBuilder.RegisterInstance(AutoMapperFactory.CreateMapper())
            .As<IMapper>();
        containerBuilder.RegisterInstance(clusterConfig);
        containerBuilder.RegisterInstance(connectionFactory)
            .As<IConnectionFactory>();
        containerBuilder.RegisterInstance(
            new global::Miningcore.Api.Responses.AdminGcStats());
        containerBuilder.RegisterInstance(minerRepo).As<IMinerRepository>();
        containerBuilder.RegisterInstance(paymentRepo)
            .As<IPaymentRepository>();
        containerBuilder.RegisterInstance(balanceRepo)
            .As<IBalanceRepository>();
        containerBuilder.RegisterInstance(statsRepo).As<IStatsRepository>();
        containerBuilder.RegisterInstance(blockRepo).As<IBlockRepository>();
        containerBuilder.RegisterInstance(shareRepo).As<IShareRepository>();
        containerBuilder.RegisterInstance(clock).As<IMasterClock>();
        containerBuilder.RegisterInstance(
            new ConcurrentDictionary<string, IMiningPool>());
        var container = containerBuilder.Build();
        var controller = new AdminApiController(container);

        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHostDefaults(builder => builder
                .UseKestrel(options => options.Listen(IPAddress.Loopback, port))
                .ConfigureServices(services =>
                {
                    services.AddCors();
                    services.AddMvc(options =>
                        {
                            options.EnableEndpointRouting = false;
                        })
                        .AddApplicationPart(typeof(AdminApiController).Assembly)
                        .AddControllersAsServices();
                    services.AddSingleton(controller);
                    services.AddSingleton(sp => new PoolApiController(container,
                        sp.GetRequiredService<IActionDescriptorCollectionProvider>()));
                })
                .Configure(app =>
                {
                    Program.ConfigureApiPipeline(app,
                        new Program.ApiEndpointPorts(port, port, port),
                        null, null,
                        AdminApiCredential.Create(AdminToken), false,
                        beforeAccessControl: pipeline =>
                            pipeline.UseMiddleware<ApiExceptionHandlingMiddleware>(),
                        afterAccessControl: pipeline => pipeline.UseMvc());
                }))
            .Build();

        return new RunningAdminHost(host, container, port,
            () => storedSettings, () => lastBalanceAddress);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint) listener.LocalEndpoint).Port;
    }

    private static bool IsAddressInUse(Exception exception)
    {
        while(exception != null)
        {
            if(exception is Microsoft.AspNetCore.Connections.AddressInUseException ||
                exception is SocketException
                {
                    SocketErrorCode: SocketError.AddressAlreadyInUse,
                })
                return true;

            exception = exception.InnerException;
        }

        return false;
    }

    private sealed class RunningAdminHost(IHost host, IContainer container,
        int port,
        Func<global::Miningcore.Persistence.Model.MinerSettings> getStoredSettings,
        Func<string> getLastBalanceAddress) :
        IAsyncDisposable
    {
        public int Port { get; } = port;
        public global::Miningcore.Persistence.Model.MinerSettings StoredSettings =>
            getStoredSettings();
        public string LastBalanceAddress => getLastBalanceAddress();

        public Task StartAsync() => host.StartAsync();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await host.StopAsync();
            }
            finally
            {
                host.Dispose();
                container.Dispose();
            }
        }
    }

    private const int ListenerStartAttempts = 5;
    private const string PoolId = "eth1";
    private const string MixedCaseAddress =
        "0xAbCdEf0123456789AbCdEf0123456789AbCdEf01";
    private const string AdminToken =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
}
