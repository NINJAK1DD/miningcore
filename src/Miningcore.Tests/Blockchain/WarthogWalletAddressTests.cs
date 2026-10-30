using System;
using System.Buffers.Binary;
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
using Microsoft.Extensions.DependencyInjection;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Warthog;
using Miningcore.Blockchain.Warthog.DaemonRequests;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Rest;
using Miningcore.Stratum;
using Miningcore.Time;
using NLog;
using NLog.Config;
using NLog.Targets;
using NSubstitute;
using NBitcoin.Secp256k1;
using Xunit;

namespace Miningcore.Tests.Blockchain;

[CollectionDefinition("Warthog wallet secrecy", DisableParallelization = true)]
public sealed class WarthogWalletSecrecyCollection;

public sealed class WarthogDaemonFactAttribute : FactAttribute
{
    public WarthogDaemonFactAttribute()
    {
        var daemon = Environment.GetEnvironmentVariable("MININGCORE_TEST_WARTHOG_DAEMON");
        if(string.IsNullOrWhiteSpace(daemon) || !File.Exists(daemon))
            Skip = "Set MININGCORE_TEST_WARTHOG_DAEMON to run the isolated Warthog daemon test";
    }
}

[Collection("Warthog wallet secrecy")]
public class WarthogWalletAddressTests : TestBase, IDisposable
{
    private readonly List<ServiceProvider> clientProviders = new();

    public void Dispose()
    {
        foreach(var provider in clientProviders)
            provider.Dispose();
    }

    // Public, synthetic secp256k1 scalar 1: never use this key for real funds.
    private static readonly string Canary = new string('0', 63) + '1';
    private const string Address = "751e76e8199196d454941c45d1b3a323f1433bd6ffded884";
    private const string OddAddress = "7fda9cf020c16cacf529c87d8de89bfc70b8c9cb12eda2b5";
    private static readonly string OddKey = new string('0', 63) + '6';
    private const string CurveOrder = "fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141";
    private const string LargestValidKey = "fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364140";
    private const string LargestValidAddress = "adde4c73c7b9cee17da6c7b3e2b2eea1a0dcbe675de096be";
    private const string LetterKey = "00000000000000000000000000000000000000000000000000000000000000ab";
    private const string LetterAddress = "81ac61244a9ba7b579f747f80f6f1b09fe9dcab1488b77c2";
    private const string PinHash = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    [Fact]
    public void LocalDerivation_MatchesIndependentSecp256k1Vector()
    {
        using var key = WarthogWalletAddress.ValidatePrivateKey(Canary, Address);
        Assert.Equal(Address, WarthogWalletAddress.DeriveAddress(key));
        Assert.Equal(2, key.CreatePubKey().ToBytes()[0]);
    }

    [Fact]
    public void LocalDerivation_MatchesOddCompressedPubkeyVector()
    {
        using var key = WarthogWalletAddress.ValidatePrivateKey(OddKey, OddAddress);
        Assert.Equal(OddAddress, WarthogWalletAddress.DeriveAddress(key));
        Assert.Equal(3, key.CreatePubKey().ToBytes()[0]);
    }

    [Fact]
    public void LargestPermittedScalar_IsAccepted()
    {
        using var key = WarthogWalletAddress.ValidatePrivateKey(LargestValidKey, LargestValidAddress);
        Assert.Equal(LargestValidAddress, WarthogWalletAddress.DeriveAddress(key));
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
        Assert.ThrowsAny<Exception>(() => AssertNoCanary(
            new[] { "http://127.0.0.1/chain/mine?key=" + Canary }, capture));
        Assert.ThrowsAny<Exception>(() => AssertNoCanary(
            new[] { "X-Wallet-Key: " + Canary }, capture));
        Assert.ThrowsAny<Exception>(() => AssertNoCanary(
            new[] { "{\"walletPrivateKey\":\"" + Canary + "\"}" }, capture));

        LogManager.GetCurrentClassLogger().Warn("Legacy wallet check included " + Canary);
        Assert.ThrowsAny<Exception>(() => AssertNoCanary(Array.Empty<string>(), capture));
    }

