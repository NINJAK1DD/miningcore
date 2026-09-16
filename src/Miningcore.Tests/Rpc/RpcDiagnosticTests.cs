using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Rpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Targets;
using NSubstitute;
using Xunit;
using ZeroMQ;

namespace Miningcore.Tests.Rpc;

[CollectionDefinition(Name, DisableParallelization = true)]
public class RpcDiagnosticCollection
{
    public const string Name = "RPC diagnostic global settings";
}

[Collection(RpcDiagnosticCollection.Name)]
public class RpcDiagnosticTests
{
    private const string Secret = "synthetic-private-secret";
    private const string UnsafeText = Secret + "\r\n\u0085\u2028\u2029forged-log";
    private static readonly string EncodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("rpc-user:" + Secret));

    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "rpc-error")]
    [InlineData(true, "rpc-error")]
    [InlineData(false, "http-error")]
    [InlineData(true, "http-error")]
    [InlineData(false, "invalid-json")]
    [InlineData(true, "invalid-json")]
    [InlineData(false, "custom-success")]
    [InlineData(true, "invalid-id")]
    public async Task HttpAndBatch_KeepWireContractWithoutLoggingPayloads(bool batch, string outcome)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var logs = new CapturedLogs();
        var observed = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await Server.Start(async context =>
        {
            try
            {
                Assert.Contains(Secret, context.Request.QueryString.Value);
                Assert.Equal("Basic " + EncodedCredentials,
                    context.Request.Headers.Authorization.ToString());
                using var reader = new StreamReader(context.Request.Body);
                var request = JToken.Parse(await reader.ReadToEndAsync(deadline.Token));
                observed.TrySetResult(request);
                context.Response.StatusCode = outcome == "http-error" ? 500 : 200;
                if(outcome == "invalid-json")
                    await context.Response.WriteAsync("{\"" + Secret + "\": not-json}", deadline.Token);
                else
                {
                    JObject Reply(JToken item) => new()
                    {
                        ["id"] = outcome == "invalid-id" ? UnsafeText : item["id"],
                        ["result"] = outcome == "rpc-error" ? null : new JObject { ["privateKey"] = UnsafeText },
                        ["error"] = outcome == "rpc-error" ? new JObject
                        {
                            ["code"] = -13, ["message"] = UnsafeText, ["data"] = new JObject { ["password"] = Secret },
                        } : null,
                    };
                    JToken response = batch ? new JArray(((JArray) request).Reverse().Select(Reply)) : Reply(request);
                    await context.Response.WriteAsync(response.ToString(Formatting.None), deadline.Token);
                }
            }
            catch(Exception ex) { observed.TrySetException(ex); throw; }
        });
        var bus = Substitute.For<IMessageBus>();
        var client = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), bus, "test");
        var customMethod = UnsafeText + new string('x', 10000);
        var method = outcome == "custom-success" ? customMethod : "walletpassphrase";
        var payload = new object[] { UnsafeText, 30 };
        var results = batch
            ? await client.ExecuteBatchAsync(logs.Logger, deadline.Token,
                new RpcRequest("walletpassphrase", payload), new RpcRequest(customMethod, payload))
            : new[] { await client.ExecuteAsync<JToken>(logs.Logger, method, deadline.Token, payload) };
        var received = await observed.Task.WaitAsync(deadline.Token);
        var requests = batch ? ((JArray) received).ToArray() : new[] { received };
        Assert.Equal(batch ? 2 : 1, requests.Length);
        Assert.Equal(method, requests[0]["method"].Value<string>());
        if(batch) Assert.Equal(customMethod, requests[1]["method"].Value<string>());
        Assert.All(requests, request => Assert.Equal(UnsafeText, request["params"][0].Value<string>()));
        Assert.All(results, result =>
        {
            if(outcome == "invalid-json")
                Assert.IsAssignableFrom<JsonException>(result.Error.InnerException);
            else if(outcome == "invalid-id")
                Assert.IsType<InvalidDataException>(result.Error.InnerException);
            else if(outcome == "rpc-error")
            {
                Assert.Equal(-13, result.Error.Code);
                Assert.Equal(UnsafeText, result.Error.Message); // Diagnostics must not rewrite caller data.
                Assert.Equal(Secret, ((JObject) result.Error.Data)["password"].Value<string>());
            }
            else
                Assert.Equal(UnsafeText, result.Response["privateKey"].Value<string>());
        });
        logs.AssertSafe();
        Assert.Contains(logs.Messages, x => x.Contains("\"stage\":\"Request\""));
        Assert.Contains(logs.Messages, x => x.Contains("\"stage\":\"Response\""));
        if(outcome == "invalid-json")
            Assert.Contains(logs.Messages, x => x.Contains("\"failure\":\"json\""));
        var telemetry = bus.ReceivedCalls().SelectMany(x => x.GetArguments()).OfType<TelemetryEvent>().ToArray();
        if(outcome == "invalid-json")
            Assert.Empty(telemetry);
        else
            Assert.Equal(batch ? "batch" : outcome == "custom-success" ? "other" : "walletpassphrase",
                Assert.Single(telemetry).Info);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_SerializationFailurePreservesThrowContractWithoutExceptionLogging(bool throwOnError)
    {
        using var logs = new CapturedLogs();
        var error = new InvalidOperationException(UnsafeText);
        var settings = new JsonSerializerSettings { Converters = { new ThrowingConverter(error) } };
        var client = new RpcClient(new DaemonEndpointConfig { Host = "127.0.0.1", Port = 1 },
            settings, Substitute.For<IMessageBus>(), "test");
        if(throwOnError)
            Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ExecuteAsync<JToken>(logs.Logger, UnsafeText, CancellationToken.None, new object(), true)));
        else
        {
            var result = await client.ExecuteAsync<JToken>(logs.Logger, UnsafeText, CancellationToken.None, new object());
            Assert.Same(error, result.Error.InnerException);
        }
        logs.AssertSafe();
        Assert.Contains("\"stage\":\"Failure\"", Assert.Single(logs.Messages));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WebSocket_WireAndReconnectDiagnosticsExcludeSecrets(int observerFailure)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var logs = new CapturedLogs(NLog.LogLevel.Debug);
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var message = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = Encoding.UTF8.GetBytes(new JObject { ["privateKey"] = UnsafeText }.ToString(Formatting.None));
        await using var server = await Server.Start(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var received = new MemoryStream();
            var buffer = new byte[4096];
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
                received.Write(buffer, 0, part.Count);
            } while(!part.EndOfMessage);
            observed.TrySetResult(Encoding.UTF8.GetString(received.ToArray()));
            await socket.SendAsync(new ArraySegment<byte>(response), WebSocketMessageType.Text, true, deadline.Token);
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            catch(OperationCanceledException) { }
        });
        var client = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), Substitute.For<IMessageBus>(), "test");
        using var subscription = client.WebsocketSubscribe(logs.Logger, deadline.Token, server.Endpoint(),
            UnsafeText, new object[] { UnsafeText }).Subscribe(new Observer(bytes =>
            {
                message.TrySetResult(bytes);
                if(observerFailure == 1) throw new InvalidOperationException(UnsafeText);
            }));
        var request = JObject.Parse(await observed.Task.WaitAsync(deadline.Token));
        Assert.Equal(UnsafeText, request["method"].Value<string>());
        Assert.Equal(UnsafeText, request["params"][0].Value<string>());
        Assert.Equal(response, await message.Task.WaitAsync(deadline.Token));
        if(observerFailure != 0)
            await logs.WaitForFailure();
        deadline.Cancel();
        logs.AssertSafe();
        Assert.Contains(logs.Messages, x => x.Contains("\"stage\":\"Subscribe\""));
        Assert.Contains(logs.Messages, x => x.Contains("\"stage\":\"Receive\""));
        Assert.All(logs.Messages, x => Assert.Contains("\"endpointIndex\":null", x));
    }

    [Fact]
    public void Diagnostics_HaveFixedBoundedSchemaEvenForUnknownExceptionAndMethod()
    {
        using var logs = new CapturedLogs();
        foreach(var transport in Enum.GetValues<RpcDiagnostics.Transport>())
        foreach(var stage in Enum.GetValues<RpcDiagnostics.Stage>())
            RpcDiagnostics.Write(logs.Logger, NLog.LogLevel.Error, transport, stage,
                new string('x', 100000) + UnsafeText, int.MaxValue, 599, long.MaxValue,
                long.MaxValue, new InvalidOperationException(UnsafeText));
        logs.AssertSafe();
    }

    [Fact]
    public void Diagnostics_IgnoreGlobalJsonSettingsAndHonorDisabledLogging()
    {
        using var logs = new CapturedLogs();
        var original = JsonConvert.DefaultSettings;
        try
        {
            JsonConvert.DefaultSettings = () => throw new InvalidOperationException(UnsafeText);
            RpcDiagnostics.Write(logs.Logger, NLog.LogLevel.Debug, RpcDiagnostics.Transport.Http,
                RpcDiagnostics.Stage.Failure, UnsafeText, failure: new InvalidOperationException(UnsafeText));
            logs.AssertSafe();
            logs.Disable();
            RpcDiagnostics.Write(logs.Logger, NLog.LogLevel.Debug, RpcDiagnostics.Transport.Http,
                RpcDiagnostics.Stage.Request, UnsafeText);
            Assert.Single(logs.Messages);
        }
        finally { JsonConvert.DefaultSettings = original; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_CancellationDoesNotExposeEndpointOrPayload(bool batch)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancelled = new CancellationTokenSource();
        using var logs = new CapturedLogs();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await Server.Start(async context =>
        {
            received.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            catch(OperationCanceledException) { }
        });
        var client = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), Substitute.For<IMessageBus>(), "test");
        var pending = batch
            ? client.ExecuteBatchAsync(logs.Logger, cancelled.Token, new RpcRequest(UnsafeText, new[] { Secret }))
            : Wrap(client.ExecuteAsync<JToken>(logs.Logger, UnsafeText, cancelled.Token, new[] { Secret }));
        await received.Task.WaitAsync(deadline.Token);
        cancelled.Cancel();
        var results = await pending.WaitAsync(deadline.Token);
        Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(results).Error.InnerException);
        logs.AssertSafe();
        Assert.Contains(logs.Messages, x => x.Contains("\"failure\":\"cancelled\""));

        static async Task<RpcResponse<JToken>[]> Wrap(Task<RpcResponse<JToken>> task) => new[] { await task };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task WebSocket_ConnectAndSerializationFailuresHaveSafeDiagnostics(int failureKind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var logs = new CapturedLogs(NLog.LogLevel.Debug);
        var serializationFailure = failureKind != 0;
        var reconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = 0;
        await using var server = await Server.Start(async context =>
        {
            if(Interlocked.Increment(ref connections) == 2) reconnect.TrySetResult();
            if(!serializationFailure)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync(UnsafeText);
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            catch(OperationCanceledException) { }
        });
        var client = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), Substitute.For<IMessageBus>(), "test");
        Exception failure = failureKind switch
        {
            2 => new TaskCanceledException(UnsafeText),
            3 => new ObjectDisposedException(UnsafeText),
            _ => new InvalidOperationException(UnsafeText),
        };
        var settings = new JsonSerializerSettings { Converters = { new ThrowingConverter(failure) } };
        using var subscription = client.WebsocketSubscribe(logs.Logger, deadline.Token, server.Endpoint(),
            UnsafeText, new[] { Secret }, serializationFailure ? settings : null).Subscribe(new Observer(_ => { }));
        await logs.WaitForFailure();
        if(failureKind >= 2) await reconnect.Task.WaitAsync(deadline.Token);
        deadline.Cancel();
        logs.AssertSafe();
        var category = failureKind switch { 0 => "websocket", 2 => "cancelled", 3 => "disposed", _ => "invalid-operation" };
        Assert.Contains(logs.Messages, x => x.Contains("\"failure\":\"" + category + "\""));
        if(!serializationFailure)
            Assert.Contains(logs.Messages, x => x.Contains("\"httpStatus\":401"));
    }

    [Theory]
    [InlineData("listunspent")]
    [InlineData("createauxblock")]
    [InlineData("submitauxblock")]
    [InlineData("get_info")]
    [InlineData("get_miner_work")]
    [InlineData("get_difficulty")]
    [InlineData("getblockheaderbyhash")]
    [InlineData("ctxc_getWork")]
    [InlineData("ctxc_submitWork")]
    [InlineData("subscribe")]
    public async Task BuiltInMethods_PreserveDistinctWireAndTelemetryLabels(string method)
    {
        using var logs = new CapturedLogs();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = await Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync(deadline.Token));
            Assert.Equal(method, request["method"].Value<string>());
            await context.Response.WriteAsync(new JObject
            {
                ["id"] = request["id"], ["result"] = new JObject(),
            }.ToString(Formatting.None), deadline.Token);
        });
        var bus = Substitute.For<IMessageBus>();
        var client = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), bus, "test");
        Assert.Null((await client.ExecuteAsync<JToken>(logs.Logger, method, deadline.Token)).Error);
        var telemetry = bus.ReceivedCalls().SelectMany(x => x.GetArguments()).OfType<TelemetryEvent>();
        Assert.Equal(method, Assert.Single(telemetry).Info);
        logs.AssertSafe();
        Assert.All(logs.Messages, x => Assert.Equal(method, JObject.Parse(x["RPC diagnostic ".Length..])["method"].Value<string>()));
    }

    [Fact]
    public void BuiltInCatalog_CoversCommandConstantsWithoutAcceptingDynamicPrefixes()
    {
        var types = typeof(RpcClient).Assembly.GetTypes().Where(x =>
            x.Namespace?.StartsWith("Miningcore.Blockchain.", StringComparison.Ordinal) == true &&
            x.Name.EndsWith("Commands", StringComparison.Ordinal));
        foreach(var type in types)
        foreach(var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.IsLiteral && x.FieldType == typeof(string)))
        {
            var value = (string) field.GetRawConstantValue();
            var methods = type.Name == "EthCommands" && value.StartsWith('_')
                ? new[] { "eth" + value, "ctxc" + value } : new[] { value };
            foreach(var method in methods)
            {
                Assert.InRange(method.Length, 1, 64);
                Assert.Equal(method, RpcDiagnostics.Method(method));
            }
        }
        Assert.Null(RpcDiagnostics.Method(null));
        Assert.Equal("other", RpcDiagnostics.Method(Secret + "_getWork"));
        Assert.Equal("other", RpcDiagnostics.Method("get_info" + UnsafeText));
        Assert.Equal("other", RpcDiagnostics.Method("getblockhash"));
    }

    [Fact]
    public void CatalogFailureDegradesToAnEmptyVocabulary()
    {
        Assert.Empty(RpcMethodCatalog.BuildSafely(() => throw new InvalidOperationException(UnsafeText)));
        Assert.Empty(RpcMethodCatalog.BuildSafely(() => null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalWorkerFaultPreservesMergedPollingFallback(bool externalStratum)
    {
        using var logs = new CapturedLogs(NLog.LogLevel.Info);
        using var polling = new Subject<byte[]>();
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task observed = null;
        var push = Observable.Create<byte[]>(_ =>
        {
            observed = RpcClient.ObserveSubscriptionWorkerAsync(worker.Task, logs.Logger, UnsafeText, 2);
            return Disposable.Empty;
        }).Publish().RefCount();
        // Match the job managers' push + polling Merge, including the external
        // Stratum subscriber without an OnError handler. No clock sleeps needed.
        var jobs = push.Merge(polling);
        var delivered = new List<byte[]>();
        Exception error = null;
        using var subscription = externalStratum
            ? jobs.Do(delivered.Add).Subscribe()
            : jobs.Subscribe(delivered.Add, ex => error = ex);
        var before = new byte[] { 1 };
        var after = new byte[] { 2 };
        polling.OnNext(before);
        worker.SetException(new Exception(UnsafeText));
        await observed.WaitAsync(TimeSpan.FromSeconds(10));
        polling.OnNext(after);
        Assert.Null(error);
        Assert.Equal(new[] { before, after }, delivered);
        Assert.True(polling.HasObservers);
        logs.AssertSafe();
        var diagnostic = Assert.Single(logs.Messages);
        Assert.Contains("\"endpointIndex\":2", diagnostic);
        Assert.Contains("\"stage\":\"Failure\"", diagnostic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCallbackFailureHasOnlyFixedSafeDiagnostic(bool zmq)
    {
        using var logs = new CapturedLogs(NLog.LogLevel.Info);
        var lifetime = new RpcSubscriptionLifetime(CancellationToken.None, () =>
            RpcDiagnostics.Write(logs.Logger, NLog.LogLevel.Error,
                zmq ? RpcDiagnostics.Transport.Zmq : RpcDiagnostics.Transport.WebSocket,
                RpcDiagnostics.Stage.CancellationCallbackFailure, endpointIndex: 2));
        using var callback = lifetime.Token.Register(() => throw new Exception(UnsafeText));
        using var unsubscribe = Disposable.Create(lifetime.Cancel);
        try
        {
            unsubscribe.Dispose();
            Assert.True(lifetime.Token.IsCancellationRequested);
            logs.AssertSafe();
            var diagnostic = Assert.Single(logs.Messages);
            Assert.Contains("\"stage\":\"CancellationCallbackFailure\"", diagnostic);
            Assert.Contains("\"failure\":null", diagnostic);
        }
        finally { await lifetime.CompleteAsync(); }
        await lifetime.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void EndpointLookupUsesFullConfigurationAndDegradesToUnknown()
    {
        var first = new DaemonEndpointConfig { Host = Secret };
        var selected = new DaemonEndpointConfig { Host = Secret };
        var configured = new[] { first, new DaemonEndpointConfig(), selected };
        Assert.Equal(1, RpcDiagnostics.EndpointIndex(configured, first));
        Assert.Equal(3, RpcDiagnostics.EndpointIndex(configured, selected));
        Assert.Null(RpcDiagnostics.EndpointIndex(configured, new DaemonEndpointConfig { Host = Secret }));
        Assert.Null(RpcDiagnostics.EndpointIndex(configured, null));
        Assert.Null(RpcDiagnostics.EndpointIndex(null, selected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task WebSocketDiagnosticIndexCannotRejectUsableEndpoint(int? index)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var logs = new CapturedLogs(NLog.LogLevel.Debug);
        var message = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await Server.Start(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.SendAsync(new ArraySegment<byte>(new byte[] { 1 }), WebSocketMessageType.Text, true, deadline.Token);
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            catch(OperationCanceledException) { }
        });
        var client = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), Substitute.For<IMessageBus>(), "test");
        using var subscription = client.WebsocketSubscribe(logs.Logger, deadline.Token, server.Endpoint(),
            "eth_subscribe", endpointIndex: index).Subscribe(bytes => message.TrySetResult(bytes));
        Assert.Equal(new byte[] { 1 }, await message.Task.WaitAsync(deadline.Token));
        logs.AssertSafe();
        Assert.All(logs.Messages, record => Assert.Equal(index > 0 ? index : null,
            JObject.Parse(record["RPC diagnostic ".Length..])["endpointIndex"].Value<int?>()));
    }

    [Fact]
    public void Failures_PreserveSafeCodesAndDistinguishTimeoutFromCancellation()
    {
        using var logs = new CapturedLogs();
        var cases = new (Exception Error, string Category, int? Code, int? Status)[]
        {
            (new TaskCanceledException(UnsafeText, new TimeoutException(UnsafeText)), "timeout", null, null),
            (new OperationCanceledException(UnsafeText), "cancelled", null, null),
            (new System.Net.Http.HttpRequestException(UnsafeText, null, HttpStatusCode.Forbidden), "http", 0, 403),
            (new WebSocketException(WebSocketError.NotAWebSocket, UnsafeText), "websocket", (int) WebSocketError.NotAWebSocket, null),
            (new ZException(ZError.EINVAL), "zmq", ZError.EINVAL.Number, null),
            (new InvalidOperationException(UnsafeText), "invalid-operation", null, null),
        };
        foreach(var item in cases)
            RpcDiagnostics.Write(logs.Logger, NLog.LogLevel.Error, RpcDiagnostics.Transport.Http,
                RpcDiagnostics.Stage.Failure, failure: item.Error);
        logs.AssertSafe();
        var records = logs.Messages.Select(x => JObject.Parse(x["RPC diagnostic ".Length..])).ToArray();
        for(var i = 0; i < cases.Length; i++)
        {
            Assert.Equal(cases[i].Category, records[i]["failure"].Value<string>());
            Assert.Equal(cases[i].Code, records[i]["failureCode"].Value<int?>());
            Assert.Equal(cases[i].Status, records[i]["httpStatus"].Value<int?>());
            Assert.Equal(JTokenType.Null, records[i]["method"].Type);
        }
    }

    [Fact]
    public void Zmq_DeliversSecretTopicAndPayloadWithoutLoggingThem()
    {
        using var logs = new CapturedLogs(NLog.LogLevel.Debug);
        using var publisher = new ZSocket(ZSocketType.XPUB);
        publisher.ReceiveTimeout = TimeSpan.FromSeconds(10);
        publisher.Bind("tcp://127.0.0.1:*");
        var endpoint = new DaemonEndpointConfig();
        var client = new RpcClient(endpoint, new JsonSerializerSettings(), Substitute.For<IMessageBus>(), "test");
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = client.ZmqSubscribe(logs.Logger, CancellationToken.None,
            new Dictionary<DaemonEndpointConfig, (string, string)> { [endpoint] = (publisher.LastEndpoint, UnsafeText) },
            new[] { new DaemonEndpointConfig(), new DaemonEndpointConfig(), endpoint })
            .Subscribe(new ZmqObserver(message =>
            {
                using(message)
                    received.TrySetResult(new[] { message[0].ReadString(), message[1].ReadString(), Thread.CurrentThread.Name });
            }));
        // XPUB acknowledges the subscription: no sleep/slow-joiner race before publishing.
        using var subscribed = publisher.ReceiveFrame();
        Assert.NotNull(subscribed);
        using var outgoing = new ZMessage { new ZFrame(UnsafeText), new ZFrame(Secret) };
        publisher.SendMessage(outgoing);
        var result = received.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Assert.Equal(UnsafeText, result[0]);
        Assert.Equal(Secret, result[1]);
        Assert.StartsWith("ZMQ subscriber ", result[2]);
        Assert.DoesNotContain(Secret, result[2]);
        logs.AssertSafe();
        var diagnostic = JObject.Parse(Assert.Single(logs.Messages)["RPC diagnostic ".Length..]);
        Assert.Equal(JTokenType.Null, diagnostic["method"].Type);
        // The actual publisher maps to daemon 3, not filtered-map position 1.
        Assert.Equal(3, diagnostic["endpointIndex"].Value<int>());
        Assert.Equal("ZMQ subscriber " + diagnostic["endpointIndex"].Value<long>(), result[2]);
    }

    [Fact]
    public async Task ZmqEndpointOrdinalsSurviveRefCountResubscription()
    {
        using var logs = new CapturedLogs();
        var first = new DaemonEndpointConfig();
        var second = new DaemonEndpointConfig();
        var client = new RpcClient(first, new JsonSerializerSettings(), Substitute.For<IMessageBus>(), "test");
        var observable = client.ZmqSubscribe(logs.Logger, CancellationToken.None,
            new Dictionary<DaemonEndpointConfig, (string, string)>
            {
                [second] = ("invalid-" + Secret + "://second", UnsafeText),
                [first] = ("invalid-" + Secret + "://first", UnsafeText),
            }, new[] { new DaemonEndpointConfig(), first, new DaemonEndpointConfig(), second });
        for(var attempt = 0; attempt < 2; attempt++)
        {
            var offset = logs.Messages.Length;
            using(var subscription = observable.Subscribe(new ZmqObserver(_ => { })))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while(logs.Messages.Skip(offset).Select(x =>
                    JObject.Parse(x["RPC diagnostic ".Length..])["endpointIndex"].Value<int>()).Distinct().Count() < 2)
                    await logs.WaitForFailures(1, deadline.Token);
                var indices = logs.Messages.Skip(offset)
                    .Select(x => JObject.Parse(x["RPC diagnostic ".Length..])["endpointIndex"].Value<int>())
                    .Distinct().OrderBy(x => x).ToArray();
                Assert.Equal(new[] { 2, 4 }, indices);
            }
        }
        logs.AssertSafe();
    }

    [Fact]
    public async Task Zmq_RejectsSecretEndpointWithoutLoggingIt()
    {
        using var logs = new CapturedLogs(NLog.LogLevel.Debug);
        var endpoint = new DaemonEndpointConfig();
        var client = new RpcClient(endpoint, new JsonSerializerSettings(), Substitute.For<IMessageBus>(), "test");
        using var subscription = client.ZmqSubscribe(logs.Logger, CancellationToken.None,
            new Dictionary<DaemonEndpointConfig, (string, string)> { [endpoint] = ("invalid-" + Secret + "://" + Secret, UnsafeText) },
            configuredEndpoints: null)
            .Subscribe(new ZmqObserver(_ => { }));
        await logs.WaitForFailure();
        logs.AssertSafe();
        Assert.Contains(logs.Messages, x => x.Contains("\"failure\":\"zmq\""));
        Assert.All(logs.Messages, x => Assert.Contains("\"endpointIndex\":null", x));
    }

    internal sealed class CapturedLogs : IDisposable
    {
        private readonly LogFactory factory = new();
        private readonly ConcurrentLogTarget target = new() { Layout = "${message}${exception:format=tostring}" };
        public CapturedLogs(NLog.LogLevel minimum = null)
        {
            var config = new NLog.Config.LoggingConfiguration();
            config.AddRule(minimum ?? NLog.LogLevel.Trace, NLog.LogLevel.Fatal, target);
            factory.Configuration = config;
            Logger = factory.GetLogger("rpc-diagnostics-test");
        }
        public NLog.ILogger Logger { get; }
        public void Disable() => factory.Configuration = new NLog.Config.LoggingConfiguration();
        public string[] Messages => target.Messages.ToArray();
        public async Task WaitForFailure()
        {
            try { await target.Failure.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch(TimeoutException) { Assert.True(false, "Expected an RPC Failure diagnostic within 10 seconds"); }
        }
        public async Task WaitForFailures(int count, CancellationToken ct)
        {
            for(var i = 0; i < count; i++)
            {
                try { Assert.True(await target.Failures.WaitAsync(TimeSpan.FromSeconds(10), ct), "Expected another RPC Failure diagnostic within 10 seconds"); }
                catch(OperationCanceledException) { Assert.True(false, "Endpoint Failure diagnostics did not arrive before the test deadline"); }
            }
        }
        public void AssertSafe()
        {
            Assert.NotEmpty(Messages);
            foreach(var message in Messages)
            {
                Assert.DoesNotContain(Secret, message);
                Assert.DoesNotContain("rpc-user", message);
                Assert.DoesNotContain(EncodedCredentials, message);
                Assert.DoesNotContain("127.0.0.1", message);
                Assert.True(message.Length < 512);
                Assert.DoesNotContain(message, c => c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029');
                Assert.StartsWith("RPC diagnostic ", message);
                var data = JObject.Parse(message["RPC diagnostic ".Length..]);
                Assert.Equal(new[] { "transport", "stage", "method", "batchCount", "httpStatus", "bytes", "httpResponseChars", "elapsedMs", "endpointIndex", "failure", "failureCode" },
                    data.Properties().Select(x => x.Name).ToArray());
            }
        }
        public void Dispose() => factory.Dispose();
    }

    private sealed class ConcurrentLogTarget : TargetWithLayout
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public TaskCompletionSource Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim Failures { get; } = new(0);
        protected override void Write(LogEventInfo logEvent)
        {
            var text = Layout.Render(logEvent);
            Messages.Enqueue(text);
            if(text.Contains("\"stage\":\"Failure\"", StringComparison.Ordinal))
            {
                Failure.TrySetResult();
                Failures.Release();
            }
        }
    }

    private sealed class ZmqObserver(Action<ZMessage> next) : IObserver<ZMessage>
    {
        public void OnNext(ZMessage value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class ThrowingConverter(Exception error) : JsonConverter
    {
        public override bool CanConvert(Type objectType) => true;
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) => throw error;
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => throw error;
    }

    private sealed class Observer(Action<byte[]> next) : IObserver<byte[]>
    {
        public void OnNext(byte[] value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    internal sealed class Server(WebApplication app, Uri address) : IAsyncDisposable
    {
        public DaemonEndpointConfig Endpoint() => new()
        {
            Host = address.Host, Port = address.Port, HttpPath = "/rpc?key=" + Secret,
            User = "rpc-user", Password = Secret,
        };
        public static async Task<Server> Start(RequestDelegate handler)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders(); // Isolate the RPC log capture from server request logs.
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.UseWebSockets();
            app.Run(handler);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            return new Server(app, new Uri(addresses.Addresses.Single()));
        }
        public async ValueTask DisposeAsync()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(stop.Token);
            await app.DisposeAsync();
        }
    }
}
