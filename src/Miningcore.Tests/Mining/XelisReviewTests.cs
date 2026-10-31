using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Miningcore.Blockchain.Xelis;
using Miningcore.Blockchain;
using Miningcore.Crypto;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Tests.Rpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Mining;

public class XelisReviewTests
{
    [Theory]
    [InlineData(2, "xel/v1", true)]
    [InlineData(1, "xel/v2", true)]
    [InlineData(2, "xel/v2", true)]
    [InlineData(1, "xel/v1", false)]
    public async Task SameWorkPrefixRefreshesConsensusParameters(int nextDifficulty, string nextAlgorithm, bool changed)
    {
        var difficulty = 1;
        var algorithm = "xel/v1";
        using var logs = new RpcDiagnosticTests.CapturedLogs(LogLevel.Error);
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            await context.Response.WriteAsync(new JObject { ["id"] = request["id"], ["result"] = Work(difficulty, algorithm) }.ToString());
        });
        using var container = new ContainerBuilder().Build();
        var bus = Substitute.For<IMessageBus>();
        var consumer = new RpcConsumerDiagnosticTests.XelisConsumer(container, bus,
            new RpcClient(server.Endpoint(), new JsonSerializerSettings(), bus, "test"), logs.Logger);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.True(await consumer.Refresh(deadline.Token));
        var original = consumer.CurrentJob;
        difficulty = nextDifficulty; algorithm = nextAlgorithm;
        Assert.Equal(changed, await consumer.Refresh(deadline.Token));
        Assert.Equal(!changed, ReferenceEquals(original, consumer.CurrentJob));
        Assert.Equal(new System.Numerics.BigInteger(nextDifficulty), consumer.CurrentJob.ExactDifficulty);
        Assert.Equal(nextAlgorithm, consumer.CurrentJob.Algorithm);
        Assert.Equal(XelisUtils.DifficultyToTarget(new System.Numerics.BigInteger(nextDifficulty)), consumer.CurrentJob.blockTargetBytes);
        Assert.Equal((double) nextDifficulty, consumer.BlockchainStats.NetworkDifficulty);
        Assert.Equal(changed ? 2 : 1, bus.ReceivedCalls().Count(x => x.GetArguments().OfType<NewChainHeightNotification>().Any()));
        Assert.Empty(logs.Messages);
    }

    [Theory]
    [InlineData(null, false)] [InlineData("", false)] [InlineData("xel/v3", false)]
    [InlineData("xel/v3", true)] [InlineData("unknown\r\nhttps://user:secret@host", true)]
    public async Task UnsupportedWorkRetiresIssuedWorkWithoutPublishingReplacement(string algorithm, bool afterValidWork)
    {
        var current = afterValidWork ? "xel/v1" : algorithm;
        using var logs = new RpcDiagnosticTests.CapturedLogs(LogLevel.Error);
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            await context.Response.WriteAsync(new JObject { ["id"] = request["id"], ["result"] = Work(1, current) }.ToString());
        });
        using var container = new ContainerBuilder().Build();
        var bus = Substitute.For<IMessageBus>();
        var consumer = new RpcConsumerDiagnosticTests.XelisConsumer(container, bus,
            new RpcClient(server.Endpoint(), new JsonSerializerSettings(), bus, "test"), logs.Logger);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if(afterValidWork) Assert.True(await consumer.Refresh(deadline.Token));
        var original = consumer.CurrentJob;
        bus.ClearReceivedCalls();
        current = algorithm;
        Assert.False(await consumer.Refresh(deadline.Token));
        Assert.Null(consumer.CurrentJob);
        if(original != null) Assert.True(original.IsRetired);
        Assert.DoesNotContain(bus.ReceivedCalls(), x => x.GetArguments().OfType<NewChainHeightNotification>().Any());
        Assert.Contains("\"failure\":\"invalid-data\"", Assert.Single(logs.Messages));
        Assert.DoesNotContain("secret", string.Join("", logs.Messages));
    }

    [Theory]
    [InlineData(true, "error", "", null)] [InlineData(false, "error", "", null)]
    [InlineData(true, "null", "invalid-data", null)] [InlineData(false, "null", "invalid-data", null)]
    [InlineData(false, "mismatch", "invalid-data", null)]
    [InlineData(true, "number", "json", null)] [InlineData(false, "number", "json", null)]
    [InlineData(false, "large", "invalid-data", null)]
    [InlineData(false, "large", "invalid-data", "chunked")]
    public async Task BadRpcWorkReportsSafeErrors(bool headerFailure, string kind, string category, string framing)
    {
        using var logs = new RpcDiagnosticTests.CapturedLogs(LogLevel.Error);
        var calls = 0;
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            Interlocked.Increment(ref calls);
            var body = new JObject { ["id"] = request["id"], ["result"] = Work(1, "xel/v1") };
            string raw = null;
            if((string) request["method"] == (headerFailure ? XelisCommands.GetBlockHeader : XelisCommands.GetBlockTemplate))
            {
                if(kind == "error") body["error"] = new JObject { ["code"] = -42, ["message"] = "https://user:secret@host\r\nforged", ["data"] = "secret" };
                else if(kind == "null") body["result"] = null;
                else if(kind == "mismatch") body["result"]["difficulty"] = 2;
                else if(kind == "number") raw = "{\"id\":" + request["id"].ToString(Formatting.None) + ",\"result\":{\"difficulty\":" + new string('9', 1000) + "}}";
                else body["result"]["padding"] = new string('x', 5000);
            }
            raw ??= body.ToString(Formatting.None);
            if(framing == "chunked")
            {
                await context.Response.WriteAsync(raw[..100]);
                await context.Response.Body.FlushAsync();
                await context.Response.WriteAsync(raw[100..]);
            }
            else
            {
                context.Response.ContentLength = System.Text.Encoding.UTF8.GetByteCount(raw);
                await context.Response.WriteAsync(raw);
            }
        });
        using var container = new ContainerBuilder().Build();
        var bus = Substitute.For<IMessageBus>();
        var consumer = new RpcConsumerDiagnosticTests.XelisConsumer(container, bus,
            new RpcClient(server.Endpoint(), new JsonSerializerSettings(), bus, "test"), logs.Logger);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.False(await consumer.Refresh(deadline.Token));
        Assert.Null(consumer.CurrentJob);
        Assert.Equal(headerFailure ? 1 : 2, calls);
        Assert.DoesNotContain(bus.ReceivedCalls(), x => x.GetArguments().OfType<NewChainHeightNotification>().Any());
        var record = JObject.Parse(Assert.Single(logs.Messages)["RPC consumer diagnostic ".Length..]);
        int? expectedCode = kind == "error" ? -42 : kind is "mismatch" or "null" ? null : -500;
        Assert.Equal(expectedCode, record["code"].Value<int?>());
        if(kind != "error") Assert.Equal(category, record["failure"].Value<string>());
        Assert.DoesNotContain("secret", string.Join("", logs.Messages));
        Assert.DoesNotContain("null-reference", string.Join("", logs.Messages));
    }

    private static JObject Work(int difficulty, string algorithm) => new()
    {
        ["difficulty"] = difficulty, ["algorithm"] = algorithm, ["template"] = new string('0', 256),
        ["miner_work"] = new string('a', 224), ["topoheight"] = 123,
    };

    [Fact]
    public async Task NumericLimitAcceptsFullU256AndIgnoresEscapedQuotedDigits()
    {
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        var value = XelisConstants.Diff1Target.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            var quoted = JsonConvert.SerializeObject("escaped\\\"" + new string('9', 1000));
            await context.Response.WriteAsync("{\"result\":{\"difficulty\":" + value + ",\"ignored\":" + quoted + "}}");
        });
        var client = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), Substitute.For<IMessageBus>(), "test");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await client.ExecuteBoundedAsync<Miningcore.Blockchain.Xelis.DaemonResponses.GetBlockTemplateResponse>(
            logs.Logger, XelisCommands.GetBlockTemplate, deadline.Token, responseLimits: new RpcResponseLimits(4096, 78));
        Assert.Null(response.Error);
        Assert.Equal(XelisConstants.Diff1Target, response.Response.Difficulty);
    }

    [Fact]
    public async Task BoundedChunkedResponseHonorsCancellationWhileWaitingForBody()
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
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        var client = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), Substitute.For<IMessageBus>(), "test");
        using var cancellation = new CancellationTokenSource();
        var pending = client.ExecuteBoundedAsync<JToken>(logs.Logger, XelisCommands.GetBlockTemplate, cancellation.Token,
            responseLimits: new RpcResponseLimits(4096, 78));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsAssignableFrom<OperationCanceledException>(response.Error.InnerException);
        Assert.Null(response.Response);
    }

    private sealed class ProofHash(bool zero = false) : IHashAlgorithm
    {
        public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
        { result.Clear(); if(!zero) result[^1] = 1; }
    }
    private sealed class IdentityHash : IHashAlgorithm
    {
        public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra) => SHA256.HashData(data, result);
    }
    private sealed class ConcurrentConsumer(IComponentContext container, IMessageBus bus, RpcClient rpc, ILogger log)
        : RpcConsumerDiagnosticTests.XelisConsumer(container, bus, rpc, log)
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int count;
        protected override async Task<bool> SubmitBlockAsync(Share share, string blockTemplate, CancellationToken ct)
        {
            if(Interlocked.Increment(ref count) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(ct); // Both candidates exist before either RPC captures its payload.
            return await base.SubmitBlockAsync(share, blockTemplate, ct);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentWorkersSubmitTheirOwnHeadersAndNeverMutateSharedTemplate(bool zero)
    {
        var headers = new ConcurrentBag<string>();
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            Assert.Equal(XelisCommands.SubmitBlock, (string) request["method"]);
            headers.Add((string) request["params"]["block_template"]);
            await context.Response.WriteAsync(new JObject { ["id"] = request["id"], ["result"] = true }.ToString());
        });
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        using var container = new ContainerBuilder().Build();
        var bus = Substitute.For<IMessageBus>();
        var consumer = new ConcurrentConsumer(container, bus, new RpcClient(server.Endpoint(), new JsonSerializerSettings(), bus, "test"), logs.Logger);
        var job = XelisDifficultyTests.Init(1);
        var original = job.BlockHeader.Template;
        NonBitcoinCandidateCreditTests.Set(job, "xelisHash", new ProofHash(zero));
        NonBitcoinCandidateCreditTests.Set(job, "blake3Hasher", new IdentityHash());
        var a = XelisDifficultyTests.Worker(2);
        var b = XelisDifficultyTests.Worker(3);
        NonBitcoinCandidateCreditTests.Set(a, "RemoteEndpoint", new IPEndPoint(IPAddress.Loopback, 10001));
        NonBitcoinCandidateCreditTests.Set(b, "RemoteEndpoint", new IPEndPoint(IPAddress.Loopback, 10002));
        a.ContextAs<XelisWorkerContext>().ExtraNonce1 = new string('1', 64);
        b.ContextAs<XelisWorkerContext>().ExtraNonce1 = new string('2', 64);
        foreach(var worker in new[] { a, b })
            worker.ContextAs<XelisWorkerContext>().AddJob(job.ForWorker(worker.ContextAs<XelisWorkerContext>()), 8);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task<Share> Submit(StratumConnection worker)
        {
            var issued = worker.ContextAs<XelisWorkerContext>().validJobs.Single();
            return await consumer.SubmitShareAsync(worker, new object[] { "test", issued.JobId, "0000000000000001" }, deadline.Token);
        }
        var results = await Task.WhenAll(Task.Run(() => Submit(a)), Task.Run(() => Submit(b)));
        Assert.Equal(2, headers.Count);
        Assert.Equal(2, headers.Distinct().Count());
        foreach(var pair in new[] { (Worker: a, Share: results[0]), (Worker: b, Share: results[1]) })
        {
            var extra = pair.Worker.ContextAs<XelisWorkerContext>().ExtraNonce1;
            var header = Assert.Single(headers.Where(x => x.Substring(50, 64) == extra));
            Assert.Equal("0000000000000001", header.Substring(34, 16));
            var work = new byte[112];
            Convert.FromHexString("0000000000000001").CopyTo(work, 40);
            Convert.FromHexString(extra).CopyTo(work, 48);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(work)).ToLowerInvariant(), pair.Share.BlockHash);
            Assert.True(pair.Share.IsBlockCandidate);
            Assert.Equal(1d, pair.Share.Difficulty);
        }
        Assert.Equal(original, job.BlockHeader.Template);
    }
}
