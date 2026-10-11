using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.IO;
using Miningcore.Blockchain.Beam;
using Miningcore.Blockchain.Beam.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Native;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using Newtonsoft.Json.Linq;
using NLog;
using Xunit;

namespace Miningcore.Tests.Blockchain.Beam;

public class BeamGpuProofTests
{
    private sealed class LinuxNativeTheoryAttribute : TheoryAttribute
    {
        public LinuxNativeTheoryAttribute()
        {
            if(!OperatingSystem.IsLinux()) Skip = "Physical GPU proof replay requires the Linux native Beam verifier";
        }
    }

    public static IEnumerable<object[]> Proofs => Enumerable.Range(0, 9).Select(index => new object[] { index });

    [LinuxNativeTheory]
    [MemberData(nameof(Proofs))]
    public void PhysicalBeamHashThreeProofRetainsNativeIdCreditAndDuplicateContract(int index)
    {
        // Actual RTX 3080 Ti/lolMiner 1.98a solutions captured by the bounded
        // process-host probe on 2026-10-11. No synthetic final-hash substitution.
        using var stream = typeof(BeamGpuProofTests).Assembly.GetManifestResourceStream(
            "Miningcore.Tests.Fixtures.Beam.gpu-beamhash3.json");
        using var reader = new StreamReader(stream);
        var evidence = JObject.Parse(reader.ReadToEnd());
        Assert.True(evidence.Value<bool>("passed"));
        var proof = evidence["proofs"][index];
        var stage = proof.Value<int>("stage");
        var clock = new MockMasterClock();
        var context = new BeamWorkerContext();
        context.Init(8, null, clock);
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory),
            new RecyclableMemoryStreamManager(), clock, "physical-beam-replay", true);
        worker.SetContext(context);
        BeamJob Template(string id, char input)
        {
            var result = new BeamJob();
            result.Init(new BeamBlockTemplate
            {
                JobId = id, Height = 1000000, Input = new string(input, 64), PowType = 2,
                PackedDifficulty = BeamUtils.PackedDifficulty(1_000_000_000),
            }, id, new PoolConfig { Template = new BeamCoinTemplate() }, new ClusterConfig(), clock, new BeamHash());
            return result;
        }
        var source = Template("12345", '0');
        context.AddJob(source.ForWorker(context), 4);
        if(stage >= 1)
        {
            context.SetDifficulty(32);
            context.AddJob(source.ForWorker(context), 4);
        }
        if(stage == 2)
        {
            context.SetDifficulty(16);
            source = Template("12346", '1');
            context.AddJob(source.ForWorker(context), 4);
        }
        var job = context.GetJob(proof.Value<string>("id"));
        Assert.NotNull(job);
        var nonce = proof.Value<string>("nonce");
        var solution = proof.Value<string>("solution");
        var result = job.ProcessShare(worker, nonce, solution);
        Assert.Equal(BeamConstants.BeamRpcShareAccepted, result.stratumError);
        Assert.False(result.Share.IsBlockCandidate);
        Assert.Equal(proof.Value<double>("credit"), result.Share.Difficulty);
        Assert.Equal(BeamConstants.BeamRpcDuplicateShare, job.ProcessShare(worker, nonce, solution).stratumError);
    }
}
