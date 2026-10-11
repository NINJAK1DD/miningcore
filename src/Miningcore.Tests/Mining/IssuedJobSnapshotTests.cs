using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.IO;
using Miningcore.Blockchain.Alephium;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Blockchain.Progpow;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NLog;
using Xunit;

namespace Miningcore.Tests.Mining;

public class IssuedJobSnapshotTests
{
    private sealed class LinuxNativeFactAttribute : FactAttribute
    {
        public LinuxNativeFactAttribute()
        {
            if(!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Skip = "Requires the source-built Linux native hashing library";
        }
    }

    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static MethodInfo Copier(Type type)
    {
        for(; type != null; type = type.BaseType)
            if(type.GetMethod("ForWorker", Members | BindingFlags.DeclaredOnly) is { } method) return method;
        return null;
    }

    public static IEnumerable<object[]> Jobs => typeof(BitcoinJob).Assembly.GetTypes()
        .Where(x => !x.IsAbstract && x.Name.EndsWith("Job") && !typeof(BitcoinBlake2bJob).IsAssignableFrom(x) && Copier(x) != null)
        .Select(x => new object[] { x.FullName });

    [Theory]
    [MemberData(nameof(Jobs))]
    public void ConcreteAndCustomJobs_KeepTypeTemplateDuplicateSetAndSeparateDifficultyIds(string name)
    {
        var type = typeof(BitcoinJob).Assembly.GetType(name);
        var worker = new WorkerContextBase();
        var time = new ManualTimeProvider();
        worker.Init(10, null, new MockMasterClock(), time);
        var template = type.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(type) : RuntimeHelpers.GetUninitializedObject(type);
        var idProperty = type.GetProperty("JobId", Members) ?? type.GetProperty("Id", Members);
        // Setter may live on a base type (private Ethereum.Id).
        for(var owner = type; owner != null; owner = owner.BaseType)
            if(owner.GetProperty(idProperty.Name, Members | BindingFlags.DeclaredOnly) is { } declared)
            { declared.SetValue(template, "source"); break; }
        FieldInfo parameters = null;
        FieldInfo duplicates = null;
        for(var owner = type; owner != null; owner = owner.BaseType)
        {
            parameters ??= owner.GetField("jobParams", Members | BindingFlags.DeclaredOnly);
            duplicates ??= owner.GetField("submissions", Members | BindingFlags.DeclaredOnly) ??
                owner.GetField("workerNonces", Members | BindingFlags.DeclaredOnly);
        }
        if(parameters?.FieldType == typeof(object[]))
            parameters.SetValue(template, new object[] { "source", "immutable", false });
        if(parameters?.FieldType == typeof(AlephiumJobParams))
            parameters.SetValue(template, new AlephiumJobParams { JobId = "source", HeaderBlob = "immutable" });
        if(duplicates != null && duplicates.GetValue(template) == null)
            duplicates.SetValue(template, Activator.CreateInstance(duplicates.FieldType));
        var method = Copier(type);
        var old = method.Invoke(template, new object[] { worker });
        Assert.Equal(type, old.GetType());
        Assert.Same(old, method.Invoke(template, new object[] { worker }));
        worker.SetDifficulty(40);
        var current = method.Invoke(template, new object[] { worker });
        Assert.NotEqual(idProperty.GetValue(old), idProperty.GetValue(current));
        Assert.Equal("source", idProperty.GetValue(template));
        Assert.Equal(10, worker.ValidateShareDifficulty(50, false, old));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(20, false, current));
        Assert.Equal(40, worker.ValidateShareDifficulty(40, false, current));
        if(parameters?.FieldType == typeof(object[]))
        {
            Assert.NotSame(parameters.GetValue(template), parameters.GetValue(current));
            Assert.Equal(idProperty.GetValue(current), ((object[]) parameters.GetValue(current))[0]);
            Assert.Equal("source", ((object[]) parameters.GetValue(template))[0]);
        }
        if(duplicates != null)
        {
            Assert.Same(duplicates.GetValue(template), duplicates.GetValue(old));
            Assert.Same(duplicates.GetValue(old), duplicates.GetValue(current));
        }
        time.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(50, false, old));
        Assert.Equal(10, worker.ValidateShareDifficulty(50, true, old));
        worker.Init(40, null, new MockMasterClock(), new ManualTimeProvider());
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(50, true, old));
    }

    [Fact]
    public void Kaspa_IncorrectNumericIdsUseLatestEpochAndMalformedIdsFailClosed()
    {
        var context = new WorkerContextBase();
        context.Init(10, null, new MockMasterClock());
        var hash = new Miningcore.Crypto.Hashing.Algorithms.Sha256D();
        var template = new KaspaJob(hash, hash, hash);
        typeof(KaspaJob).GetProperty("JobId", Members).SetValue(template, "1");
        var old = template.ForWorker(context);
        context.SetDifficulty(40);
        var current = template.ForWorker(context);
        var jobs = new[] { old, current };
        Assert.Same(current, KaspaJobManager.FindCompatibleJob(jobs, "2"));
        Assert.Throws<StratumException>(() => context.ValidateShareDifficulty(20, false,
            KaspaJobManager.FindCompatibleJob(jobs, "2")));
        foreach(var invalid in new[] { "invalid", "2-d1", "-1", "999999999999999999999999" })
            Assert.Null(KaspaJobManager.FindCompatibleJob(jobs, invalid));
        Assert.Null(KaspaJobManager.FindCompatibleJob(jobs, "1"));
        Assert.Equal(10, context.ValidateShareDifficulty(20, false, old));
    }

    [LinuxNativeFact]
    public void Alephium_BelowIssuedTargetKeepsFamilySpecificRejection()
    {
        var hasher = new Miningcore.Crypto.Hashing.Algorithms.Blake3IHash();
        var first = new byte[32];
        var hash = new byte[32];
        hasher.Digest(new byte[AlephiumConstants.NonceLength + 1], first);
        hasher.Digest(first, hash);
        var (from, to) = AlephiumUtils.BlockChainIndex(hash);
        var template = new AlephiumJob();
        template.Init(new AlephiumBlockTemplate
        {
            JobId = "alephium", HeaderBlob = "00", TxsBlob = "",
            TargetBlob = new string('0', 63) + "1", FromGroup = from, ToGroup = to
        });
        var context = new AlephiumWorkerContext();
        var clock = new MockMasterClock();
        context.Init(40, null, clock);
        var connection = new StratumConnection(new NullLogger(LogManager.LogFactory),
            new RecyclableMemoryStreamManager(), clock, "alephium-error", false);
        connection.SetContext(context);
        var job = template.ForWorker(context);
        var failure = Assert.Throws<AlephiumStratumException>(() =>
            job.ProcessShare(connection, new string('0', AlephiumConstants.NonceLength * 2)));
        Assert.Equal(AlephiumStratumError.LowDifficultyShare, failure.Code);
    }

    [Fact]
    public void Progpow_ReissuedWorkerJobsShareProofDeduplicationAcrossEpochs()
    {
        var context = new WorkerContextBase();
        context.Init(10, null, new MockMasterClock());
        var template = new ProgpowJob();
        var old = (ProgpowJob) template.ForWorker(context);
        context.SetDifficulty(20);
        var current = (ProgpowJob) template.ForWorker(context);
        Assert.True(old.RegisterWorkerProof("worker", "nonce", "header", "mix"));
        Assert.False(current.RegisterWorkerProof("worker", "nonce", "header", "mix"));
        Assert.True(current.RegisterWorkerProof("other-worker", "nonce", "header", "mix"));
    }
}
