using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Text;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.Metadata;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Beam;
using Miningcore.Blockchain.Alephium;
using Miningcore.Blockchain.Xelis;
using Miningcore.Blockchain.Ethereum;
using Miningcore.Configuration;
using Miningcore.Diagnostics;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Rest;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Rpc;

[Collection(RpcDiagnosticCollection.Name)]
public class RpcConsumerDiagnosticTests
{
    private const string Secret = "synthetic-private-secret";
    private const string Malicious = Secret + " https://user:password@invalid/secret?token=key\r\n\u0085\u2028\u2029forged-line";

    [Fact]
    public void GlobalLoggingMutators_AreExcludedFromParallelCollections()
    {
        // xUnit 2.4.2 runs DisableParallelization collections after awaiting all
        // parallel collections. Different collection names do not defeat that rule.
        foreach(var type in new[] { typeof(RpcConsumerDiagnosticTests),
            typeof(Miningcore.Tests.Payments.PayoutManagerLoggingTests),
            typeof(IPAccessWhitelistLoggingTests), typeof(Miningcore.Tests.Mining.ShareRecorderTests) })
        {
            var collection = Assert.Single(type.CustomAttributes.Where(x => x.AttributeType == typeof(CollectionAttribute)));
            var name = collection.ConstructorArguments[0].Value;
            var definition = Assert.Single(type.Assembly.GetTypes().SelectMany(x => x.CustomAttributes)
                .Where(x => x.AttributeType == typeof(CollectionDefinitionAttribute) && Equals(x.ConstructorArguments[0].Value, name)));
            Assert.Contains(definition.NamedArguments,
                x => x.MemberName == "DisableParallelization" && Equals(x.TypedValue.Value, true));
        }
    }

    [Theory]
    [InlineData(AlephiumStratumError.JobNotFound, "job-not-found")]
    [InlineData(AlephiumStratumError.InvalidJobChainIndex, "invalid-job-chain-index")]
    [InlineData(AlephiumStratumError.InvalidWorker, "invalid-worker")]
    [InlineData(AlephiumStratumError.InvalidNonce, "invalid-nonce")]
    [InlineData(AlephiumStratumError.DuplicatedShare, "duplicate-share")]
    [InlineData(AlephiumStratumError.LowDifficultyShare, "low-difficulty-share")]
    [InlineData(AlephiumStratumError.InvalidBlockChainIndex, "invalid-block-chain-index")]
    [InlineData(AlephiumStratumError.MinusOne, "share-rejected")]
    [InlineData((AlephiumStratumError) 1000, "share-rejected")]
    public void AlephiumRejection_KeepsItsOwnCodeAndSafeReason(AlephiumStratumError code, string reason)
    {
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        var error = new AlephiumStratumException(code, Malicious);
        RpcConsumerDiagnostics.Write(logs.Logger, LogLevel.Info, "AlephiumPool.OnSubmitAsync", error, connectionId: "server-session");
        var record = JObject.Parse(Assert.Single(logs.Messages)["RPC consumer diagnostic ".Length..]);
        Assert.Equal(reason, record["failure"].Value<string>());
        Assert.Equal((int) code, record["failureCode"].Value<int>());
        Assert.Equal("server-session", record["connectionId"].Value<string>());
        Assert.Equal(Malicious, error.Message);
        AssertSafe(logs.Messages);
    }

