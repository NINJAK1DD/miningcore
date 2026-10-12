using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Progpow;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Mining;

public class ProgpowSubmissionBoundaryTests
{
    private sealed class Manager() : ProgpowJobManager(Substitute.For<IComponentContext>(), new MockMasterClock(),
        Substitute.For<IMessageBus>(), Substitute.For<IExtraNonceProvider>())
    {
        internal void Initialize()
        {
            poolConfig = new PoolConfig { Id = "prefix-boundary" };
            clusterConfig = new ClusterConfig { ClusterName = "lab" };
            logger = LogManager.GetCurrentClassLogger();
        }
    }

    // Control only the final consensus verifier; exercise the real manager,
    // worker parser, assignment credit and shared accepted-proof registry.
    private sealed class Proof : ProgpowJob
    {
        internal int Calls;
        public override (Share Share, string BlockHex) ProcessShareInternal(ILogger logger, StratumConnection worker,
            ulong nonce, string header, string mix)
        {
            Calls++;
            return (new Share { Difficulty = worker.Context.ValidateShareDifficulty(20, false, this) }, null);
        }
    }

    private static (Manager Manager, StratumConnection Worker, Proof Job) Create()
    {
        var clock = new MockMasterClock();
        var context = new ProgpowWorkerContext { ExtraNonce1 = "abcdef01" };
        context.Init(10, null, clock);
        var source = new Proof();
        var job = (Proof) source.ForWorker(context, preserveId: true);
        context.AddJob(new ProgpowWorkerJob("1", context.ExtraNonce1) { Job = job }, 4);
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), clock, "prefix", true);
        worker.SetContext(context);
        typeof(StratumConnection).GetProperty(nameof(StratumConnection.RemoteEndpoint)).SetValue(worker, new IPEndPoint(IPAddress.Loopback, 12345));
        var manager = new Manager();
        manager.Initialize();
        return (manager, worker, job);
    }

    private static object[] Submission() => new object[] { "miner.worker", "1", "0xabcdef0100000001", "0x" + new string('a', 64), "0x" + new string('b', 64) };

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task MalformedPrefixedProofFieldsAreRecoverableAndDoNotConsumeProof(int field)
    {
        var (manager, worker, job) = Create();
        var body = ((string) Submission()[field])[2..];
        foreach(var invalid in new object[] { null, 1, "", "0", "0x", body, "xx" + body, "0x" + body[..^1],
            "0x" + body + "f", "0x" + ProofEncodingBoundaryTests.NibbleAlias(body), "0x" + body + "\n" })
        {
            var parameters = Submission();
            parameters[field] = invalid;
            var error = await Assert.ThrowsAsync<StratumException>(() => manager.SubmitShareAsync(worker, parameters, CancellationToken.None).AsTask());
            Assert.Equal(StratumError.Other, error.Code);
            Assert.Equal(0, job.Calls);
        }
        var accepted = await manager.SubmitShareAsync(worker, Submission(), CancellationToken.None);
        Assert.Equal(10, accepted.Difficulty);
        Assert.Equal(1, job.Calls);
        var alias = Submission();
        for(var i = 2; i < alias.Length; i++) alias[i] = ((string) alias[i]).ToUpperInvariant();
        Assert.Equal(StratumError.DuplicateShare, (await Assert.ThrowsAsync<StratumException>(() =>
            manager.SubmitShareAsync(worker, alias, CancellationToken.None).AsTask())).Code);
        Assert.Equal(1, job.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public async Task MalformedParameterCountsAreRecoverable(int count)
    {
        var (manager, worker, job) = Create();
        var error = await Assert.ThrowsAsync<StratumException>(() => manager.SubmitShareAsync(worker, new object[count], CancellationToken.None).AsTask());
        Assert.Equal("invalid params", error.Message);
        Assert.Equal(0, job.Calls);
    }

    [Fact]
    public async Task NonArraySubmissionIsRecoverable()
    {
        var (manager, worker, job) = Create();
        Assert.Equal("invalid params", (await Assert.ThrowsAsync<StratumException>(() =>
            manager.SubmitShareAsync(worker, "invalid", CancellationToken.None).AsTask())).Message);
        Assert.Equal(0, job.Calls);
    }
}
