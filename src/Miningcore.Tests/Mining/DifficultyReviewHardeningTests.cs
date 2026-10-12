using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Alephium;
using Miningcore.Blockchain.Beam;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Mining;

public class DifficultyReviewHardeningTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(300)]
    public void GraceUsesReceiptTimeAndFallsBackOnlyToCurrentCredit(int grace)
    {
        var time = new ManualTimeProvider();
        var worker = new WorkerContextBase();
        worker.Init(10, null, new MockMasterClock(), time, grace);
        var source = new object();
        var old = worker.JobForDifficulty(source, (_, _) => new object(), "job");
        Assert.Same(source, old); // First issuance needs no worker copy.
        worker.SetDifficulty(40);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(grace));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(20, false, old));
        Assert.Equal(40, worker.ValidateShareDifficulty(50, false, old));
        Assert.Equal(10, worker.ValidateShareDifficulty(20, true, old));
        if(grace == 0) return;
        time.AdvanceMonotonic(TimeSpan.FromTicks(-1));
        var timely = worker.CaptureShareReceipt();
        time.AdvanceMonotonic(TimeSpan.FromHours(1));
        using(worker.UseShareReceipt(timely))
            Assert.Equal(10, worker.ValidateShareDifficulty(20, false, old));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(20, false, old));
        using(worker.UseShareReceipt(new ShareReceipt(new ManualTimeProvider(), 0)))
            Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(50, false, old));
        using(worker.UseShareReceipt(new ShareReceipt(time, time.GetTimestamp() + 1)))
            Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(50, false, old));
        worker.CloseDifficultyAssignments();
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(50, true, old));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(50, false, new object()));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void EndpointAndWorkerBoundGrace(int seconds, bool valid)
    {
        Assert.Equal(valid, new PoolEndpointValidator().Validate(new PoolEndpoint
            { Difficulty = 1, DifficultyGraceSeconds = seconds }).IsValid);
        var worker = new WorkerContextBase();
        if(valid) worker.Init(1, null, new MockMasterClock(), difficultyGraceSeconds: seconds);
        else Assert.Throws<ArgumentOutOfRangeException>(() => worker.Init(1, null, new MockMasterClock(), difficultyGraceSeconds: seconds));
        Assert.Equal(30, new PoolEndpoint().DifficultyGraceSeconds);
    }

    private sealed class Counter : JobManagerBase<object>
    {
        internal Counter() : base(Substitute.For<IComponentContext>(), Substitute.For<IMessageBus>()) { }
        protected override void ConfigureDaemons() => throw new NotSupportedException();
        protected override Task<bool> AreDaemonsHealthyAsync(System.Threading.CancellationToken ct) => throw new NotSupportedException();
        protected override Task<bool> AreDaemonsConnectedAsync(System.Threading.CancellationToken ct) => throw new NotSupportedException();
        protected override Task EnsureDaemonsSynchedAsync(System.Threading.CancellationToken ct) => throw new NotSupportedException();
        protected override Task PostStartInitAsync(System.Threading.CancellationToken ct) => throw new NotSupportedException();
        public override object GetJobForStratum() => throw new NotSupportedException();
    }

    [Fact]
    public void NativeCounterIsUniqueUnderConcurrentTemplateAndSnapshotAllocation()
    {
        var counter = new Counter();
        var ids = new ConcurrentBag<string>();
        Parallel.For(0, 10000, _ => ids.Add(counter.NextWorkerJobId("D")));
        Assert.Equal(10000, ids.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 10000), ids.Select(int.Parse).OrderBy(x => x));
        Assert.Matches("^[0-9a-f]{8}$", counter.NextWorkerJobId("x8"));
        Assert.Matches("^[0-9A-F]+$", counter.NextWorkerJobId("X"));
        typeof(JobManagerBase<object>).GetField("jobId", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(counter, int.MaxValue);
        Assert.Equal("00000001", counter.NextWorkerJobId());
        Assert.Equal("2", counter.NextWorkerJobId("D"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("D")]
    [InlineData("x8")]
    [InlineData("X")]
    public void NativeCounterWrapsAtomicallyWithoutDuplicatesInTheRecentWindow(string format)
    {
        var counter = new Counter();
        typeof(JobManagerBase<object>).GetField("jobId", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(counter, int.MaxValue - 5000);
        var ids = new ConcurrentBag<string>();
        Parallel.For(0, 10000, _ => ids.Add(counter.NextWorkerJobId(format)));
        Assert.Equal(10000, ids.Distinct().Count());
        Assert.DoesNotContain(ids, id => id.StartsWith("-", StringComparison.Ordinal));
        Assert.Contains(format == "D" ? "1" : format == "X" ? "1" : "00000001", ids);
        Assert.Equal(format == "D" ? "5001" : format == "X" ? "1389" : "00001389", counter.NextWorkerJobId(format));
    }

    [Fact]
    public void ProgpowAssignmentSnapshotsPreserveBackingIdWithoutAllocatingWireIds()
    {
        var worker = new WorkerContextBase();
        worker.Init(10, null, new MockMasterClock());
        var source = new Miningcore.Blockchain.Progpow.ProgpowJob();
        typeof(Miningcore.Blockchain.Bitcoin.BitcoinJob).GetProperty("JobId").SetValue(source, "backing");
        var old = source.ForWorker(worker, () => throw new InvalidOperationException("unused"), preserveId: true);
        worker.SetDifficulty(40);
        var current = source.ForWorker(worker, () => throw new InvalidOperationException("unused"), preserveId: true);
        Assert.NotSame(old, current);
        Assert.Equal(old.JobId, current.JobId);
        Assert.Equal(10, worker.ValidateShareDifficulty(20, false, old));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(20, false, current));
    }

    private static KaspaJob Kaspa(string id)
    {
        var hash = new Sha256D();
        var job = new KaspaJob(hash, hash, hash);
        typeof(KaspaJob).GetProperty("JobId").SetValue(job, id);
        return job;
    }

    [Fact]
    public void NumericCompatibilityRetainsOldestTemplateAndLatestAssignment()
    {
        var worker = new WorkerContextBase();
        worker.Init(10, null, new MockMasterClock());
        var counter = new Counter();
        var first = Kaspa(counter.NextWorkerJobId("D"));
        var old = first.ForWorker(worker, () => counter.NextWorkerJobId("D"));
        worker.SetDifficulty(40);
        var updated = first.ForWorker(worker, () => counter.NextWorkerJobId("D"));
        var second = Kaspa(counter.NextWorkerJobId("D"));
        var latestTemplate = second.ForWorker(worker, () => counter.NextWorkerJobId("D"));
        Assert.Equal("1", old.JobId);
        Assert.Equal("2", updated.JobId);
        Assert.Equal("3", latestTemplate.JobId); // New work never gets an assignment suffix.
        var jobs = new[] { old, updated, latestTemplate };
        Assert.Same(updated, KaspaJobManager.FindCompatibleJob(jobs, "1"));
        Assert.Same(updated, KaspaJobManager.FindCompatibleJob(jobs, "100"));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(20, false, updated));
        Assert.Null(KaspaJobManager.FindCompatibleJob(jobs, "0"));
    }

    [Fact]
    public void GroupCompatibilityRetainsOldestTemplateAndLatestAssignment()
    {
        var worker = new WorkerContextBase();
        worker.Init(10, null, new MockMasterClock());
        AlephiumJob Template(string id, int group)
        {
            var result = new AlephiumJob();
            typeof(AlephiumJob).GetProperty("JobId").SetValue(result, id);
            typeof(AlephiumJob).GetProperty("BlockTemplate").SetValue(result,
                new AlephiumBlockTemplate { FromGroup = group, ToGroup = 3 });
            return result;
        }
        var source = Template("A", 0);
        var original = source.ForWorker(worker);
        worker.SetDifficulty(40);
        var updated = source.ForWorker(worker, () => "B");
        var later = Template("C", 0).ForWorker(worker);
        var other = Template("D", 1).ForWorker(worker);
        var jobs = new[] { original, updated, later, other };
        Assert.Same(updated, AlephiumJobManager.FindCompatibleJob(jobs, 0, 3));
        Assert.Same(other, AlephiumJobManager.FindCompatibleJob(jobs, 1, 3));
        Assert.Null(AlephiumJobManager.FindCompatibleJob(jobs, 2, 3));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(20, false, updated));
    }

    [Theory]
    [InlineData("Cryptonote")]
    [InlineData("Conceal")]
    [InlineData("Zano")]
    public async Task BlobSubmissionRejectsDifferentTemplateAtSameHeightBeforeValidation(string family)
    {
        var ns = $"Miningcore.Blockchain.{family}";
        var assembly = typeof(WorkerContextBase).Assembly;
        var jobType = assembly.GetType($"{ns}.{family}Job");
        var managerType = assembly.GetType($"{ns}.{family}JobManager");
        // Deliberately unprepared jobs: template identity must be checked before
        // accessing a native hasher, candidate state, deduplication or daemon RPC.
        var old = RuntimeHelpers.GetUninitializedObject(jobType);
        var current = RuntimeHelpers.GetUninitializedObject(jobType);
        if(family == "Zano")
        {
            jobType.GetProperty("Height").SetValue(old, "100");
            jobType.GetProperty("Height").SetValue(current, "100");
        }
        else
        {
            var templateType = jobType.GetProperty("BlockTemplate").PropertyType;
            var template = Activator.CreateInstance(templateType);
            templateType.GetProperty("Height").SetValue(template, 100u);
            var field = jobType.GetField("<BlockTemplate>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(old, template);
            field.SetValue(current, template);
        }
        var arguments = managerType.GetConstructors().Single().GetParameters().Select(p =>
            p.ParameterType == typeof(Miningcore.Time.IMasterClock) ? (object) new MockMasterClock() :
                Substitute.For(new[] { p.ParameterType }, Array.Empty<object>())).ToArray();
        var manager = Activator.CreateInstance(managerType, arguments);
        managerType.BaseType.GetField("currentJob", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, current);
        var workerType = assembly.GetType($"{ns}.{family}WorkerJob");
        var assigned = Activator.CreateInstance(workerType, "assigned", 10d);
        workerType.GetProperty("Height").SetValue(assigned, family == "Zano" ? (object) "100" : 100u);
        workerType.GetProperty("DifficultyTemplate", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(assigned, old);
        var context = (WorkerContextBase) Activator.CreateInstance(assembly.GetType($"{ns}.{family}WorkerContext"));
        context.Init(10, null, new MockMasterClock());
        var connection = new StratumConnection(NLog.LogManager.GetLogger("blob-template-test"),
            new Microsoft.IO.RecyclableMemoryStreamManager(), new MockMasterClock(), "blob-template-test", true);
        connection.SetContext(context);
        var request = Activator.CreateInstance(assembly.GetType($"{ns}.StratumRequests.{family}SubmitShareRequest"));
        var failure = await Assert.ThrowsAsync<StratumException>(async () =>
            await (ValueTask<Share>) managerType.GetMethod("SubmitShareAsync").Invoke(manager,
                new[] { connection, request, assigned, (object) CancellationToken.None }));
        Assert.Equal("block expired", failure.Message);
        var submissions = workerType.GetField("Submissions").GetValue(assigned);
        Assert.Equal(0, (int) submissions.GetType().GetProperty("Count").GetValue(submissions));
    }

    [Fact]
    public void BeamStableInputKeepsNumericEchoAndLatestTarget()
    {
        var worker = new BeamWorkerContext();
        worker.Init(10, null, new MockMasterClock());
        var template = new BeamJob();
        typeof(BeamJob).GetProperty("JobId").SetValue(template, "12345");
        var old = template.ForWorker(worker);
        worker.AddJob(old, 4);
        worker.SetDifficulty(40);
        var current = template.ForWorker(worker);
        worker.AddJob(current, 4);
        Assert.Equal("12345", current.JobId);
        Assert.Same(current, worker.GetJob(long.Parse(current.JobId).ToString()));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(20, false, worker.GetJob("12345")));
        Assert.Equal(40, worker.ValidateShareDifficulty(40, false, worker.GetJob("12345")));
    }
}
