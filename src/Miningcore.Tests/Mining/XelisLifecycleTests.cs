using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Miningcore.Blockchain.Xelis;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Rpc;
using Miningcore.Messaging;
using Miningcore.Stratum;
using Miningcore.Tests.Rpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Mining;

public class XelisLifecycleTests
{
    private sealed class Proof : IHashAlgorithm
    {
        internal int Calls;
        internal Action BeforeHash;
        public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
        {
            Interlocked.Increment(ref Calls);
            BeforeHash?.Invoke();
            result.Fill(255);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string HeaderAlgorithm = "xel/v1", WorkAlgorithm = "xel/v1";
        internal int Difficulty = 1;
        internal int? WorkDifficulty;
        internal bool Fail, PauseHeader, PauseWork, PauseSubmission;
        internal readonly TaskCompletionSource SubmissionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseSubmission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Submissions;
        internal readonly TaskCompletionSource Paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly RpcDiagnosticTests.CapturedLogs Logs = new(LogLevel.Info);
        private readonly IContainer container = new ContainerBuilder().Build();
        private RpcDiagnosticTests.Server server;
        internal RpcConsumerDiagnosticTests.XelisConsumer Manager;

        internal static async Task<Fixture> Start()
        {
            var fixture = new Fixture();
            fixture.server = await RpcDiagnosticTests.Server.Start(fixture.Handle);
            var bus = Substitute.For<IMessageBus>();
            fixture.Manager = new(fixture.container, bus,
                new RpcClient(fixture.server.Endpoint(), new JsonSerializerSettings(), bus, "test"), fixture.Logs.Logger);
            return fixture;
        }

        private async Task Handle(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            var method = (string) request["method"];
            if(method == XelisCommands.SubmitBlock)
            {
                Interlocked.Increment(ref Submissions);
                if(PauseSubmission)
                {
                    SubmissionEntered.TrySetResult();
                    await ReleaseSubmission.Task.WaitAsync(context.RequestAborted);
                }
            }
            if(PauseHeader && method == XelisCommands.GetBlockHeader || PauseWork && method == XelisCommands.GetBlockTemplate)
            {
                await context.Response.WriteAsync("{\"result\":");
                await context.Response.Body.FlushAsync();
                Paused.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch(OperationCanceledException) { }
                return;
            }
            if(Fail)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsync("private https://user:secret@host " + new string('-', 1000));
                return;
            }
            var work = new JObject
            {
                ["difficulty"] = method == XelisCommands.GetBlockHeader ? Difficulty : WorkDifficulty ?? Difficulty,
                ["algorithm"] = method == XelisCommands.GetBlockHeader ? HeaderAlgorithm : WorkAlgorithm,
                ["template"] = new string('0', 256), ["miner_work"] = new string('a', 224), ["topoheight"] = 123,
            };
            await context.Response.WriteAsync(new JObject { ["id"] = request["id"],
                ["result"] = method == XelisCommands.SubmitBlock ? new JValue(true) : work }.ToString());
        }

        internal JObject[] Diagnostics => Logs.Messages.Where(x => x.StartsWith("RPC consumer diagnostic ", StringComparison.Ordinal))
            .Select(x => JObject.Parse(x["RPC consumer diagnostic ".Length..])).ToArray();

        public async ValueTask DisposeAsync()
        { await server.DisposeAsync(); container.Dispose(); Logs.Dispose(); }
    }

    private static StratumConnection Issue(XelisJob source, Proof proof)
    {
        NonBitcoinCandidateCreditTests.Set(source, "xelisHash", proof);
        var worker = XelisDifficultyTests.Worker(1);
        NonBitcoinCandidateCreditTests.Set(worker, "RemoteEndpoint", new IPEndPoint(IPAddress.Loopback, 12345));
        var context = worker.ContextAs<XelisWorkerContext>();
        context.AddJob(source.ForWorker(context), 8);
        return worker;
    }

    private static Task<Miningcore.Blockchain.Share> Submit(Fixture fixture, StratumConnection worker, string jobId, CancellationToken ct) =>
        fixture.Manager.SubmitShareAsync(worker, new object[] { "test", jobId, "0000000000000001" }, ct).AsTask();

