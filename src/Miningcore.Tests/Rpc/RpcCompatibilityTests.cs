using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Rpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Rpc;

public class RpcCompatibilityTests
{
    [Fact]
    public async Task OriginalGenericClrSignatureAndOptionalArgumentsRemainCallable()
    {
        var types = new[] { typeof(ILogger), typeof(string), typeof(CancellationToken), typeof(object), typeof(bool) };
        var method = Assert.Single(typeof(RpcClient).GetMethods().Where(x => x.Name == "ExecuteAsync" && x.IsGenericMethodDefinition &&
            x.GetParameters().Select(p => p.ParameterType).SequenceEqual(types)));
        Assert.True(method.GetParameters()[3].IsOptional);
        Assert.Equal(false, method.GetParameters()[4].DefaultValue);
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            await context.Response.WriteAsync(new JObject { ["id"] = request["id"], ["result"] = new JObject { ["ok"] = true } }.ToString());
        });
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        var client = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), Substitute.For<IMessageBus>(), "test");
        var task = (Task<RpcResponse<JToken>>) method.MakeGenericMethod(typeof(JToken)).Invoke(client,
            new object[] { logs.Logger, "get_info", CancellationToken.None, null, false });
        var response = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(response.Error);
        Assert.True((bool) response.Response["ok"]);
    }

    [Fact]
    public async Task ElapsedHttpTimeoutAfterHeadersIsNotMistakenForCallerCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            await context.Response.WriteAsync("{\"result\":");
            await context.Response.Body.FlushAsync();
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            catch(OperationCanceledException) { }
        });
        using var transport = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        var client = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), Substitute.For<IMessageBus>(), "test", transport);
        var pending = client.ExecuteBoundedAsync<JToken>(logs.Logger, "get_miner_work", CancellationToken.None,
            new RpcResponseLimits(4096, 78));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        var timeout = Assert.IsType<TaskCanceledException>(response.Error.InnerException);
        Assert.IsType<TimeoutException>(timeout.InnerException);
        Assert.Equal("timeout", RpcConsumerDiagnostics.Failure(timeout));
        Assert.Null(response.Response);
        Assert.Contains(logs.Messages, x => x.Contains("\"failure\":\"timeout\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NonSuccessBodiesPreserveHttpStatusWithoutBypassingNumericLimits(bool oversizedNumber)
    {
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            context.Response.StatusCode = 502;
            await context.Response.WriteAsync(oversizedNumber
                ? "{\"result\":" + new string('9', 1000) + "}"
                : new string('-', 1000));
        });
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        var client = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), Substitute.For<IMessageBus>(), "test");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await client.ExecuteBoundedAsync<JToken>(logs.Logger, "get_miner_work", deadline.Token,
            new RpcResponseLimits(4096, 78));
        var error = Assert.IsType<HttpRequestException>(response.Error.InnerException);
        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Equal("http", RpcConsumerDiagnostics.Failure(error));
        Assert.Null(response.Response);
        if(oversizedNumber) Assert.Contains("numeric token", error.InnerException.Message, StringComparison.Ordinal);
    }
}
