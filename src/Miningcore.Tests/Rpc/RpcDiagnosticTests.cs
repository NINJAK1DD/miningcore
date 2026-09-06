using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
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

namespace Miningcore.Tests.Rpc;

[CollectionDefinition("RPC diagnostic global settings", DisableParallelization = true)]
public class RpcDiagnosticCollection { }

[Collection("RPC diagnostic global settings")]
public class RpcDiagnosticTests
{
    private const string Secret = "synthetic-private-secret";
    private const string UnsafeText = Secret + "\r\n\u0085\u2028\u2029forged-log";

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
                Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("rpc-user:" + Secret)),
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebSocket_WireAndReconnectDiagnosticsExcludeSecrets(bool observerFails)
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
                if(observerFails) throw new InvalidOperationException(UnsafeText);
            }));
        var request = JObject.Parse(await observed.Task.WaitAsync(deadline.Token));
        Assert.Equal(UnsafeText, request["method"].Value<string>());
        Assert.Equal(UnsafeText, request["params"][0].Value<string>());
        Assert.Equal(response, await message.Task.WaitAsync(deadline.Token));
        if(observerFails)
            while(!logs.Messages.Any(x => x.Contains("\"stage\":\"Failure\"")))
                await Task.Delay(10, deadline.Token);
        deadline.Cancel();
        logs.AssertSafe();
        Assert.Contains(logs.Messages, x => x.Contains("\"stage\":\"Subscribe\""));
        Assert.Contains(logs.Messages, x => x.Contains("\"stage\":\"Receive\""));
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebSocket_ConnectAndSerializationFailuresHaveSafeDiagnostics(bool serializationFailure)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var logs = new CapturedLogs(NLog.LogLevel.Debug);
        await using var server = await Server.Start(async context =>
        {
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
        var settings = new JsonSerializerSettings { Converters = { new ThrowingConverter(new InvalidOperationException(UnsafeText)) } };
        using var subscription = client.WebsocketSubscribe(logs.Logger, deadline.Token, server.Endpoint(),
            UnsafeText, new[] { Secret }, serializationFailure ? settings : null).Subscribe(new Observer(_ => { }));
        while(!logs.Messages.Any(x => x.Contains("\"stage\":\"Failure\"")))
            await Task.Delay(10, deadline.Token);
        deadline.Cancel();
        logs.AssertSafe();
        Assert.Contains(logs.Messages, x => x.Contains(serializationFailure ? "\"failure\":\"other\"" : "\"failure\":\"websocket\""));
    }

    private sealed class CapturedLogs : IDisposable
    {
        private readonly LogFactory factory = new();
        private readonly MemoryTarget target = new() { Layout = "${message}${exception:format=tostring}" };
        public CapturedLogs(NLog.LogLevel minimum = null)
        {
            var config = new NLog.Config.LoggingConfiguration();
            config.AddRule(minimum ?? NLog.LogLevel.Trace, NLog.LogLevel.Fatal, target);
            factory.Configuration = config;
            Logger = factory.GetLogger("rpc-diagnostics-test");
        }
        public NLog.ILogger Logger { get; }
        public void Disable() => factory.Configuration = new NLog.Config.LoggingConfiguration();
        public string[] Messages => target.Logs.ToArray();
        public void AssertSafe()
        {
            Assert.NotEmpty(Messages);
            foreach(var message in Messages)
            {
                Assert.DoesNotContain(Secret, message);
                Assert.DoesNotContain("rpc-user", message);
                Assert.DoesNotContain("127.0.0.1", message);
                Assert.True(message.Length < 512);
                Assert.DoesNotContain(message, c => c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029');
                Assert.StartsWith("RPC diagnostic ", message);
                var data = JObject.Parse(message["RPC diagnostic ".Length..]);
                Assert.Equal(new[] { "transport", "stage", "method", "batchCount", "httpStatus", "bytes", "elapsedMs", "failure" },
                    data.Properties().Select(x => x.Name).ToArray());
            }
        }
        public void Dispose() => factory.Dispose();
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

    private sealed class Server(WebApplication app, Uri address) : IAsyncDisposable
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