    [Theory]
    [InlineData("header-v3")] [InlineData("work-v3")] [InlineData("algorithm-mismatch")] [InlineData("difficulty-mismatch")]
    public async Task IncompatibleTransitionRetiresAllRecentJobsAndRecoveryNeverReopensThem(string fault)
    {
        await using var fixture = await Fixture.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.True(await fixture.Manager.Refresh(deadline.Token));
        var oldest = fixture.Manager.CurrentJob;
        var proof = new Proof();
        var a = Issue(oldest, proof);
        fixture.Difficulty = 2;
        Assert.True(await fixture.Manager.Refresh(deadline.Token));
        var previous = fixture.Manager.CurrentJob;
        var b = Issue(previous, proof);

        if(fault == "header-v3") fixture.HeaderAlgorithm = "xel/v3";
        if(fault == "work-v3") fixture.WorkAlgorithm = "xel/v3";
        if(fault == "algorithm-mismatch") fixture.WorkAlgorithm = "xel/v2";
        if(fault == "difficulty-mismatch") fixture.WorkDifficulty = 3;
        Assert.False(await fixture.Manager.Refresh(deadline.Token));
        Assert.Null(fixture.Manager.GetJobForStratum());
        Assert.Null(fixture.Manager.CurrentJob);

        foreach(var pair in new[] { (Job: oldest, Worker: a), (Job: previous, Worker: b) })
        {
            Assert.True(pair.Job.IsRetired);
            Assert.Null(pair.Worker.ContextAs<XelisWorkerContext>().GetJob(pair.Job.JobId));
            Assert.Equal(StratumError.JobNotFound, Assert.Throws<XelisWorkUnavailableException>(() => pair.Job.ForWorker(pair.Worker.Context)).Code);
            Assert.Equal(StratumError.JobNotFound, Assert.Throws<XelisWorkUnavailableException>(() => pair.Job.GetJobParams(true)).Code);
            Assert.Equal(StratumError.JobNotFound, (await Assert.ThrowsAsync<XelisWorkUnavailableException>(() => Submit(fixture, pair.Worker, pair.Job.JobId, deadline.Token))).Code);
        }

        // A new authorization cannot obtain an old job, including through the pool's issuer.
        var pool = (XelisPool) RuntimeHelpers.GetUninitializedObject(typeof(XelisPool));
        NonBitcoinCandidateCreditTests.Set(pool, "manager", fixture.Manager);
        var create = typeof(XelisPool).GetMethod("CreateWorkerJob", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Null(create.Invoke(pool, new object[] { XelisDifficultyTests.Worker(1), true }));
        Assert.Equal(0, proof.Calls);
        Assert.Equal(0, fixture.Submissions);

        fixture.HeaderAlgorithm = fixture.WorkAlgorithm = "xel/v1";
        fixture.WorkDifficulty = null;
        Assert.True(await fixture.Manager.Refresh(deadline.Token));
        var recovered = fixture.Manager.GetJobForStratum();
        Assert.False(recovered.IsRetired);
        Assert.NotEqual(previous.JobId, recovered.JobId);
        Assert.True(oldest.IsRetired);
        Assert.True(previous.IsRetired);
        Assert.Equal(StratumError.JobNotFound, (await Assert.ThrowsAsync<XelisWorkUnavailableException>(() => Submit(fixture, a, oldest.JobId, deadline.Token))).Code);
        var c = Issue(recovered, proof);
        var accepted = await Submit(fixture, c, recovered.JobId, deadline.Token);
        Assert.False(accepted.IsBlockCandidate);
        Assert.Equal(1d, accepted.Difficulty);
        Assert.Equal(1, proof.Calls);
        Assert.Equal(0, fixture.Submissions);
    }

    [Fact]
    public async Task RetirementDuringProofVerificationRejectsTheResultBeforeAccountingOrSubmission()
    {
        await using var fixture = await Fixture.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        fixture.Difficulty = 2;
        Assert.True(await fixture.Manager.Refresh(deadline.Token));
        var job = fixture.Manager.CurrentJob;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var proof = new Proof { BeforeHash = () => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        var worker = Issue(job, proof);
        var pending = Task.Run(() => Submit(fixture, worker, job.JobId, deadline.Token));
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            fixture.HeaderAlgorithm = "xel/v3";
            Assert.False(await fixture.Manager.Refresh(deadline.Token));
        }
        finally { release.Set(); }
        Assert.Equal(StratumError.JobNotFound, (await Assert.ThrowsAsync<XelisWorkUnavailableException>(() => pending)).Code);
        Assert.Equal(0, fixture.Submissions);
    }

    [Fact]
    public async Task AdmittedBlockReceiptSurvivesRetirementWhileUnrelatedOldSharesAreRejected()
    {
        await using var fixture = await Fixture.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.True(await fixture.Manager.Refresh(deadline.Token));
        var job = fixture.Manager.CurrentJob;
        var proof = new Proof(); // All-ff hash equals the difficulty-1 target.
        var a = Issue(job, proof);
        var b = Issue(job, proof);
        fixture.PauseSubmission = true;
        var pending = Submit(fixture, a, job.JobId, deadline.Token);
        try
        {
            await fixture.SubmissionEntered.Task.WaitAsync(deadline.Token);
            fixture.HeaderAlgorithm = "xel/v3";
            Assert.False(await fixture.Manager.Refresh(deadline.Token));
            await Assert.ThrowsAsync<XelisWorkUnavailableException>(() => Submit(fixture, b, job.JobId, deadline.Token));
            Assert.Equal(1, proof.Calls);
        }
        finally { fixture.ReleaseSubmission.TrySetResult(); }
        var accepted = await pending.WaitAsync(deadline.Token);
        Assert.True(accepted.IsBlockCandidate);
        Assert.Equal("0000000000000001", accepted.TransactionConfirmationData);
        Assert.Equal(1, fixture.Submissions);
    }

    [Fact]
    public async Task SuspendedOutageRemindsAtTenMinutesAndCountsAllFailuresOnRecovery()
    {
        await using var fixture = await Fixture.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clock = (Miningcore.Tests.Util.MockMasterClock) typeof(XelisJobManager)
            .GetField("clock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture.Manager);
        clock.CurrentTime = DateTime.UnixEpoch;
        Assert.True(await fixture.Manager.Refresh(deadline.Token));
        fixture.HeaderAlgorithm = "xel/v3";
        Assert.False(await fixture.Manager.Refresh(deadline.Token));
        for(var i = 0; i < 3; i++) Assert.False(await fixture.Manager.Refresh(deadline.Token));
        Assert.Single(fixture.Diagnostics);
        clock.CurrentTime += TimeSpan.FromMinutes(10);
        Assert.False(await fixture.Manager.Refresh(deadline.Token));
        Assert.Equal(5, fixture.Diagnostics[1]["failedCount"].Value<int>());
        Assert.False(await fixture.Manager.Refresh(deadline.Token));
        Assert.Equal(2, fixture.Diagnostics.Length);
        fixture.HeaderAlgorithm = "xel/v1";
        Assert.True(await fixture.Manager.Refresh(deadline.Token));
        Assert.Equal("Recovered", fixture.Diagnostics[2]["stage"].Value<string>());
        Assert.Equal(6, fixture.Diagnostics[2]["failedCount"].Value<int>());
    }

    [Fact]
    public async Task TransientOutageKeepsAssignmentsLogsOnlyFirstFailureAndCountsRecovery()
    {
        await using var fixture = await Fixture.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        fixture.Difficulty = 2;
        Assert.True(await fixture.Manager.Refresh(deadline.Token));
        var job = fixture.Manager.CurrentJob;
        var worker = Issue(job, new Proof());
        fixture.Fail = true;
        for(var i = 0; i < 3; i++) Assert.False(await fixture.Manager.Refresh(deadline.Token));
        Assert.Same(job, fixture.Manager.GetJobForStratum());
        Assert.False(job.IsRetired);
        Assert.Equal("http", Assert.Single(fixture.Diagnostics)["failure"].Value<string>());
        fixture.Fail = false;
        Assert.False(await fixture.Manager.Refresh(deadline.Token));
        Assert.Equal(2, fixture.Diagnostics.Length);
        Assert.Equal("Recovered", fixture.Diagnostics[1]["stage"].Value<string>());
        Assert.Equal(3, fixture.Diagnostics[1]["failedCount"].Value<int>());
        Assert.DoesNotContain("secret", string.Join("", fixture.Logs.Messages));
        Assert.False((await Submit(fixture, worker, job.JobId, deadline.Token)).IsBlockCandidate);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task CallerCancellationDuringEitherRpcIsSilentAndDoesNotRetireOrCountFailure(bool header)
    {
        await using var fixture = await Fixture.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.True(await fixture.Manager.Refresh(deadline.Token));
        var job = fixture.Manager.CurrentJob;
        fixture.PauseHeader = header; fixture.PauseWork = !header;
        using var cancel = new CancellationTokenSource();
        var pending = fixture.Manager.Refresh(cancel.Token);
        await fixture.Paused.Task.WaitAsync(deadline.Token);
        cancel.Cancel();
        Assert.False(await pending.WaitAsync(deadline.Token));
        Assert.Empty(fixture.Diagnostics);
        Assert.Same(job, fixture.Manager.GetJobForStratum());
        Assert.False(job.IsRetired);
        fixture.PauseHeader = fixture.PauseWork = false;
        fixture.Fail = true;
        Assert.False(await fixture.Manager.Refresh(deadline.Token));
        Assert.Equal(1, Assert.Single(fixture.Diagnostics)["failedCount"].Value<int>());
    }

    [Fact]
    public void NativeCapabilityRejectsUnsupportedArchitecturesAndMissingExportsBeforeAnyHash()
    {
        Assert.False(XelisNativeCapability.IsAvailable(Architecture.Arm64, _ => throw new Exception("must not load ARM stub")));
        Assert.False(XelisNativeCapability.IsAvailable(Architecture.X64, name => name == "xelis_hash_export"));
        Assert.False(XelisNativeCapability.IsAvailable(Architecture.X64, _ => false));
        Assert.True(XelisNativeCapability.IsAvailable(Architecture.X64, _ => true));
        if(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            Assert.True(XelisNativeCapability.IsAvailable());
        if(!XelisNativeCapability.IsAvailable())
        {
            using var container = new ContainerBuilder().Build();
            using var logs = new RpcDiagnosticTests.CapturedLogs();
            var bus = Substitute.For<IMessageBus>();
            var manager = new RpcConsumerDiagnosticTests.XelisConsumer(container, bus,
                new RpcClient(new DaemonEndpointConfig(), new JsonSerializerSettings(), bus, "test"), logs.Logger);
            Assert.Throws<Miningcore.Mining.TrustedPoolStartupException>(() => manager.Configure(new PoolConfig { Id = "test" }, new ClusterConfig()));
        }
    }
}
