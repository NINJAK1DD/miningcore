using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IO;
using Miningcore.Banning;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Ethereum;
using Miningcore.Blockchain.Xelis;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Blockchain.Alephium;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Diagnostics;
using Miningcore.Rpc;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Stratum;

public class StratumDiagnosticTests
{
    private const string Secret = "synthetic-stratum-secret";
    private const string Hostile = Secret + "\r\n\u0085\u2028\u2029forged-line";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    public static IEnumerable<object[]> Failures()
    {
        yield return new object[] { new JsonReaderException(Hostile, Hostile, 12, 34, null), "json" };
        yield return new object[] { new JsonSerializationException(Hostile), "json" };
        yield return new object[] { new InvalidDataException(Hostile), "invalid-data" };
        yield return new object[] { new AuthenticationException(Hostile), "tls-handshake" };
        yield return new object[] { new CryptographicException(Hostile), "cryptographic" };
        yield return new object[] { new IOException(Hostile) { Source = Hostile }, "io" };
        yield return new object[] { new ArgumentException(Hostile, Hostile), "argument" };
        yield return new object[] { new FormatException(Hostile), "format" };
        yield return new object[] { new HostileException(), "other" };
        yield return new object[] { new StratumException(StratumError.DuplicateShare, Hostile), "duplicate-share" };
        yield return new object[] { new StratumException(StratumError.JobNotFound, Hostile), "job-not-found" };
        yield return new object[] { new StratumException(StratumError.LowDifficultyShare, Hostile), "low-difficulty-share" };
        yield return new object[] { new StratumException(StratumError.UnauthorizedWorker, Hostile), "unauthorized-worker" };
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void EveryEvent_ProjectsFailureWithoutFormattingOrAttachingIt(Exception failure, string category)
    {
        using var logs = new Capture();
        foreach(var operation in System.Enum.GetValues<StratumDiagnostics.Event>())
            StratumDiagnostics.Write(logs.Logger, LogLevel.Error, operation,
                "server-connection", failure, Hostile, long.MaxValue, 65535);

        foreach(var record in logs.Records)
        {
            Assert.Equal(category, record["failure"].Value<string>());
            Assert.Equal("server-connection", record["connectionId"].Value<string>());
            Assert.Equal(long.MaxValue, record["bytes"].Value<long>());
            Assert.Equal(65535, record["port"].Value<int>());
            if(failure is StratumException share)
                Assert.Equal((int) share.Code, record["code"].Value<int>());
        }
        logs.AssertSafe();
    }

    [Fact]
    public void OptionalFields_AreOmittedForEveryEventWithoutLosingUnknownRequestMethod()
    {
        using var logs = new Capture();
        foreach(var operation in System.Enum.GetValues<StratumDiagnostics.Event>())
            StratumDiagnostics.Write(logs.Logger, LogLevel.Debug, operation);

        foreach(var record in logs.Records)
        {
            var request = record["event"].Value<string>() == "Request";
            Assert.Equal(request ? 2 : 1, record.Count);
            if(request) Assert.Equal("other", record["method"].Value<string>());
            Assert.DoesNotContain(record.Properties(), x => x.Value.Type == JTokenType.Null);
        }
        logs.AssertSafe();
    }

    [Theory]
    [InlineData("ReceiveWait")]
    [InlineData("BufferWait")]
    public void WaitingRecords_ContainOnlyEventAndConnectionId(string eventName)
    {
        using var logs = new Capture();
        var operation = System.Enum.Parse<StratumDiagnostics.Event>(eventName);
        StratumDiagnostics.Write(logs.Logger, LogLevel.Debug, operation, "0HN7A1B2C3D4E");
        Assert.Equal("Stratum diagnostic {\"event\":\"" + operation + "\",\"connectionId\":\"0HN7A1B2C3D4E\"}", Assert.Single(logs.Messages));
        Assert.True(Encoding.UTF8.GetByteCount(Assert.Single(logs.Messages)) < 80);
        logs.AssertSafe();
    }

    [Fact]
    public void OptionalFields_RetainZeroValuesAndIgnoreEventInapplicableMethodAndReason()
    {
        using var logs = new Capture();
        StratumDiagnostics.Write(logs.Logger, LogLevel.Debug, StratumDiagnostics.Event.Receive,
            failure: new SocketException(0), method: Hostile, bytes: 0, port: 0);
        var record = Assert.Single(logs.Records);
        Assert.Equal(0, record["bytes"].Value<long>());
        Assert.Equal(0, record["port"].Value<int>());
        Assert.Equal(0, record["code"].Value<int>());
        Assert.Null(record["connectionId"]);
        Assert.Null(record["method"]);
        Assert.Null(record["reason"]);
        logs.AssertSafe();
    }

    [Fact]
    public void Vocabulary_CoversAllDeclaredMethodsAndRejectsArbitraryValues()
    {
        var declared = typeof(StratumConnection).Assembly.GetTypes()
            .Where(x => x.Name.EndsWith("StratumMethods", StringComparison.Ordinal))
            .SelectMany(x => x.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(x => x.IsLiteral && x.FieldType == typeof(string))
            .Select(x => (string) x.GetRawConstantValue()).Distinct().ToArray();
        Assert.NotEmpty(declared);
        foreach(var method in declared)
        {
            Assert.Equal(method, StratumDiagnostics.Method(method));
            Assert.Equal("other", StratumDiagnostics.Method(method + Hostile));
        }
        Assert.Equal("other", StratumDiagnostics.Method(null));
        Assert.Equal("other", StratumDiagnostics.Method(new string('x', 100_000)));
        using var logs = new Capture();
        StratumDiagnostics.Write(logs.Logger, LogLevel.Debug,
            (StratumDiagnostics.Event) int.MaxValue, method: Hostile);
        Assert.Equal("other", Assert.Single(logs.Records)["event"].Value<string>());
        logs.AssertSafe();
    }

    [Fact]
    public void Vocabulary_CoversBundledEthashV1PrefixesWithoutAcceptingArbitraryPrefixes()
    {
        // The pool composes these names at runtime, so scanning method constants alone
        // cannot detect a missing bundled coin prefix (for example Cortex's ctxc).
        var templates = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "coins.json")));
        var defaultPrefix = new EthereumCoinTemplate().RpcMethodPrefix;
        var prefixes = templates.Properties().Select(x => x.Value)
            .Where(x => x["family"]?.Value<string>() == "ethereum")
            .Select(x => x["rpcMethodPrefix"]?.Value<string>() ?? defaultPrefix).Distinct().ToArray();
        Assert.Contains("ctxc", prefixes);
        foreach(var prefix in prefixes)
        foreach(var suffix in new[] { EthereumStratumMethods.SubmitLogin, EthereumStratumMethods.GetWork,
            EthereumStratumMethods.SubmitWork, EthereumStratumMethods.SubmitHashrate })
        {
            var method = prefix + suffix;
            Assert.Equal(method, StratumDiagnostics.Method(method));
            Assert.Equal("other", StratumDiagnostics.Method(method + Hostile));
            Assert.Equal("other", StratumDiagnostics.Method(Hostile + method));
            Assert.Equal("other", StratumDiagnostics.Method("unreviewed-prefix" + suffix));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mining.authorize")]
    [InlineData("ctxc_submitLogin")]
    [InlineData("ctxc_getWork")]
    [InlineData("ctxc_submitWork")]
    [InlineData("ctxc_submitHashrate")]
    public async Task Tcp_RequestsAndResponsesRetainSecretsOnlyOnWire_AndTelemetryIsBounded(string knownMethod)
    {
        using var logs = new Capture();
        using var container = new ContainerBuilder().Build();
        var bus = Substitute.For<IMessageBus>();
        var server = new DiagnosticServer(container, bus, logs.Logger);
        var requests = new ConcurrentQueue<JsonRpcRequest>();
        server.Handler = async (connection, request, _) =>
        {
            requests.Enqueue(request);
            await connection.RespondAsync(request.Params, request.Id);
        };
        await using(var tcp = await TcpSession.Start(logs.Logger, server))
        {
            foreach(var method in new[] { knownMethod, Hostile, knownMethod + Hostile, "mining.submit" })
            {
                var request = new JObject
                {
                    ["id"] = Hostile, ["method"] = method,
                    ["params"] = new JArray(Hostile + ".worker", Hostile, new JObject { ["agent"] = Hostile }),
                };
                if(method == null)
                    request.Remove("method");
                await tcp.Send(request.ToString(Formatting.None) + "\n");
                var response = JObject.Parse(await tcp.Reader.ReadLineAsync().WaitAsync(Deadline));
                Assert.Equal(Hostile, response["id"].Value<string>());
                Assert.True(JToken.DeepEquals(request["params"], response["result"]));
            }
        }
        Assert.Equal(new[] { knownMethod, Hostile, knownMethod + Hostile, "mining.submit" }, requests.Select(x => x.Method));
        Assert.All(requests, x => Assert.Equal(Hostile, x.ParamsAs<JArray>()[1].Value<string>()));
        var telemetry = bus.ReceivedCalls().SelectMany(x => x.GetArguments()).OfType<TelemetryEvent>()
            .Where(x => x.Category == TelemetryCategory.StratumRequest).ToArray();
        var expectedMethods = new[] { knownMethod ?? "other", "other", "other", "mining.submit" };
        Assert.Equal(expectedMethods, telemetry.Select(x => x.Info));
        Assert.Equal(1, server.Completions);
        Assert.Equal(0, server.Errors);
        Assert.Equal(expectedMethods, logs.Records.Where(x => x["event"].Value<string>() == "Request")
            .Select(x => x["method"].Value<string>()));
        Assert.Contains(logs.Records, x => x["event"].Value<string>() == "Send" && x["bytes"].Value<long>() > 0);
        Assert.Contains(logs.Records, x => x["event"].Value<string>() == "Buffer" && x["bytes"].Value<long>() > 0);
        Assert.Contains(logs.Records, x => x["event"].Value<string>() == "ReceiveWait");
        Assert.Contains(logs.Records, x => x["event"].Value<string>() == "BufferWait");
        server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
        logs.AssertSafe();
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("default", true)]
    [InlineData("enabled", true)]
    [InlineData("disabled", false)]
    public async Task Tcp_HostileMalformedJson_PreservesExistingJunkBanPolicy(string policy, bool banned)
    {
        using var logs = new Capture();
        using var container = new ContainerBuilder().Build();
        var server = new DiagnosticServer(container, Substitute.For<IMessageBus>(), logs.Logger, policy);
        await using(var tcp = await TcpSession.Start(logs.Logger, server))
        {
            // An unexpected token below an attacker-controlled JSON path makes Json.NET
            // include that path in its exception. The transport must not render it.
            await tcp.Send("{\"" + Secret + "\":{\"" + Secret + "\":!}}\n");
            await tcp.Dispatch.WaitAsync(Deadline);
        }
        Assert.Equal(1, server.Errors);
        Assert.Equal(0, server.Completions);
        Assert.Equal(0, server.Requests);
        server.Bans.Received(banned ? 1 : 0).Ban(IPAddress.Loopback, TimeSpan.FromMinutes(3));
        Assert.Contains(logs.Records, x => x["failure"]?.Value<string>() == "json");
        logs.AssertSafe();
    }

    [Fact]
    public async Task Tcp_OversizedInput_KeepsInvalidDataCategoryAndDoesNotIntroduceBan()
    {
        using var logs = new Capture();
        using var container = new ContainerBuilder().Build();
        var server = new DiagnosticServer(container, Substitute.For<IMessageBus>(), logs.Logger);
        await using(var tcp = await TcpSession.Start(logs.Logger, server))
        {
            await tcp.Send(Secret + new string('x', 0x8000));
            await tcp.Dispatch.WaitAsync(Deadline);
        }
        Assert.Equal(0, server.Requests);
        Assert.Equal(1, server.Errors);
        server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
        Assert.Contains(logs.Records, x => x["failure"]?.Value<string>() == "invalid-data");
        logs.AssertSafe();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tcp_TerminalCallbackFailure_IsSafeAndOnlySignalsOnce(bool error)
    {
        using var logs = new Capture();
        using var container = new ContainerBuilder().Build();
        var server = new DiagnosticServer(container, Substitute.For<IMessageBus>(), logs.Logger)
        {
            ThrowInTerminalCallback = true,
        };
        await using(var tcp = await TcpSession.Start(logs.Logger, server))
        {
            if(error)
                await tcp.Send(Secret + "\n");
            else
                tcp.Client.Dispose();
            await tcp.Dispatch.WaitAsync(Deadline);
        }
        Assert.Equal(error ? 1 : 0, server.Errors);
        Assert.Equal(error ? 0 : 1, server.Completions);
        Assert.Single(logs.Records.Where(x => x["event"].Value<string>() == "TerminalCallback"));
        logs.AssertSafe();
    }

    [Theory]
    [InlineData("PROXY TCP4 synthetic-stratum-secret 127.0.0.1 123 456\r\n", "format", false)]
    [InlineData("PROXY TCP4 127.0.0.1 127.0.0.1 synthetic-stratum-secret 456\r\n", "format", false)]
    [InlineData("PROXY TCP4 127.0.0.1 127.0.0.1 123 456\r\n", null, true)]
    public async Task Tcp_ProxyHeaders_AreMetadataOnly(string header, string failure, bool valid)
    {
        using var logs = new Capture();
        using var container = new ContainerBuilder().Build();
        var server = new DiagnosticServer(container, Substitute.For<IMessageBus>(), logs.Logger);
        await using(var tcp = await TcpSession.Start(logs.Logger, server, new PoolEndpoint
        {
            TcpProxyProtocol = new TcpProxyProtocolConfig { Enable = true, Mandatory = true, ProxyAddresses = new[] { "127.0.0.1" } },
        }))
        {
            await tcp.Send(header);
            if(valid)
            {
                await tcp.Send("{\"id\":1,\"method\":\"mining.authorize\",\"params\":[\"" + Secret + "\"]}\n");
                Assert.True(JObject.Parse(await tcp.Reader.ReadLineAsync().WaitAsync(Deadline))["result"].Value<bool>());
            }
            else
                await tcp.Dispatch.WaitAsync(Deadline);
        }
        Assert.Equal(valid ? 1 : 0, server.Requests);
        Assert.Equal(valid ? 0 : 1, server.Errors);
        if(!valid)
            Assert.Contains(logs.Records, x => x["failure"]?.Value<string>() == failure);
        server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
        Assert.Contains(logs.Records, x => x["event"].Value<string>() == "ProxyHeader");
        logs.AssertSafe();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Tcp_BitcoinAuthorization_UsesProductionHandlerWithoutLoggingIdentity(bool authorized, bool ban)
    {
        using var logs = new Capture();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        builder.RegisterInstance(Substitute.For<IBlockRepository>());
        builder.RegisterInstance(Substitute.For<IShareRepository>());
        using var container = builder.Build();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var pool = new AuthorizationPool(container, logs.Logger, cache, authorized, ban);
        var worker = new BitcoinWorkerContext { UserAgent = Hostile };
        worker.Init(1, null, new StandardClock());
        var server = new DiagnosticServer(container, Substitute.For<IMessageBus>(), logs.Logger)
        {
            Initialize = connection => connection.SetContext(worker),
            Handler = pool.Authorize,
        };
        await using(var tcp = await TcpSession.Start(logs.Logger, server))
        {
            var request = new JObject
            {
                ["id"] = Hostile, ["method"] = "mining.authorize",
                ["params"] = new JArray(Secret + "." + Hostile, Hostile),
            };
            await tcp.Send(request.ToString(Formatting.None) + "\n");
            if(ban)
            {
                // Login bans retain abortive disconnect; delivery of the queued rejection
                // before that close has never been guaranteed by this handler.
                await tcp.Dispatch.WaitAsync(Deadline);
            }
            else
            {
                var response = JObject.Parse(await tcp.Reader.ReadLineAsync().WaitAsync(Deadline));
                Assert.Equal(Hostile, response["id"].Value<string>());
                Assert.Equal(authorized, response["result"].Value<bool>());
                if(!authorized)
                    Assert.Equal((int) StratumError.UnauthorizedWorker, response["error"]["code"].Value<int>());
            }
        }
        Assert.Equal(authorized, worker.IsAuthorized);
        Assert.Equal(Secret, worker.Miner);
        Assert.Equal(Hostile.Trim(), worker.Worker);
        Assert.Equal(Hostile, pool.Password);
        Assert.Equal(0, worker.Stats.ValidShares);
        Assert.Equal(0, worker.Stats.InvalidShares);
        pool.Bans.Received(ban ? 1 : 0).Ban(IPAddress.Loopback, TimeSpan.FromSeconds(10));
        if(ban)
            Assert.Contains(logs.Messages, x => x.Contains("Banning unauthorized worker (identity withheld)", StringComparison.Ordinal));
        logs.AssertSafe();
    }

    [Theory]
    [InlineData("xelis")]
    [InlineData("kaspa")]
    [InlineData("alephium")]
    public async Task Tcp_ProductionJobLookup_RejectsHostileJobIdWithUnchangedReason(string family)
    {
        using var logs = new Capture();
        using var container = new ContainerBuilder().Build();
        var bus = Substitute.For<IMessageBus>();
        var server = new DiagnosticServer(container, bus, logs.Logger);
        var xelis = new XelisLookup(container, bus, logs.Logger);
        var kaspa = new KaspaLookup(container, bus, logs.Logger);
        var alephium = new AlephiumLookup(container, bus, logs.Logger);
        server.Initialize = connection => connection.SetContext<Miningcore.Mining.WorkerContextBase>(family switch
        {
            "xelis" => new XelisWorkerContext { Miner = Hostile, UserAgent = Hostile },
            "kaspa" => new KaspaWorkerContext { Miner = Hostile, UserAgent = Hostile },
            _ => new AlephiumWorkerContext { Miner = Hostile, UserAgent = Hostile },
        });
        server.Handler = async (connection, request, ct) =>
        {
            var parameters = request.ParamsAs<string[]>();
            if(family == "alephium")
            {
                var alephiumError = await Assert.ThrowsAsync<AlephiumStratumException>(async () =>
                    await alephium.SubmitShareAsync(connection, new AlephiumWorkerSubmitParams
                    {
                        Worker = parameters[0], JobId = parameters[1], Nonce = parameters[2],
                    }, ct));
                Assert.Equal(AlephiumStratumError.JobNotFound, alephiumError.Code);
                Assert.Equal("job not found", alephiumError.Message);
                await connection.RespondAsync(new JsonRpcResponse(
                    new JsonRpcError((int) alephiumError.Code, alephiumError.Message, null), request.Id));
                return;
            }
            var error = await Assert.ThrowsAsync<StratumException>(async () =>
            {
                switch(family)
                {
                    case "xelis": await xelis.SubmitShareAsync(connection, parameters, ct); break;
                    case "kaspa": await kaspa.SubmitShareAsync(connection, parameters, ct); break;
                }
            });
            Assert.Equal(StratumError.JobNotFound, error.Code);
            Assert.Equal("job not found", error.Message);
            await connection.RespondErrorAsync(error.Code, error.Message, request.Id);
        };
        await using(var tcp = await TcpSession.Start(logs.Logger, server))
        {
            await tcp.Send(new JObject
            {
                ["id"] = 7, ["method"] = "mining.submit", ["params"] = new JArray(Hostile, Hostile, Hostile),
            }.ToString(Formatting.None) + "\n");
            var response = JObject.Parse(await tcp.Reader.ReadLineAsync().WaitAsync(Deadline));
            Assert.Equal(family == "alephium" ? (int) AlephiumStratumError.JobNotFound : (int) StratumError.JobNotFound,
                response["error"]["code"].Value<int>());
        }
        Assert.Equal(1, server.Requests);
        server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
        Assert.Contains(logs.Messages, x => x.Contains("Share rejected: job-not-found", StringComparison.Ordinal));
        logs.AssertSafe();
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("default", true)]
    [InlineData("enabled", true)]
    [InlineData("disabled", false)]
    public async Task Tcp_MalformedTlsHandshake_PreservesBanAndDoesNotLogInput(string policy, bool banned)
    {
        using var logs = new Capture();
        using var container = new ContainerBuilder().Build();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var server = new DiagnosticServer(container, Substitute.For<IMessageBus>(), logs.Logger, policy);
        await using(var tcp = await TcpSession.Start(logs.Logger, server, new PoolEndpoint { Tls = true }, certificate))
        {
            await tcp.Send(Hostile + "\n");
            await tcp.Dispatch.WaitAsync(Deadline);
        }
        Assert.Equal(0, server.Requests);
        Assert.Equal(1, server.Errors);
        server.Bans.Received(banned ? 1 : 0).Ban(IPAddress.Loopback, TimeSpan.FromMinutes(3));
        Assert.Contains(logs.Records, x => x["failure"]?.Value<string>() is "tls-handshake" or "io");
        logs.AssertSafe();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Listener_AcceptAndTaskRemovalFailures_AreSafeAndReleaseSocketOwnership(bool duringAccept)
    {
        using var logs = new Capture();
        using var container = new ContainerBuilder().Build();
        var server = new DiagnosticServer(container, Substitute.For<IMessageBus>(), logs.Logger);
        if(duringAccept)
            server.Initialize = _ => throw new Exception(Hostile);
        else
            server.RemovingTask = _ => Task.FromException(new Exception(Hostile));
        using var reservation = CreateReservation(new PoolEndpoint());
        var endpoint = reservation.Endpoint.IPEndPoint;
        using var lifetime = new CancellationTokenSource(Deadline);
        var run = server.RunAsync(lifetime.Token, reservation);
        try
        {
            using(var client = new TcpClient(AddressFamily.InterNetwork))
            {
                await client.ConnectAsync(endpoint, lifetime.Token);
                if(!duringAccept)
                {
                    await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"mining.authorize\"}\n"), lifetime.Token);
                    using var reader = new StreamReader(client.GetStream());
                    Assert.True(JObject.Parse(await reader.ReadLineAsync(lifetime.Token))["result"].Value<bool>());
                }
            }
            await logs.WaitForEvent(duringAccept ? "AcceptError" : "TaskRemoval");
        }
        finally
        {
            lifetime.Cancel();
            await run.WaitAsync(Deadline);
        }
        Assert.Equal(0, server.ConnectionCount);
        Assert.Equal(0, server.TrackedConnectionTaskCount);
        using var rebound = StratumServer.CreateBoundSocket(endpoint);
        logs.AssertSafe();
    }

    [Theory]
    [InlineData("missing", "file-not-found")]
    [InlineData("wrong-password", "invalid-certificate-or-password")]
    [InlineData("invalid", "invalid-certificate-or-password")]
    public async Task Listener_CertificateLoadFailure_DoesNotDisclosePathOrPassword(string mode, string reason)
    {
        using var logs = new Capture();
        using var container = new ContainerBuilder().Build();
        var server = new DiagnosticServer(container, Substitute.For<IMessageBus>(), logs.Logger);
        using var reservation = CreateReservation(new PoolEndpoint
        {
            Tls = true, TlsPfxFile = Path.Combine(Path.GetTempPath(), Secret + Guid.NewGuid().ToString("N") + ".pfx"),
            TlsPfxPassword = Hostile,
        });
        var path = reservation.Endpoint.PoolEndpoint.TlsPfxFile;
        try
        {
            if(mode == "wrong-password")
            {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
                File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, "different-synthetic-password"));
            }
            else if(mode == "invalid")
                File.WriteAllText(path, Hostile);
            await Assert.ThrowsAnyAsync<CryptographicException>(() => server.RunAsync(CancellationToken.None, reservation));
        }
        finally { if(mode != "missing") File.Delete(path); }
        var record = Assert.Single(logs.Records);
        Assert.Equal("CertificateLoad", record["event"].Value<string>());
        Assert.Equal("cryptographic", record["failure"].Value<string>());
        Assert.Equal(reason, record["reason"].Value<string>());
        Assert.Equal(reservation.Endpoint.IPEndPoint.Port, record["port"].Value<int>());
        Assert.Equal(0, server.TrackedConnectionTaskCount);
        using var rebound = StratumServer.CreateBoundSocket(reservation.Endpoint.IPEndPoint);
        logs.AssertSafe();
    }