    [Theory]
    [InlineData(Malicious)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("00")]
    [InlineData("0x")]
    [InlineData("0x00")]
    public async Task XelisMinerWork_MalformedRemotePayloadIsNeverLoggedOrPublished(string minerWork)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            await context.Response.WriteAsync(new JObject
            {
                ["id"] = request["id"],
                ["result"] = new JObject { ["template"] = "00", ["topoheight"] = 123,
                    ["difficulty"] = 1, ["miner_work"] = minerWork },
                ["error"] = null,
            }.ToString(Formatting.None));
        });
        using var container = new ContainerBuilder().Build();
        var bus = Substitute.For<IMessageBus>();
        var rpc = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), bus, "test");
        var consumer = new XelisConsumer(container, bus, rpc, logs.Logger);
        Assert.False(await consumer.Refresh(deadline.Token));
        Assert.Null(consumer.CurrentJob);
        Assert.DoesNotContain(bus.ReceivedCalls(), call => call.GetArguments().OfType<NewChainHeightNotification>().Any());
        AssertSafe(logs.Messages);
        var diagnostic = Assert.Single(logs.Messages.Where(x => x.StartsWith("RPC consumer diagnostic ", StringComparison.Ordinal)));
        Assert.Contains("\"operation\":\"XelisJobManager.UpdateJob\"", diagnostic);
        Assert.Contains("\"failure\":\"format\"", diagnostic);
        var raw = await rpc.ExecuteAsync<Miningcore.Blockchain.Xelis.DaemonResponses.GetBlockTemplateResponse>(
            logs.Logger, "get_miner_work", deadline.Token);
        Assert.Equal(minerWork, raw.Response.Template);
    }

    [Fact]
    public void BoundedShareFailure_UnknownKindCannotIntroduceAnArbitraryLabel()
    {
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        RpcConsumerDiagnostics.Write(logs.Logger, LogLevel.Info, "AlephiumPool.OnSubmitAsync", new UnknownShareFailure());
        var record = JObject.Parse(Assert.Single(logs.Messages)["RPC consumer diagnostic ".Length..]);
        Assert.Equal("share-rejected", record["failure"].Value<string>());
        Assert.Equal(1234, record["failureCode"].Value<int>());
        AssertSafe(logs.Messages);
    }

    private sealed class UnknownShareFailure : Exception, IBoundedShareFailure
    {
        internal UnknownShareFailure() : base(Malicious) { }
        public ShareFailureKind DiagnosticKind => (ShareFailureKind) 1234;
        public int DiagnosticCode => 1234;
    }

    [Theory]
    [InlineData(null, "(none)")]
    [InlineData("", "withheld")]
    [InlineData(Malicious, "withheld")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void BlockHashProjection_DistinguishesMissingAndInvalid(string value, string expected)
    {
        Assert.Equal(expected, RpcConsumerDiagnostics.BlockHash(value));
    }

    [Fact]
    public void PaymentFailureMetadata_HasValueEquality()
    {
        var first = PaymentFailureDiagnostic.Create(new HttpRequestException(Malicious), -13);
        var second = PaymentFailureDiagnostic.Create(new HttpRequestException("different private cause"), -13);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XelisMinerWork_ValidHexStillPublishes(bool prefixed)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        var minerWork = (prefixed ? "0x" : "") + new string('a', 224);
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            await context.Response.WriteAsync(new JObject
            {
                ["id"] = request["id"],
                ["result"] = new JObject { ["template"] = new string('0', 256),
                    ["topoheight"] = 123, ["difficulty"] = 1024, ["miner_work"] = minerWork },
                ["error"] = null,
            }.ToString(Formatting.None));
        });
        using var container = new ContainerBuilder().Build();
        var bus = Substitute.For<IMessageBus>();
        var rpc = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), bus, "test");
        var consumer = new XelisConsumer(container, bus, rpc, logs.Logger);
        Assert.True(await consumer.Refresh(deadline.Token), string.Join("\n", logs.Messages));
        Assert.Equal(new string('a', 64), consumer.CurrentJob.PrevHash);
        Assert.Equal(minerWork, consumer.CurrentJob.BlockTemplate.Template);
        Assert.Contains(bus.ReceivedCalls(), call => call.GetArguments().OfType<NewChainHeightNotification>().Any());
        Assert.DoesNotContain(logs.Messages, x => x.Contains(minerWork, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedPayoutFailures_IdentifyBothPoolsAtErrorLevel(bool aggregate)
    {
        var previous = LogManager.Configuration;
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}${exception:format=tostring}" };
        var config = new NLog.Config.LoggingConfiguration();
        config.AddRule(LogLevel.Error, LogLevel.Fatal, target);
        LogManager.Configuration = config;
        try
        {
            var handler = Substitute.For<IPayoutHandler>();
            Exception cause = new HttpRequestException(Malicious);
            Exception failure = aggregate ? new AggregateException(Malicious, cause) : new InvalidOperationException(Malicious, cause);
            handler.ConfigureAsync(Arg.Any<ClusterConfig>(), Arg.Any<PoolConfig>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(failure));
            var builder = new ContainerBuilder();
            builder.RegisterInstance(new[] { new Meta<Lazy<IPayoutHandler, CoinFamilyAttribute>>(
                new Lazy<IPayoutHandler, CoinFamilyAttribute>(() => handler, new CoinFamilyAttribute(CoinFamily.Bitcoin)),
                new Dictionary<string, object>()) })
                .As<IEnumerable<Meta<Lazy<IPayoutHandler, CoinFamilyAttribute>>>>();
            using var container = builder.Build();
            using var manager = new PayoutManager(container, Substitute.For<IConnectionFactory>(),
                Substitute.For<IBlockRepository>(), Substitute.For<IShareRepository>(), Substitute.For<IBalanceRepository>(),
                new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig() },
                Substitute.For<IMessageBus>(), Substitute.For<IPayoutManagerLease>(), new ProcessStatus());
            foreach(var id in new[] { "pool-one", "pool-two" })
            {
                var pool = Substitute.For<IMiningPool>();
                pool.Config.Returns(new PoolConfig { Id = id, Enabled = true,
                    Template = new BitcoinTemplate { Family = CoinFamily.Bitcoin },
                    PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true } });
                typeof(PayoutManager).GetMethod("AttachPool", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, new object[] { pool });
            }
            await (Task) typeof(PayoutManager).GetMethod("ProcessPoolsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(manager, new object[] { CancellationToken.None })!;
            Assert.Equal(2, target.Logs.Count);
            var records = target.Logs.Select(x => JObject.Parse(x["RPC consumer diagnostic ".Length..])).ToArray();
            Assert.Equal(new[] { "pool-one", "pool-two" }, records.Select(x => x["poolId"].Value<string>()).OrderBy(x => x));
            Assert.All(records, x => Assert.Equal("http", x["failure"].Value<string>()));
            AssertSafe(target.Logs.ToArray());
        }
        finally { LogManager.Configuration = previous; }
    }

    [Fact]
    public async Task TrustedStartupDiagnostic_PreservesActionableMessageAndPrivateCause()
    {
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        using var container = new ContainerBuilder().Build();
        var original = Assert.Throws<TrustedPoolStartupException>(() =>
            BitcoinJobManagerBase<BitcoinJob>.ResolvePoolPublicKey(new PoolConfig { Id = "test", PubKey = Malicious }, null));
        var consumer = new StartupConsumer(container, logs.Logger, original);
        var error = await Assert.ThrowsAsync<TrustedPoolStartupException>(() => consumer.StartAsync(CancellationToken.None));
        Assert.Same(original, error);
        Assert.Contains("invalid 'pubKey'", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [Theory]
    [InlineData(StratumError.JobNotFound, "job-not-found")]
    [InlineData(StratumError.DuplicateShare, "duplicate-share")]
    [InlineData(StratumError.LowDifficultyShare, "low-difficulty-share")]
    [InlineData(StratumError.UnauthorizedWorker, "unauthorized-worker")]
    [InlineData(StratumError.NotSubscribed, "not-subscribed")]
    [InlineData(StratumError.Other, "share-rejected")]
    [InlineData(StratumError.MinusOne, "share-rejected")]
    [InlineData((StratumError) 1000, "share-rejected")]
    public void ShareRejection_RetainsBoundedReasonNotArbitraryMessage(StratumError code, string reason)
    {
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        RpcConsumerDiagnostics.Write(logs.Logger, LogLevel.Info, "BitcoinPool.OnSubmitAsync", new StratumException(code, Malicious), connectionId: "server-session");
        var record = JObject.Parse(Assert.Single(logs.Messages)["RPC consumer diagnostic ".Length..]);
        Assert.Equal(reason, record["failure"].Value<string>());
        Assert.Equal((int) code, record["failureCode"].Value<int>());
        Assert.Equal("server-session", record["connectionId"].Value<string>());
        AssertSafe(logs.Messages);
    }

    [Fact]
    public async Task BeamHealthAndConnectivity_ClassifyRestFailure()
    {
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        using var container = new ContainerBuilder().Build();
        using var http = new HttpClient(new FailingHttpHandler());
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        var consumer = new BeamConsumer(container, logs.Logger);
        typeof(BeamJobManager).GetField("explorerRestClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(consumer, new SimpleRestClient(factory, "http://invalid/"));
        Assert.False(await consumer.Healthy());
        Assert.False(await consumer.Connected());
        Assert.Equal(2, logs.Messages.Length);
        Assert.All(logs.Messages, x => Assert.Contains("\"failure\":\"http\"", x));
        AssertSafe(logs.Messages);
    }

    private sealed class FailingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException(Malicious));
    }

    [Fact]
    public async Task BeamSocketConsumer_PreservesWireDataWithoutLoggingPayloadOrParserText()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        using var container = new ContainerBuilder().Build();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = new DaemonEndpointConfig { Host = "127.0.0.1", Port = ((IPEndPoint) listener.LocalEndpoint).Port };
        var consumer = new BeamConsumer(container, logs.Logger);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = consumer.SubscribeBeam(deadline.Token, endpoint, new { password = Malicious })
            .Subscribe(value => received.TrySetResult(value), error => received.TrySetException(error));
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        using var stream = peer.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var request = await reader.ReadLineAsync(deadline.Token);
        Assert.Equal(Malicious, JObject.Parse(request!)["password"].Value<string>());
        var job = new JObject { ["id"] = "job", ["secret"] = Malicious }.ToString(Formatting.None);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(job + "\n"), deadline.Token);
        Assert.Equal(job, await received.Task.WaitAsync(deadline.Token));
        await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"" + Secret + "\": not-json}\n"), deadline.Token);
        while(!logs.Messages.Any(x => x.Contains("\"failure\":\"json\"")))
            await Task.Delay(10, deadline.Token);
        AssertSafe(logs.Messages);
        // Cancel while the worker is in its retry delay, before disposing its linked source.
        subscription.Dispose();
    }

    [Fact]
    public async Task StreamedBitcoinTemplate_ParserFailureIsSafeAndDoesNotPublishWork()
    {
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        using var container = new ContainerBuilder().Build();
        var consumer = new BitcoinConsumer(container, Substitute.For<IMessageBus>(), null, logs.Logger);
        var result = await consumer.RefreshStream("{\"" + Secret + "\": not-json}");
        Assert.False(result.IsNew);
        Assert.False(result.Force);
        AssertSafe(logs.Messages);
        Assert.Contains(logs.Messages, x => x.Contains("\"failure\":\"json\""));
    }

    [Fact]
    public void UncertainPayment_HostProjectionKeepsClassificationAndPrivateEvidence()
    {
        var evidence = new PayoutReconciliation { Uncertain = new[] { new PayoutReconciliationEntry { Detail = Malicious, Amount = 1 } } };
        var original = new PayoutOutcomeUncertainException(Malicious, new IOException(Malicious), evidence);
        var projected = original.ForHostReporting();
        Assert.IsType<PayoutOutcomeUncertainException>(projected);
        Assert.Same(original, projected.OriginalFailure);
        Assert.Same(evidence, projected.OriginalFailure.Reconciliation);
        Assert.Same(evidence, projected.Reconciliation);
        Assert.Null(projected.InnerException);
        Assert.DoesNotContain(Secret, projected.ToString());
        Assert.Contains("unknown wallet outcome", projected.Message);
    }

    [Fact]
    public async Task NullDaemonResult_HealthProbeStaysFalseWithoutDiagnosticFailure()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        await using var server = await MaliciousDaemon("null-result");
        using var container = new ContainerBuilder().Build();
        var bus = Substitute.For<IMessageBus>();
        var consumer = new BitcoinConsumer(container, bus,
            new RpcClient(server.Endpoint(), new JsonSerializerSettings(), bus, "test"), logs.Logger);
        Assert.False(await consumer.Healthy(deadline.Token));
        AssertSafe(logs.Messages);
    }

    [SourceCheckoutFact]
    public void ConsumerSource_UsesReviewedLabelsAndDoesNotReintroduceDirectErrorSinks()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while(directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Miningcore", "Rpc")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var root = Path.Combine(directory!.FullName, "src", "Miningcore");
        var operations = new HashSet<string>();
        // Explicitly scoped-out relay transport and generic host-shutdown errors.
        // New/renamed shared consumers are scanned by directory, not a basename list.
        var exclusions = new[] { "Mining/ShareRelay.cs", "Mining/ShareReceiver.cs", "Mining/MiningFailStopCoordinator.cs" }
            .Select(x => Path.Combine(root, x.Replace('/', Path.DirectorySeparatorChar))).ToHashSet();
        Assert.All(exclusions, file => Assert.True(File.Exists(file), $"Revisit renamed audit exclusion: {file}"));
        foreach(var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                    .Where(x => !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            var source = File.ReadAllText(file);
            foreach(Match match in Regex.Matches(source,
                        "RpcConsumerDiagnostics\\.Write\\([^,]+,\\s*[^,]+,\\s*\"([^\"]+)\""))
            {
                Assert.Contains(match.Groups[1].Value, RpcConsumerOperations.All);
                operations.Add(match.Groups[1].Value);
            }
            if(!file.Contains(Path.DirectorySeparatorChar + "Blockchain" + Path.DirectorySeparatorChar) &&
                !file.Contains(Path.DirectorySeparatorChar + "Payments" + Path.DirectorySeparatorChar) &&
                !file.Contains(Path.DirectorySeparatorChar + "Mining" + Path.DirectorySeparatorChar) &&
                !file.Contains(Path.DirectorySeparatorChar + "Notifications" + Path.DirectorySeparatorChar)) continue;
            if(exclusions.Contains(file)) continue;
            foreach(Match sink in Regex.Matches(source, @"\b\w*[Ll]ogg?er\.(?:Trace|Debug|Info|Warn|Error|Fatal)\(.*?\);", RegexOptions.Singleline))
            {
                Assert.DoesNotMatch(@"\.Error\??\.Message|\.TxKey\b|\.Warning\b|\b(?:ex|exception)\.(?:Message|ToString)\b", sink.Value);
                Assert.DoesNotMatch(@"^\w+\.\w+\((?:ex|exception)\s*[,)]", sink.Value);
                Assert.DoesNotMatch(@"\{(?:json|data|payload|line)\}", sink.Value);
                Assert.DoesNotMatch(@"\{blockTemplate\.Template\}", sink.Value);
            }
        }
        Assert.Equal(RpcConsumerOperations.All.OrderBy(x => x), operations.OrderBy(x => x));
        Assert.All(RpcConsumerOperations.All, value => Assert.True(value.Length < 128));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("synthetic-private-secret", false)]
    [InlineData("https://user:password@invalid", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("0xBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", true)]
    public void UncertainTransactionProjection_RetainsOnlyReviewedIdentifierShape(string value, bool valid)
    {
        var output = RpcConsumerDiagnostics.TransactionId(value);
        if(valid) Assert.Equal(value, output);
        else Assert.Equal("[unverified transaction identifier withheld]", output);
    }

    [Theory]
    [InlineData("bitcoin", "error")]
    [InlineData("bitcoin", "malformed-json")]
    [InlineData("bitcoin", "malformed-result")]
    [InlineData("ethereum", "error")]
    [InlineData("ethereum", "malformed-json")]
    [InlineData("ethereum", "malformed-result")]
    public async Task RealHttpConsumer_DoesNotLogDaemonOrParserText(string family, string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        await using var server = await MaliciousDaemon(mode);
        using var container = new ContainerBuilder().Build();
        var bus = Substitute.For<IMessageBus>();
        var rpc = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), bus, "test");

        if(family == "bitcoin")
        {
            var consumer = new BitcoinConsumer(container, bus, rpc, logs.Logger);
            await consumer.Refresh(deadline.Token);
            Assert.Equal(0, consumer.BlockchainStats.NetworkHashrate);
        }
        else
        {
            var consumer = new EthereumConsumer(container, bus, rpc, logs.Logger);
            Assert.False(await consumer.Refresh(deadline.Token));
        }

        AssertSafe(logs.Messages);
        Assert.Contains(logs.Messages, x => x.StartsWith("RPC consumer diagnostic ", StringComparison.Ordinal));
        // Execute the same request independently: safe output must not rewrite
        // the RPC error or its structural parsing exception for other callers.
        var raw = await rpc.ExecuteAsync<JToken>(logs.Logger, "getblocktemplate", deadline.Token);
        if(mode == "error")
        {
            Assert.Equal(Malicious, raw.Error.Message);
            Assert.Equal(-13, raw.Error.Code);
            Assert.Equal(Secret, ((JObject) raw.Error.Data)["password"].Value<string>());
        }
        else if(mode == "malformed-json")
            Assert.IsAssignableFrom<JsonException>(raw.Error.InnerException);
    }

    [Fact]
    public async Task BlockRejection_PreservesDecisionButDoesNotEmailDaemonText()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        await using var server = await MaliciousDaemon("error");
        using var container = new ContainerBuilder().Build();
        var bus = Substitute.For<IMessageBus>();
        var consumer = new BitcoinConsumer(container, bus,
            new RpcClient(server.Endpoint(), new JsonSerializerSettings(), bus, "test"), logs.Logger);

        Assert.False(await consumer.Submit(deadline.Token));
        var notification = Assert.Single(bus.ReceivedCalls().SelectMany(x => x.GetArguments()).OfType<AdminNotification>());
        Assert.Equal("Block submission failed", notification.Subject);
        Assert.DoesNotContain("payment", notification.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reconcil", notification.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, notification.Message);
        Assert.Contains("withheld", notification.Message);
        AssertSafe(logs.Messages);
    }

    [Fact]
    public async Task WalletRelock_ReportsFailureWithoutLeakingAndDoesNotThrow()
    {
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        await using var server = await MaliciousDaemon("error");
        var bus = Substitute.For<IMessageBus>();
        var rpc = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), bus, "test");
        var payout = new PayoutConsumer(bus, logs.Logger);
        await payout.Relock(async ct =>
        {
            var result = await rpc.ExecuteAsync<JToken>(logs.Logger, "walletlock", ct);
            Assert.Equal(Malicious, result.Error.Message);
            throw new InvalidOperationException(result.Error.Message);
        });
        var notification = Assert.Single(bus.ReceivedCalls().SelectMany(x => x.GetArguments()).OfType<AdminNotification>());
        Assert.Contains("relock", notification.Subject);
        Assert.DoesNotContain(Secret, notification.Message);
        AssertSafe(logs.Messages);
    }

    [Theory]
    [InlineData(PaymentNotificationOutcome.Failure)]
    [InlineData(PaymentNotificationOutcome.Uncertain)]
    public void PaymentAlerts_WithholdErrorAndDetailWithoutMutatingEvidence(PaymentNotificationOutcome outcome)
    {
        var entry = new PayoutReconciliationEntry { Address = "recipient", Amount = 1, Detail = Malicious, TransactionId = Malicious, TransactionIds = new[] { Malicious } };
        var evidence = new PayoutReconciliation { Uncertain = new[] { entry } };
        var notification = new PaymentNotification("test", Malicious, 1, "BTC")
        {
            Outcome = outcome, Reconciliation = evidence,
        };
        var rendered = NotificationService.FormatPaymentNotification(notification, "BTC", null);
        Assert.False(rendered.IsSuccess);
        Assert.DoesNotContain(Secret, rendered.EmailMessage + rendered.PushoverMessage);
        Assert.Equal(Malicious, notification.Error);
        Assert.Same(evidence, notification.Reconciliation);
        Assert.Equal(Malicious, entry.Detail);
        if(outcome == PaymentNotificationOutcome.Uncertain)
            Assert.Contains("reconciliation", rendered.EmailMessage);
    }

    [Fact]
    public async Task DaemonStartup_PreservesOriginalFailureWithoutHostRenderingIt()
    {
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        using var container = new ContainerBuilder().Build();
        var original = new PoolStartupException(Malicious, "test", new JsonReaderException(Malicious));
        var consumer = new StartupConsumer(container, logs.Logger, original);
        var error = await Assert.ThrowsAsync<RpcConsumerStartupException>(() => consumer.StartAsync(CancellationToken.None));
        Assert.Same(original, error.OriginalFailure);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.DoesNotContain(Secret, error.Message);
        AssertSafe(logs.Messages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Projection_RejectsUnknownLabelsAndDoesNotConsultExceptionText(bool debugEnabled)
    {
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        if(!debugEnabled) logs.Disable();
        RpcConsumerDiagnostics.Write(logs.Logger, LogLevel.Error, Malicious, new HostileException(), int.MinValue);
        if(!debugEnabled) Assert.Empty(logs.Messages);
        else
        {
            var data = JObject.Parse(Assert.Single(logs.Messages)["RPC consumer diagnostic ".Length..]);
            Assert.Equal(new[] { "operation", "failure", "code", "failureCode", "poolId", "failedCount", "stage", "connectionId" }, data.Properties().Select(x => x.Name));
            Assert.Equal("other", data["operation"].Value<string>());
            Assert.Equal("other", data["failure"].Value<string>());
            Assert.Equal(int.MinValue, data["code"].Value<int>());
            AssertSafe(logs.Messages);
        }
    }

    private sealed class HostileException : Exception
    {
        public override string Message => throw new InvalidOperationException("Diagnostics accessed Message");
        public override string ToString() => throw new InvalidOperationException("Diagnostics accessed ToString");
    }

    private static void AssertSafe(string[] messages)
    {
        Assert.NotEmpty(messages);
        foreach(var message in messages)
        {
            Assert.DoesNotContain(Secret, message);
            Assert.DoesNotContain("user:password", message);
            Assert.DoesNotContain(message, c => c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029');
            Assert.True(message.Length < 512);
        }
    }

    private static Task<RpcDiagnosticTests.Server> MaliciousDaemon(string mode) => RpcDiagnosticTests.Server.Start(async context =>
    {
        using var reader = new StreamReader(context.Request.Body);
        var request = JToken.Parse(await reader.ReadToEndAsync());
        if(mode == "malformed-json")
        {
            await context.Response.WriteAsync("{\"" + Secret + "\": not-json}");
            return;
        }
        JObject Reply(JToken item) => new()
        {
            ["id"] = item["id"],
            ["result"] = mode is "error" or "null-result" ? null : new JObject { [Secret] = Malicious, ["networkhashps"] = Malicious },
            ["error"] = mode == "error" ? new JObject { ["code"] = -13, ["message"] = Malicious, ["data"] = new JObject { ["password"] = Secret } } : null,
        };
        JToken reply = request is JArray batch ? new JArray(batch.Select(Reply)) : Reply(request);
        await context.Response.WriteAsync(reply.ToString(Formatting.None));
    });

    private sealed class BitcoinConsumer : BitcoinJobManager
    {
        internal BitcoinConsumer(IComponentContext container, IMessageBus bus, RpcClient client, ILogger log)
            : base(container, Substitute.For<IMasterClock>(), bus, Substitute.For<IExtraNonceProvider>())
        {
            rpc = client; logger = log; poolConfig = new PoolConfig { Id = "test" }; hasSubmitBlockMethod = true;
        }
        internal Task Refresh(CancellationToken ct) => UpdateNetworkStatsAsync(ct);
        internal Task<bool> Healthy(CancellationToken ct) => AreDaemonsHealthyAsync(ct);
        internal Task<(bool IsNew, bool Force)> RefreshStream(string json) => UpdateJob(CancellationToken.None, false, json: json);
        internal async Task<bool> Submit(CancellationToken ct) => (await SubmitBlockAsync(new Share { PoolId = "test", BlockHeight = 1, BlockHash = new string('a', 64) }, "00", ct)).Accepted;
    }

    private sealed class BeamConsumer : BeamJobManager
    {
        internal BeamConsumer(IComponentContext container, ILogger log)
            : base(container, Substitute.For<IMasterClock>(), Substitute.For<IHttpClientFactory>(),
                Substitute.For<IMessageBus>(), Substitute.For<IExtraNonceProvider>())
        { logger = log; }
        internal IObservable<string> SubscribeBeam(CancellationToken ct, DaemonEndpointConfig endpoint, object request) =>
            BeamSubscribeStratumApiSocketClient(ct, endpoint, request);
        internal Task<bool> Healthy() => AreDaemonsHealthyAsync(CancellationToken.None);
        internal Task<bool> Connected() => AreDaemonsConnectedAsync(CancellationToken.None);
    }

    private sealed class XelisConsumer : XelisJobManager
    {
        internal XelisConsumer(IComponentContext container, IMessageBus bus, RpcClient client, ILogger log)
            : base(container, Substitute.For<IMasterClock>(), bus, Substitute.For<IExtraNonceProvider>())
        {
            logger = log; poolConfig = new PoolConfig { Id = "test", Address = "test-address", Template = new XelisCoinTemplate { Symbol = "XEL" } };
            typeof(XelisJobManager).GetField("rpc", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, client);
            typeof(XelisJobManager).GetField("network", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, "mainnet");
            typeof(XelisJobManager).GetField("coin", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, poolConfig.Template);
        }
        internal XelisJob CurrentJob => currentJob;
        internal Task<bool> Refresh(CancellationToken ct) => UpdateJob(ct);
    }

    private sealed class EthereumConsumer : EthereumJobManager
    {
        internal EthereumConsumer(IComponentContext container, IMessageBus bus, RpcClient client, ILogger log)
            : base(container, Substitute.For<IMasterClock>(), bus, Substitute.For<IExtraNonceProvider>())
        {
            logger = log; poolConfig = new PoolConfig { Id = "test" };
            typeof(EthereumJobManager).GetField("rpc", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, client);
            typeof(EthereumJobManager).GetField("coin", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, new EthereumCoinTemplate { RpcMethodPrefix = "eth" });
        }
        internal Task<bool> Refresh(CancellationToken ct) => UpdateJob(ct);
    }

    private sealed class PayoutConsumer : PayoutHandlerBase
    {
        internal PayoutConsumer(IMessageBus bus, ILogger log) : base(Substitute.For<IConnectionFactory>(), Substitute.For<IMapper>(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
            Substitute.For<IPaymentRepository>(), Substitute.For<IMasterClock>(), bus)
        { logger = log; poolConfig = new PoolConfig { Id = "test" }; }
        protected override string LogCategory => "test";
        internal Task Relock(Func<CancellationToken, Task> action) => RelockPayoutWalletSafelyAsync(action);
    }

    private sealed class StartupConsumer : JobManagerBase<object>
    {
        private readonly Exception failure;
        internal StartupConsumer(IComponentContext container, ILogger log, Exception error) : base(container, Substitute.For<IMessageBus>())
        { logger = log; poolConfig = new PoolConfig { Id = "test" }; failure = error; }
        protected override void ConfigureDaemons() { }
        protected override Task<bool> AreDaemonsHealthyAsync(CancellationToken ct) => Task.FromException<bool>(failure);
        protected override Task<bool> AreDaemonsConnectedAsync(CancellationToken ct) => Task.FromResult(true);
        protected override Task EnsureDaemonsSynchedAsync(CancellationToken ct) => Task.CompletedTask;
        protected override Task PostStartInitAsync(CancellationToken ct) => Task.CompletedTask;
        public override object GetJobForStratum() => null;
    }
}

public sealed class SourceCheckoutFactAttribute : FactAttribute
{
    public SourceCheckoutFactAttribute()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while(directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Miningcore", "Rpc")))
            directory = directory.Parent;
        if(directory == null)
            Skip = "Source-inventory audit requires a repository checkout; runtime diagnostic tests remain enabled.";
    }
}