    [Theory]
    [InlineData("invalid-canary")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData(CurveOrder)]
    [InlineData("fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364142")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000001")]
    [InlineData("00000000000000000000000000000000000000000000000000000000000000001")]
    [InlineData(" 0000000000000000000000000000000000000000000000000000000000000001")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000001 ")]
    [InlineData("0x00000000000000000000000000000000000000000000000000000000000001")]
    public void InvalidKey_NeverAppearsInExceptionOrInnerException(string key)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            WarthogWalletAddress.ValidatePrivateKey(key, Address));
        Assert.DoesNotContain(key, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void UppercasePrivateKeyIsAcceptedButUppercasePoolAddressIsRejected()
    {
        using var key = WarthogWalletAddress.ValidatePrivateKey(LetterKey.ToUpperInvariant(), LetterAddress);
        Assert.Equal(LetterAddress, WarthogWalletAddress.DeriveAddress(key));
        var error = Assert.Throws<ArgumentException>(() =>
            WarthogWalletAddress.ValidatePrivateKey(OddKey, OddAddress.ToUpperInvariant()));
        Assert.Contains("lowercase hex", error.Message);
    }

    [Fact]
    public void JobManager_UppercasePoolAddressFailsBeforeNetwork()
    {
        using var requests = new RecordingHandler();
        var manager = CreateManager(requests);
        var error = Assert.Throws<Miningcore.Mining.TrustedPoolStartupException>(() =>
            manager.Configure(CreatePool(Address.ToUpperInvariant()),
                new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig { Enabled = true } }));
        Assert.Contains("lowercase hex", error.Message);
        Assert.Empty(requests.Requests);
    }

    [Fact]
    public void WrongChecksumAddressIsRejected()
    {
        var incorrect = Address[..^1] + "0";
        Assert.Throws<ArgumentException>(() => WarthogWalletAddress.ValidatePrivateKey(Canary, incorrect));
    }

    [Fact]
    public async Task PayoutStartup_MismatchFailsBeforeNetworkWithoutCanaryInDiagnostics()
    {
        using var capture = new LogCapture();
        using var requests = new RecordingHandler();
        using var handler = CreatePayoutHandler(requests);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ConfigureAsync(new ClusterConfig(), CreatePool("000000000000000000000000000000000000000000000000"),
                CancellationToken.None));

        Assert.DoesNotContain(Canary, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Canary, string.Join("\n", capture.Target.Logs), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(requests.Requests);
    }

    [Fact]
    public async Task PayoutStartup_ValidKeyNeverEntersRequestPathOrLogs()
    {
        using var capture = new LogCapture();
        using var requests = new RecordingHandler();
        using var handler = CreatePayoutHandler(requests);

        await handler.ConfigureAsync(new ClusterConfig(), CreatePool(Address), CancellationToken.None);

        Assert.Equal(new[] { "/chain/mine/" + Address }, requests.Paths);
        AssertNoCanary(requests, capture);
    }

    [Fact]
    public async Task PreparedPayoutSignature_RecoversConfiguredPublicKeyWithoutSendingSecret()
    {
        using var capture = new LogCapture();
        using var requests = new RecordingHandler();
        using var handler = CreatePayoutHandler(requests);
        await handler.ConfigureAsync(new ClusterConfig(), CreatePool(Address), CancellationToken.None);

        var transaction = await handler.Prepare(Address, 1m, 7);
        var signature = Convert.FromHexString(transaction.Signature);
        Assert.Equal(65, signature.Length);

        // Independently reconstruct the Warthog preimage from public transaction fields.
        var preimage = new byte[32 + 4 + 4 + 3 + 8 + 20 + 8];
        Convert.FromHexString(PinHash).CopyTo(preimage, 0);
        BinaryPrimitives.WriteUInt32BigEndian(preimage.AsSpan(32, 4), transaction.PinHeight);
        BinaryPrimitives.WriteUInt32BigEndian(preimage.AsSpan(36, 4), transaction.NonceId);
        BinaryPrimitives.WriteUInt64BigEndian(preimage.AsSpan(43, 8), transaction.Fee);
        Convert.FromHexString(transaction.ToAddress)[..20].CopyTo(preimage, 51);
        BinaryPrimitives.WriteUInt64BigEndian(preimage.AsSpan(71, 8), transaction.Amount);
        var digest = System.Security.Cryptography.SHA256.HashData(preimage);

        Assert.True(SecpRecoverableECDSASignature.TryCreateFromCompact(
            signature.AsSpan(0, 64), signature[64], out var recoverable));
        Assert.True(ECPubKey.TryRecover(Context.Instance, recoverable, digest, out var recovered));
        using var expectedKey = WarthogWalletAddress.ValidatePrivateKey(Canary, Address);
        Assert.Equal(expectedKey.CreatePubKey().ToBytes(), recovered.ToBytes());

        await handler.Send(transaction);
        Assert.Contains("/transaction/add", requests.Paths);
        Assert.Contains(requests.Requests, request =>
            request.Contains("signature65", StringComparison.Ordinal) &&
            request.Contains(transaction.Signature, StringComparison.OrdinalIgnoreCase));
        AssertNoCanary(requests, capture);
        Assert.DoesNotContain("privKey", JsonSerializer.Serialize(transaction), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JobManager_MismatchFailsBeforeNetworkWithoutCanaryInDiagnostics()
    {
        using var capture = new LogCapture();
        using var requests = new RecordingHandler();
        var manager = CreateManager(requests);
        var error = Assert.Throws<Miningcore.Mining.TrustedPoolStartupException>(() =>
            manager.Configure(CreatePool("000000000000000000000000000000000000000000000000"),
                new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig { Enabled = true } }));

        Assert.Contains("does not match", error.Message);
        Assert.DoesNotContain(Canary, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Canary, string.Join("\n", capture.Target.Logs), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(requests.Requests);
    }

    [Fact]
    public async Task JobManager_ValidKeyNeverEntersRequestPathOrLogs()
    {
        using var capture = new LogCapture();
        using var requests = new RecordingHandler();
        var manager = CreateManager(requests);
        manager.Configure(CreatePool(Address),
            new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig { Enabled = true } });

        using var startup = new CancellationTokenSource();
        try
        {
            await manager.StartAsync(startup.Token);
            Assert.Contains("/chain/head", requests.Paths);
            Assert.Contains("/chain/mine/" + Address, requests.Paths);
            AssertNoCanary(requests, capture);
        }
        finally
        {
            startup.Cancel();
        }
    }

    [WarthogDaemonFact]
    public async Task LiveDaemon_LocalAddressCheckIsAcceptedWithoutSendingKey()
    {
        var daemon = Environment.GetEnvironmentVariable("MININGCORE_TEST_WARTHOG_DAEMON");

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
            using var payout = CreatePayoutHandler(requests, factory);
            await payout.ConfigureAsync(new ClusterConfig(), pool, CancellationToken.None);
            var manager = CreateManager(requests, factory);
            manager.Configure(pool,
                new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig { Enabled = true } });
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await manager.Initialize(startupTimeout.Token);
                Assert.Contains("/chain/mine/" + Address, requests.Paths);
                AssertNoCanary(requests.Requests, capture);
            }
            finally
            {
                startupTimeout.Cancel();
            }
        }
        finally
        {
            if(!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            try { Directory.Delete(session, recursive: true); }
            catch(IOException) { }
            catch(UnauthorizedAccessException) { }
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
        => AssertNoCanary(requests.Requests, capture);

    private static void AssertNoCanary(IEnumerable<string> requests, LogCapture capture)
    {
        Assert.DoesNotContain(Canary, string.Join("\n", requests), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/tools/wallet/from_privkey/", string.Join("\n", requests));
        Assert.DoesNotContain(Canary, string.Join("\n", capture.Target.Logs), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> DescribeRequestAsync(HttpRequestMessage request, CancellationToken ct) =>
        $"{request.Method} {request.RequestUri}\n{request.Headers}\n{request.Content?.Headers}\n" +
        (request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));

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

    private IHttpClientFactory MakeFactory(HttpMessageHandler requests)
    {
        var services = new ServiceCollection();
        services.AddDaemonRestClient()
            .ConfigurePrimaryHttpMessageHandler(() => requests);
        var provider = services.BuildServiceProvider();
        clientProviders.Add(provider);
        return provider.GetRequiredService<IHttpClientFactory>();
    }

    private TestPayoutHandler CreatePayoutHandler(HttpMessageHandler requests,
        IHttpClientFactory factory = null)
    {
        factory ??= MakeFactory(requests);
        return new TestPayoutHandler(container, Substitute.For<IConnectionFactory>(),
            Substitute.For<IMapper>(), Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
            Substitute.For<IPaymentRepository>(), Substitute.For<IMasterClock>(),
            factory, Substitute.For<IMessageBus>());
    }

    private sealed class TestPayoutHandler : WarthogPayoutHandler
    {
        public TestPayoutHandler(IComponentContext ctx, IConnectionFactory cf, IMapper mapper,
            IShareRepository shareRepo, IBlockRepository blockRepo, IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo, IMasterClock clock, IHttpClientFactory httpClientFactory,
            IMessageBus messageBus) : base(ctx, cf, mapper, shareRepo, blockRepo, balanceRepo,
                paymentRepo, clock, httpClientFactory, messageBus) { }

        public Task<WarthogSendTransactionRequest> Prepare(string address, decimal amount, uint nonceId) =>
            PreparePayoutTransactionAsync(address, amount, nonceId, CancellationToken.None);

        public async Task Send(WarthogSendTransactionRequest request) =>
            await SendPayoutTransactionAsync(request, CancellationToken.None);
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

        public Task Initialize(CancellationToken ct) => PostStartInitAsync(ct);

        protected override void SetupJobUpdates(CancellationToken ct) { }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();
        public List<string> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Paths.Add(request.RequestUri.PathAndQuery);
            Requests.Add(await DescribeRequestAsync(request, ct));
            var path = request.RequestUri.AbsolutePath;
            var body = path switch
            {
                "/chain/head" => "{\"code\":0,\"data\":{\"is_janushash\":false,\"synced\":true,\"pinHash\":\"" + PinHash + "\",\"pinHeight\":42}}",
                "/peers/connected" => "[]",
                _ when path.StartsWith("/chain/mine/", StringComparison.Ordinal) =>
                    "{\"code\":0,\"data\":{\"testnet\":true}}",
                _ when path.StartsWith("/tools/encode16bit/from_e8/", StringComparison.Ordinal) =>
                    "{\"code\":0,\"data\":{\"roundedE8\":1}}",
                "/transaction/add" => "{\"code\":0,\"data\":{\"txHash\":\"synthetic\"}}",
                _ => "{\"code\":0,\"data\":{\"hashrate\":0}}",
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };
        }
    }

    private sealed class LiveRecordingHandler : DelegatingHandler
    {
        public LiveRecordingHandler() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
        public List<string> Paths { get; } = new();
        public List<string> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Paths.Add(request.RequestUri.PathAndQuery);
            Requests.Add(await DescribeRequestAsync(request, ct));
            return await base.SendAsync(request, ct);
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