    [Theory]
    [InlineData(false, "tls-handshake")]
    [InlineData(true, "cryptographic")]
    public void SharedFailureCategories_AgreeAcrossStratumAndRpcConsumers(bool crypto, string category)
    {
        using var logs = new Capture();
        Exception failure = crypto ? new CryptographicException(Hostile) : new AuthenticationException(Hostile);
        Assert.Equal(category, DiagnosticFailure.Category(failure));
        StratumDiagnostics.Write(logs.Logger, LogLevel.Error, StratumDiagnostics.Event.ConnectionError, failure: failure);
        RpcConsumerDiagnostics.Write(logs.Logger, LogLevel.Error, "BitcoinPool.OnSubmitAsync", failure);
        Assert.Equal(category, Assert.Single(logs.Records)["failure"].Value<string>());
        var rpc = JObject.Parse(Assert.Single(logs.Messages.Where(x => x.StartsWith("RPC consumer diagnostic ", StringComparison.Ordinal)))["RPC consumer diagnostic ".Length..]);
        Assert.Equal(category, rpc["failure"].Value<string>());
        logs.AssertSafe();
    }

    [Fact]
    public void CertificateReason_UsesBoundedStructuralCausesOnly()
    {
        Assert.Equal("access-denied", StratumDiagnostics.CertificateReason(new CryptographicException(Hostile, new UnauthorizedAccessException(Hostile))));
        Assert.Equal("file-not-found", StratumDiagnostics.CertificateReason(new DirectoryNotFoundException(Hostile)));
        Assert.Equal("file-io", StratumDiagnostics.CertificateReason(new CryptographicException(Hostile, new IOException(Hostile))));
        Assert.Equal("other", StratumDiagnostics.CertificateReason(new HostileException()));
        Exception failure = new FileNotFoundException(Hostile);
        for(var i = 0; i < 10; i++) failure = new CryptographicException(Hostile, failure);
        Assert.Equal("other", StratumDiagnostics.CertificateReason(failure));
    }

