using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Warthog;
using Miningcore.Blockchain.Warthog.DaemonRequests;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using NLog;
using NLog.Config;
using NLog.Targets;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain;

[CollectionDefinition("Warthog wallet secrecy", DisableParallelization = true)]
public sealed class WarthogWalletSecrecyCollection;

[Collection("Warthog wallet secrecy")]
public class WarthogWalletAddressTests : TestBase
{
    // Public, synthetic secp256k1 scalar 1: never use this key for real funds.
    private static readonly string Canary = new string('0', 63) + '1';
    private const string Address = "751e76e8199196d454941c45d1b3a323f1433bd6ffded884";

    [Fact]
    public void LocalDerivation_MatchesIndependentSecp256k1Vector()
    {
        var key = WarthogWalletAddress.ValidatePrivateKey(Canary, Address);
        Assert.Equal(Address, WarthogWalletAddress.DeriveAddress(key));
    }

    [Fact]
    public void PayoutRequestSerialization_ContainsNoWalletSecretField()
    {
        var request = new WarthogSendTransactionRequest
        {
            ToAddress = Address,
            Amount = 1,
            Fee = 1,
            Signature = new string('a', 130),
        };

        var json = JsonSerializer.Serialize(request);
        Assert.DoesNotContain(Canary, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanaryGuards_RejectLegacyUrlAndLogExposure()
    {
        using var capture = new LogCapture();
        Assert.ThrowsAny<Exception>(() => AssertNoCanary(
            new[] { "/tools/wallet/from_privkey/" + Canary }, capture));

        LogManager.GetCurrentClassLogger().Warn("Legacy wallet check included " + Canary);
        Assert.ThrowsAny<Exception>(() => AssertNoCanary(Array.Empty<string>(), capture));
    }

    [Theory]
    [InlineData("invalid-canary")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void InvalidKey_NeverAppearsInExceptionOrInnerException(string key)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            WarthogWalletAddress.ValidatePrivateKey(key, Address));
        Assert.DoesNotContain(key, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task PayoutStartup_MismatchFailsBeforeNetworkWithoutCanaryInDiagnostics()
    {
        using var capture = new LogCapture();
        using var requests = new RecordingHandler();
        var handler = CreatePayoutHandler(requests);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ConfigureAsync(new ClusterConfig(), CreatePool("000000000000000000000000000000000000000000000000"),
                CancellationToken.None));

        Assert.DoesNotContain(Canary, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Canary, string.Join("\n", capture.Target.Logs), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(requests.Paths);
    }

    [Fact]
    public async Task PayoutStartup_ValidKeyNeverEntersRequestPathOrLogs()
    {
        using var capture = new LogCapture();
        using var requests = new RecordingHandler();
        var handler = CreatePayoutHandler(requests);

        await handler.ConfigureAsync(new ClusterConfig(), CreatePool(Address), CancellationToken.None);

        Assert.Equal(new[] { "/chain/mine/" + Address }, requests.Paths);
        AssertNoCanary(requests, capture);
    }

    [Fact]
    public async Task JobManager_MismatchFailsBeforeNetworkWithoutCanaryInDiagnostics()
    {
        using var capture = new LogCapture();
        using var requests = new RecordingHandler();
        var manager = CreateManager(requests);
        manager.Configure(CreatePool("000000000000000000000000000000000000000000000000"),
            new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig { Enabled = true } });

        var error = await Assert.ThrowsAsync<Miningcore.Mining.TrustedPoolStartupException>(
            () => manager.Initialize());

        Assert.DoesNotContain(Canary, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Canary, string.Join("\n", capture.Target.Logs), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(requests.Paths);
    }

    [Fact]
    public async Task JobManager_ValidKeyNeverEntersRequestPathOrLogs()
    {
        using var capture = new LogCapture();
        using var requests = new RecordingHandler();
        var manager = CreateManager(requests);
        manager.Configure(CreatePool(Address),
            new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig { Enabled = true } });

        await manager.Initialize();

        Assert.Contains("/chain/head", requests.Paths);
        Assert.Contains("/chain/mine/" + Address, requests.Paths);
        AssertNoCanary(requests, capture);
    }

    [Fact]
    public async Task LiveDaemon_LocalAddressCheckIsAcceptedWithoutSendingKey()
    {
        var daemon = Environment.GetEnvironmentVariable("MININGCORE_TEST_WARTHOG_DAEMON");
        if(string.IsNullOrEmpty(daemon))
            return;

        var port = ReservePort();
        var session = Path.Combine(Path.GetTempPath(), "miningcore-warthog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        using var process = Process.Start(new ProcessStartInfo(daemon)
        {
            ArgumentList =
            {
                "--isolated", "--temporary", "--testnet",
                "--bind=127.0.0.1:0", $"--rpc=127.0.0.1:{port}",
                $"--session={session}",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var capture = new LogCapture();
            using var requests = new LiveRecordingHandler();
            var factory = MakeFactory(requests);
            var pool = CreatePool(Address);
            pool.Daemons[0].Port = port;
            using var probe = new HttpClient();
            var ready = false;

            for(var attempt = 0; attempt < 100; attempt++)
            {
                if(process.HasExited)
                    break;

                try
                {
                    using var response = await probe.GetAsync($"http://127.0.0.1:{port}/chain/head");
                    if(response.IsSuccessStatusCode)
                    {
                        ready = true;
                        break;
                    }
                }
                catch(HttpRequestException) { }

                await Task.Delay(100);
            }

            Assert.True(ready, "Isolated Warthog daemon did not become ready");
            var payout = CreatePayoutHandler(requests, factory);
            await payout.ConfigureAsync(new ClusterConfig(), pool, CancellationToken.None);
            var manager = CreateManager(requests, factory);
            manager.Configure(pool,
                new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig { Enabled = true } });
            await manager.Initialize();

            Assert.Contains("/chain/mine/" + Address, requests.Paths);
            AssertNoCanary(requests.Paths, capture);
        }
        finally
        {
            if(!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Directory.Delete(session, recursive: true);
        }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void AssertNoCanary(RecordingHandler requests, LogCapture capture)
        => AssertNoCanary(requests.Paths, capture);

    private static void AssertNoCanary(IEnumerable<string> paths, LogCapture capture)
    {
        Assert.DoesNotContain(Canary, string.Join("\n", paths), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/tools/wallet/from_privkey/", string.Join("\n", paths));
        Assert.DoesNotContain(Canary, string.Join("\n", capture.Target.Logs), StringComparison.OrdinalIgnoreCase);
    }

    private PoolConfig CreatePool(string address) => new()
    {
        Id = "wart-key-secrecy",
        Coin = "warthog",
        Template = new WarthogCoinTemplate { Symbol = "WART" },
        Address = address,
        Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 12345 } },
        PaymentProcessing = new PoolPaymentProcessingConfig
        {
            Enabled = true,
            Extra = new Dictionary<string, object> { ["walletPrivateKey"] = Canary },
        },
    };

    private static IHttpClientFactory MakeFactory(HttpMessageHandler requests)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(requests));
        return factory;
    }

    private WarthogPayoutHandler CreatePayoutHandler(HttpMessageHandler requests,
        IHttpClientFactory factory = null)
    {
        factory ??= MakeFactory(requests);
        return new WarthogPayoutHandler(container, Substitute.For<IConnectionFactory>(),
            Substitute.For<IMapper>(), Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
            Substitute.For<IPaymentRepository>(), Substitute.For<IMasterClock>(),
            factory, Substitute.For<IMessageBus>());
    }

    private TestJobManager CreateManager(HttpMessageHandler requests,
        IHttpClientFactory factory = null)
    {
        factory ??= MakeFactory(requests);
        return new TestJobManager(container, factory);
    }

    private sealed class TestJobManager : WarthogJobManager
    {
        public TestJobManager(IComponentContext ctx, IHttpClientFactory factory) :
            base(ctx, Substitute.For<IMasterClock>(), factory,
                Substitute.For<IMessageBus>(), Substitute.For<IExtraNonceProvider>()) { }

        public Task Initialize() => PostStartInitAsync(CancellationToken.None);

        protected override void SetupJobUpdates(CancellationToken ct) { }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Paths.Add(request.RequestUri.PathAndQuery);
            var path = request.RequestUri.AbsolutePath;
            var body = path switch
            {
                "/chain/head" => "{\"code\":0,\"data\":{\"is_janushash\":false}}",
                "/peers/connected" => "[]",
                _ when path.StartsWith("/chain/mine/", StringComparison.Ordinal) =>
                    "{\"code\":0,\"data\":{\"testnet\":true}}",
                _ => "{\"code\":0,\"data\":{\"hashrate\":0}}",
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class LiveRecordingHandler : DelegatingHandler
    {
        public LiveRecordingHandler() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Paths.Add(request.RequestUri.PathAndQuery);
            return base.SendAsync(request, ct);
        }
    }

    private sealed class LogCapture : IDisposable
    {
        private readonly LoggingConfiguration previous = LogManager.Configuration;
        public MemoryTarget Target { get; } = new() { Layout = "${message}|${exception:format=tostring}" };

        public LogCapture()
        {
            var config = new LoggingConfiguration();
            config.AddRuleForAllLevels(Target);
            LogManager.Configuration = config;
        }

        public void Dispose()
        {
            LogManager.Configuration = previous;
            Target.Dispose();
        }
    }
}
