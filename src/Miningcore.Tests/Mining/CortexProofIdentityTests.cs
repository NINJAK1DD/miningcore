using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IO;
using Miningcore.Blockchain.Ethereum;
using Miningcore.Blockchain.Ethereum.Custom.Cortex;
using Miningcore.Crypto.Hashing.Ethash;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Mining;

public class CortexProofIdentityTests
{
    private static EthereumBlockTemplate Template() => new() { Header = new string('0', 64), Target = new string('0', 63) + "1", Height = 1 };
    private static StratumConnection Connect(EthereumWorkerContext context, string id)
    {
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), new MockMasterClock(), id, true);
        worker.SetContext(context);
        return worker;
    }
    private static string Cycle(int first) => Enumerable.Range(first, 42)
        .SelectMany(value => BitConverter.GetBytes((uint) value)).ToArray().ToHexString();

    private sealed class Verification
    {
        internal bool Valid = true;
        internal int Calls;
    }
    // Controlled verifier outcomes and final hashes exercise the production
    // Cortex acceptance/registry boundary; these cycles are not consensus vectors.
    private sealed class Job : CortexJob
    {
        internal readonly Verification Verification = new();
        internal Job() : base("1", Template(), LogManager.GetCurrentClassLogger(), Substitute.For<IEthashLight>()) { }
        protected override int VerifySolution(byte[] header, uint[] solution)
        {
            Assert.Equal(40, header.Length);
            Assert.Equal(42, solution.Length);
            Interlocked.Increment(ref Verification.Calls);
            return Verification.Valid ? 0 : 1;
        }
        protected override byte[] SerializeCoinbase(uint[] solution) =>
            (EthereumConstants.BigMaxValue / new BigInteger(20 * EthereumConstants.Pow2x32)).ToByteArray(true, true).PadFront(0, 32);
    }

    [Fact]
    public async Task VerifiedDistinctCyclesAtOneNonceKeepDistinctCreditWhileAliasesHaveOneWinner()
    {
        var context = new EthereumWorkerContext();
        context.Init(10, null, new MockMasterClock());
        var worker = Connect(context, "cortex");
        var source = new Job();
        var old = source.ForWorker(context);
        Task<EthereumJob.SubmitResult> Submit(EthereumJob job, StratumConnection connection, string nonce, string solution) =>
            job.ProcessShareAsync(connection, "worker", nonce, solution, CancellationToken.None);
        Assert.Equal(10 * EthereumConstants.Pow2x32, (await Submit(old, worker, "1", Cycle(1))).Share.Difficulty);
        Assert.Equal(10 * EthereumConstants.Pow2x32, (await Submit(old, worker, "01", Cycle(2))).Share.Difficulty);
        var other = new EthereumWorkerContext();
        other.Init(10, null, new MockMasterClock());
        Assert.Equal("duplicate share", (await Assert.ThrowsAsync<StratumException>(() =>
            Submit(source.ForWorker(other), Connect(other, "other"), "0001", Cycle(1).ToUpperInvariant()))).Message);

        context.SetDifficulty(100);
        var hard = source.ForWorker(context, () => "2");
        Assert.Equal(StratumError.LowDifficultyShare, (await Assert.ThrowsAsync<StratumException>(() => Submit(hard, worker, "1", Cycle(3)))).Code);
        context.SetDifficulty(10);
        var easy = source.ForWorker(context, () => "3");
        Assert.Equal(10 * EthereumConstants.Pow2x32, (await Submit(easy, worker, "0001", Cycle(3))).Share.Difficulty);
        source.Verification.Valid = false;
        Assert.Equal("bad hash", (await Assert.ThrowsAsync<StratumException>(() => Submit(easy, worker, "1", Cycle(4)))).Message);
        source.Verification.Valid = true;
        Assert.Equal(10 * EthereumConstants.Pow2x32, (await Submit(easy, worker, "1", Cycle(4))).Share.Difficulty);

        var calls = source.Verification.Calls;
        foreach(var solution in new[] { Cycle(5) + "f", "0x" + Cycle(5), ProofEncodingBoundaryTests.NibbleAlias(Cycle(5)), Cycle(5) + "\n" })
            Assert.Equal("bad solution", (await Assert.ThrowsAsync<StratumException>(() => Submit(easy, worker, "1", solution))).Message);
        Assert.Equal(calls, source.Verification.Calls);
        var credits = new ConcurrentBag<double>();
        var failures = new ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            try { credits.Add((await Submit(i % 2 == 0 ? old : easy, worker, i % 2 == 0 ? "1" : "0000000000000001",
                i % 2 == 0 ? Cycle(5) : Cycle(5).ToUpperInvariant())).Share.Difficulty); }
            catch(StratumException ex) { failures.Add(ex.Message); }
        })));
        Assert.Single(credits);
        Assert.Equal(31, failures.Count);
        Assert.All(failures, message => Assert.Equal("duplicate share", message));
        Assert.Equal(10 * EthereumConstants.Pow2x32, credits.Sum());
    }

    private sealed class LinuxNativeFactAttribute : FactAttribute
    {
        public LinuxNativeFactAttribute()
        {
            if(!OperatingSystem.IsLinux()) Skip = "Requires the Linux native Cortex verifier";
        }
    }
    [LinuxNativeFact]
    public async Task NativeRejectedCycleDoesNotConsumeAcceptedProofIdentity()
    {
        var context = new EthereumWorkerContext();
        context.Init(10, null, new MockMasterClock());
        var worker = Connect(context, "cortex-native");
        var job = new CortexJob("1", Template(), LogManager.GetCurrentClassLogger(), Substitute.For<IEthashLight>()).ForWorker(context);
        foreach(var nonce in new[] { "1", "0000000000000001" })
            Assert.Equal("bad hash", (await Assert.ThrowsAsync<StratumException>(() =>
                job.ProcessShareAsync(worker, "worker", nonce, new string('0', 336), CancellationToken.None))).Message);
    }
}