    [Fact]
    public async Task Listener_UnexpectedAcceptCleanupFailure_RetainsPortWithoutExceptionText()
    {
        using var logs = new Capture();
        using var container = new ContainerBuilder().Build();
        var bus = Substitute.For<IMessageBus>();
        bus.When(x => x.SendMessage(Arg.Is<TelemetryEvent>(x => x.Category == TelemetryCategory.Connections && x.Total == 0), Arg.Any<string>()))
            .Do(_ => throw new Exception(Hostile));
        var server = new DiagnosticServer(container, bus, logs.Logger)
        {
            Initialize = _ => throw new Exception(Hostile),
        };
        using var reservation = CreateReservation(new PoolEndpoint());
        using var lifetime = new CancellationTokenSource(Deadline);
        var run = server.RunAsync(lifetime.Token, reservation);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(reservation.Endpoint.IPEndPoint, lifetime.Token);
            await logs.WaitForEvent("ListenError");
        }
        finally { lifetime.Cancel(); await run.WaitAsync(Deadline); }
        var record = Assert.Single(logs.Records.Where(x => x["event"].Value<string>() == "ListenError"));
        Assert.Equal(reservation.Endpoint.IPEndPoint.Port, record["port"].Value<int>());
        Assert.Equal(0, server.ConnectionCount);
        using var rebound = StratumServer.CreateBoundSocket(reservation.Endpoint.IPEndPoint);
        logs.AssertSafe();
    }

    [Fact]
    public async Task Listener_FaultedTcpDispatchDuringDrain_IsSafeAndReleasesOwnership()
    {
        using var logs = new Capture();
        using var container = new ContainerBuilder().Build();
        // Complete observer cleanup synchronously when the drain log is observed, so
        // the real faulted dispatch remains in the snapshot until that path is exercised.
        var remove = new TaskCompletionSource();
        IDisposable subscription = null;
        var server = new DiagnosticServer(container, Substitute.For<IMessageBus>(), logs.Logger)
        {
            Initialize = connection => subscription = connection.Terminated.Subscribe(_ => throw new Exception(Hostile)),
            RemovingTask = _ => remove.Task,
        };
        logs.OnMessage = message => { if(message.Contains("\"event\":\"Drain\"", StringComparison.Ordinal)) remove.TrySetResult(); };
        using var reservation = CreateReservation(new PoolEndpoint());
        using var lifetime = new CancellationTokenSource(Deadline);
        var run = server.RunAsync(lifetime.Token, reservation);
        try
        {
            using(var client = new TcpClient(AddressFamily.InterNetwork))
            {
                await client.ConnectAsync(reservation.Endpoint.IPEndPoint, lifetime.Token);
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"mining.authorize\"}\n"), lifetime.Token);
                using var reader = new StreamReader(client.GetStream());
                Assert.True(JObject.Parse(await reader.ReadLineAsync(lifetime.Token))["result"].Value<bool>());
            }
            lifetime.Cancel();
            await logs.WaitForEvent("Drain");
            await run.WaitAsync(Deadline);
        }
        finally
        {
            remove.TrySetResult();
            lifetime.Cancel();
            await run.WaitAsync(Deadline);
            subscription?.Dispose();
        }
        Assert.All(logs.Records.Where(x => x["event"].Value<string>() == "Drain"), x => Assert.Null(x["connectionId"]));
        Assert.Equal(0, server.ConnectionCount);
        Assert.Equal(0, server.TrackedConnectionTaskCount);
        using var rebound = StratumServer.CreateBoundSocket(reservation.Endpoint.IPEndPoint);
        logs.AssertSafe();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task Listener_BannedIpLogging_HonorsPrivacyWithoutChangingBanDecision(bool? censor, bool alreadyConnected)
    {
        using var logs = new Capture();
        using var container = new ContainerBuilder().Build();
        var server = new DiagnosticServer(container, Substitute.For<IMessageBus>(), logs.Logger);
        server.CensorIps(censor);
        server.Bans.IsBanned(Arg.Any<IPAddress>()).Returns(!alreadyConnected);
        using var reservation = CreateReservation(new PoolEndpoint());
        using var lifetime = new CancellationTokenSource(Deadline);
        var run = server.RunAsync(lifetime.Token, reservation);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(reservation.Endpoint.IPEndPoint, lifetime.Token);
            if(alreadyConnected)
            {
                var request = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"mining.authorize\"}\n");
                await client.GetStream().WriteAsync(request, lifetime.Token);
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 1024, true);
                Assert.True(JObject.Parse(await reader.ReadLineAsync(lifetime.Token))["result"].Value<bool>());
                server.Bans.IsBanned(Arg.Any<IPAddress>()).Returns(true);
                await client.GetStream().WriteAsync(request, lifetime.Token);
            }
            while(!logs.Messages.Any(x => x.Contains("Disconnecting banned", StringComparison.Ordinal)))
                await Task.Delay(10, lifetime.Token);
        }
        finally { lifetime.Cancel(); await run.WaitAsync(Deadline); }
        var message = Assert.Single(logs.Messages.Where(x => x.Contains("Disconnecting banned", StringComparison.Ordinal)));
        Assert.EndsWith(IPAddress.Loopback.CensorOrReturn(censor == true).ToString(), message);
        if(censor == true) Assert.DoesNotContain("127.0.0.1", message);
        Assert.Equal(alreadyConnected ? 1 : 0, server.Requests);
        Assert.Equal(0, server.ConnectionCount);
        logs.AssertSafe();
    }

    private static StratumListenerReservation CreateReservation(PoolEndpoint settings)
    {
        var socket = StratumServer.CreateBoundSocket(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = new StratumEndpoint((IPEndPoint) socket.LocalEndPoint, settings);
        var reservation = new StratumListenerReservation("diagnostics-listener-test", endpoint, socket);
        reservation.Activate();
        return reservation;
    }

    private sealed class AuthorizationPool : BitcoinPool
    {
        private readonly bool authorized;
        public string Password { get; private set; }
        public IBanManager Bans { get; } = Substitute.For<IBanManager>();
        public AuthorizationPool(IComponentContext context, ILogger log, IMemoryCache cache, bool authorized, bool ban)
            : base(context, new JsonSerializerSettings(), Substitute.For<IConnectionFactory>(),
                Substitute.For<IStatsRepository>(), Substitute.For<IMapper>(), new StandardClock(),
                Substitute.For<IMessageBus>(), new RecyclableMemoryStreamManager(),
                new NicehashService(Substitute.For<IHttpClientFactory>(), cache))
        {
            this.authorized = authorized;
            logger = log;
            poolConfig = new PoolConfig { Id = "authorization-diagnostic-test" };
            clusterConfig = new ClusterConfig { Banning = new ClusterBanningConfig { BanOnLoginFailure = ban } };
            banManager = Bans;
            manager = new BitcoinJobManager(context, new StandardClock(), Substitute.For<IMessageBus>(), Substitute.For<IExtraNonceProvider>());
        }
        public Task Authorize(StratumConnection connection, JsonRpcRequest request, CancellationToken ct) =>
            OnAuthorizeAsync(connection, new Timestamped<JsonRpcRequest>(request, DateTimeOffset.UtcNow), ct);
        protected override Task<bool> ValidateWorkerAsync(BitcoinWorkerContext worker, string miner, string password, CancellationToken ct)
        {
            Password = password;
            return Task.FromResult(authorized);
        }
    }

    private sealed class XelisLookup : XelisJobManager
    {
        public XelisLookup(IComponentContext context, IMessageBus bus, ILogger log)
            : base(context, new StandardClock(), bus, Substitute.For<IExtraNonceProvider>()) { logger = log; }
    }
    private sealed class KaspaLookup : KaspaJobManager
    {
        public KaspaLookup(IComponentContext context, IMessageBus bus, ILogger log)
            : base(context, bus, new StandardClock(), Substitute.For<IExtraNonceProvider>()) { logger = log; }
    }
    private sealed class AlephiumLookup : AlephiumJobManager
    {
        public AlephiumLookup(IComponentContext context, IMessageBus bus, ILogger log)
            : base(context, bus, new StandardClock(), Substitute.For<IExtraNonceProvider>()) { logger = log; }
    }

    private sealed class HostileException : Exception
    {
        public override string Message => throw new Exception("Diagnostic accessed Message");
        public override string ToString() => throw new Exception("Diagnostic accessed ToString");
    }

    private sealed class DiagnosticServer : StratumServer
    {
        public DiagnosticServer(IComponentContext context, IMessageBus bus, ILogger log, string policy = "default")
            : base(context, bus, new RecyclableMemoryStreamManager(), new StandardClock())
        {
            logger = log;
            clusterConfig = new ClusterConfig
            {
                Logging = new ClusterLoggingConfig(),
                Banning = policy == "missing" ? null : new ClusterBanningConfig
                {
                    BanOnJunkReceive = policy == "default" ? null : policy == "enabled",
                },
            };
            poolConfig = new PoolConfig { Id = "stratum-diagnostic-test" };
            banManager = Bans;
        }
        public IBanManager Bans { get; } = Substitute.For<IBanManager>();
        public int Requests { get; private set; }
        public void CensorIps(bool? censor) => clusterConfig.Logging = censor.HasValue
            ? new ClusterLoggingConfig { GPDRCompliant = censor.Value } : null;
        public int Errors { get; private set; }
        public int Completions { get; private set; }
        public bool ThrowInTerminalCallback { get; init; }
        public Action<StratumConnection> Initialize { get; set; }
        public Func<string, Task> RemovingTask { get; set; }
        public void Register(StratumConnection connection) => RegisterConnection(connection);
        public int ConnectionCount => connections.Count;
        public Func<StratumConnection, JsonRpcRequest, CancellationToken, Task> Handler { get; set; }
        public Task Request(StratumConnection connection, JsonRpcRequest request, CancellationToken ct) => base.OnRequestAsync(connection, request, ct);
        public void Complete(StratumConnection connection)
        {
            Completions++;
            OnConnectionComplete(connection);
            if(ThrowInTerminalCallback) throw new Exception(Hostile);
        }
        public void Error(StratumConnection connection, Exception error)
        {
            Errors++;
            OnConnectionError(connection, error);
            if(ThrowInTerminalCallback) throw new Exception(Hostile);
        }
        protected override void OnConnect(StratumConnection connection, IPEndPoint endpoint) => Initialize?.Invoke(connection);
        protected override Task BeforeConnectionTaskRemovalAsync(string id) => RemovingTask?.Invoke(id) ?? Task.CompletedTask;
        protected override Task OnRequestAsync(StratumConnection connection, Timestamped<JsonRpcRequest> request, CancellationToken ct)
        {
            Requests++;
            return Handler?.Invoke(connection, request.Value, ct) ?? connection.RespondAsync(true, request.Value.Id);
        }
    }

    private sealed class TcpSession : IAsyncDisposable
    {
        public TcpClient Client { get; } = new(AddressFamily.InterNetwork);
        public StreamReader Reader { get; private set; }
        public Task Dispatch { get; private set; }
        private readonly CancellationTokenSource lifetime = new(Deadline);
        private Socket accepted;
        private DiagnosticServer server;

        public static async Task<TcpSession> Start(ILogger logger, DiagnosticServer server, PoolEndpoint settings = null,
            X509Certificate2 certificate = null)
        {
            var result = new TcpSession { server = server };
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var endpoint = new StratumEndpoint((IPEndPoint) listener.LocalEndpoint, settings ?? new PoolEndpoint());
                await result.Client.ConnectAsync(endpoint.IPEndPoint, result.lifetime.Token);
                result.accepted = await listener.AcceptSocketAsync(result.lifetime.Token);
                var connection = new StratumConnection(logger, new RecyclableMemoryStreamManager(),
                    new StandardClock(), CorrelationIdGenerator.GetNextId(), false);
                server.Initialize?.Invoke(connection);
                server.Register(connection);
                result.Dispatch = connection.DispatchAsync(result.accepted, result.lifetime.Token, endpoint,
                    (IPEndPoint) result.accepted.RemoteEndPoint, certificate, server.Request, server.Complete, server.Error);
                result.Reader = new StreamReader(result.Client.GetStream(), Encoding.UTF8, false, 1024, true);
                return result;
            }
            catch
            {
                await result.DisposeAsync();
                throw;
            }
        }
        public async Task Send(string text) => await Client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(text), lifetime.Token);
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                if(Dispatch != null)
                {
                    await Dispatch.WaitAsync(Deadline);
                    Assert.Equal(0, server.ConnectionCount);
                    Assert.Equal(1, server.Errors + server.Completions);
                }
            }
            finally
            {
                lifetime.Cancel();
                Reader?.Dispose();
                accepted?.Dispose();
                lifetime.Dispose();
            }
        }
    }

    private sealed class Capture : IDisposable
    {
        private readonly LogFactory factory = new();
        private readonly CaptureTarget target = new() { Layout = "${message}${exception:format=tostring}${all-event-properties}" };
        public Capture()
        {
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Trace, LogLevel.Fatal, target);
            factory.Configuration = configuration;
            Logger = factory.GetLogger("stratum-diagnostic-test");
        }
        public ILogger Logger { get; }
        public Action<string> OnMessage { set => target.OnMessage = value; }
        public string[] Messages => target.Messages.ToArray();
        public async Task WaitForEvent(string operation)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while(!Records.Any(x => x["event"].Value<string>() == operation))
                await Task.Delay(10, deadline.Token);
        }
        public JObject[] Records => target.Messages.Where(x => x.StartsWith("Stratum diagnostic ", StringComparison.Ordinal))
            .Select(x => JObject.Parse(x["Stratum diagnostic ".Length..])).ToArray();
        public void AssertSafe()
        {
            Assert.NotEmpty(target.Messages);
            Assert.All(Records, record => Assert.DoesNotContain(record.Properties(), x => x.Value.Type == JTokenType.Null));
            Assert.All(target.Messages, message =>
            {
                Assert.DoesNotContain(Secret, message);
                Assert.DoesNotContain(message, c => c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029');
                Assert.True(message.Length < 512);
            });
            Assert.All(target.Events, item =>
            {
                Assert.Null(item.Exception);
                Assert.Empty(item.Properties);
                Assert.DoesNotContain(item.Parameters ?? Array.Empty<object>(), x => x is Exception or JsonRpcRequest or JToken);
                Assert.All((item.Parameters ?? Array.Empty<object>()).OfType<string>(), value =>
                    Assert.DoesNotContain(Secret, value));
            });
        }
        public void Dispose() => factory.Dispose();
    }

    private sealed class CaptureTarget : TargetWithLayout
    {
        public Action<string> OnMessage { get; set; }
        public ConcurrentQueue<string> Messages { get; } = new();
        public ConcurrentQueue<LogEventInfo> Events { get; } = new();
        protected override void Write(LogEventInfo logEvent)
        {
            Events.Enqueue(logEvent);
            var message = Layout.Render(logEvent);
            Messages.Enqueue(message);
            OnMessage?.Invoke(message);
        }
    }
}
