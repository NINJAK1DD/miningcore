using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Ergo;
using Miningcore.Blockchain.Ethereum;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Ethash;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Mining;

public class SemanticProofIdentityTests
{
    private sealed class LinuxNativeFactAttribute : FactAttribute
    {
        public LinuxNativeFactAttribute()
        {
            if(!OperatingSystem.IsLinux()) Skip = "Autolykos uses the Linux native Blake2b library";
        }
    }

    private static StratumConnection Connect(Miningcore.Mining.WorkerContextBase context, string id)
    {
        var result = new StratumConnection(new NullLogger(LogManager.LogFactory),
            new RecyclableMemoryStreamManager(), new MockMasterClock(), id, true);
        result.SetContext(context);
        return result;
    }

    [LinuxNativeFact]
    public void ErgoUnusedTimeAndHexCaseCannotMintAnotherCredit()
    {
        var context = new ErgoWorkerContext { ExtraNonce1 = "abcdef01" };
        context.Init(1e-20, null, new MockMasterClock());
        var connection = Connect(context, "ergo-identity");
        var source = new ErgoJob();
        source.Init(new WorkMessage { Msg = new string('0', 64), Height = 1, B = BigInteger.One }, 2, 4, "1");
        var old = source.ForWorker(context);
        var accepted = old.ProcessShare(connection, "unused", "11111111", "abcdef0100000001");
        Assert.False(accepted.IsBlockCandidate);
        Assert.Equal(1e-20 / ErgoConstants.ShareMultiplier, accepted.Difficulty);
        context.SetDifficulty(2e-20);
        var current = source.ForWorker(context, () => "2");
        foreach(var time in new[] { "22222222", "undefined", "arbitrary" })
            Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() =>
                current.ProcessShare(connection, "unused", time, "ABCDEF0100000001")).Code);

        // Rejected proofs must remain usable under an eligible assignment.
        context.SetDifficulty(double.MaxValue);
        var hard = source.ForWorker(context, () => "3");
        Assert.Equal(StratumError.LowDifficultyShare, Assert.Throws<StratumException>(() =>
            hard.ProcessShare(connection, "unused", "1", "abcdef0100000002")).Code);
        context.SetDifficulty(1e-20);
        var easy = source.ForWorker(context, () => "4");
        Assert.Equal(1e-20 / ErgoConstants.ShareMultiplier,
            easy.ProcessShare(connection, "unused", "2", "ABCDEF0100000002").Difficulty);

        var credits = new ConcurrentBag<double>();
        var failures = new ConcurrentBag<StratumError>();
        Parallel.For(0, 32, i =>
        {
            try { credits.Add((i % 2 == 0 ? old : easy).ProcessShare(connection, "unused", i.ToString(),
                i % 2 == 0 ? "abcdef0100000003" : "ABCDEF0100000003").Difficulty); }
            catch(StratumException ex) { failures.Add(ex.Code); }
        });
        Assert.Single(credits);
        Assert.Equal(31, failures.Count);
        Assert.All(failures, code => Assert.Equal(StratumError.DuplicateShare, code));
        Assert.Equal(1e-20 / ErgoConstants.ShareMultiplier, credits.Sum());
    }

    // Controlled final Ethash results exercise the actual V1 manager, target,
    // snapshot registry and acceptance boundary; these are not consensus vectors.
    private sealed class Cache : IEthashCache
    {
        internal double Difficulty = 20;
        public bool Compute(ILogger logger, byte[] header, ulong nonce, out byte[] mix, out byte[] result)
        {
            mix = new byte[32];
            var value = EthereumConstants.BigMaxValue / new BigInteger(Difficulty * EthereumConstants.Pow2x32);
            result = value.ToByteArray(true, true).PadFront(0, 32);
            return true;
        }
        public void Dispose() { }
    }

    private sealed class Manager(IComponentContext ctx) : EthereumJobManager(ctx, new MockMasterClock(),
        Substitute.For<IMessageBus>(), Substitute.For<IExtraNonceProvider>())
    {
        internal void Initialize()
        {
            poolConfig = new PoolConfig { Id = "identity" };
            clusterConfig = new ClusterConfig { ClusterName = "lab" };
        }
    }

    [Theory]
    [InlineData("1", "01")]
    [InlineData("0x1", "0000000000000001")]
    [InlineData("a", "000000000000000A")]
    public async Task EthereumV1EquivalentNoncesAndConnectionsHaveOnlyOneCredit(string first, string second)
    {
        var context = new EthereumWorkerContext();
        context.Init(10, null, new MockMasterClock());
        var connection = Connect(context, "eth-identity");
        var cache = new Cache();
        var ethash = Substitute.For<IEthashLight>();
        ethash.GetCacheAsync(Arg.Any<ILogger>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Returns(cache);
        var source = new EthereumJob("1", new EthereumBlockTemplate
            { Header = new string('0', 64), Target = new string('0', 64), Height = 1 }, LogManager.GetCurrentClassLogger(), ethash);
        var old = source.ForWorker(context);
        context.AddJob(old, 8);
        var manager = new Manager(Substitute.For<IComponentContext>());
        manager.Initialize();
        Task<Share> Submit(StratumConnection worker, string nonce) => manager.SubmitShareV1Async(worker,
            new[] { nonce, source.BlockTemplate.Header }, "worker", CancellationToken.None);
        var accepted = await Submit(connection, first);
        Assert.False(accepted.IsBlockCandidate);
        Assert.Equal(10 * EthereumConstants.Pow2x32, accepted.Difficulty);
        Assert.Equal(StratumError.MinusOne, (await Assert.ThrowsAsync<StratumException>(() => Submit(connection, second))).Code);
        var other = new EthereumWorkerContext();
        other.Init(10, null, new MockMasterClock());
        other.AddJob(source.ForWorker(other), 8);
        Assert.Equal(StratumError.MinusOne, (await Assert.ThrowsAsync<StratumException>(() => Submit(Connect(other, "other"), second))).Code);

        context.SetDifficulty(100);
        context.AddJob(source.ForWorker(context, () => "2"), 8);
        Assert.Equal(StratumError.LowDifficultyShare,
            (await Assert.ThrowsAsync<StratumException>(() => Submit(connection, "0x2"))).Code);
        context.SetDifficulty(10);
        context.AddJob(source.ForWorker(context, () => "3"), 8);
        Assert.Equal(10 * EthereumConstants.Pow2x32, (await Submit(connection, "0002")).Difficulty);

        var credits = new ConcurrentBag<double>();
        var failures = new ConcurrentBag<StratumError>();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            try { credits.Add((await Submit(connection, i % 2 == 0 ? "0x3" : "0000000000000003")).Difficulty); }
            catch(StratumException ex) { failures.Add(ex.Code); }
        })));
        Assert.Single(credits);
        Assert.Equal(31, failures.Count);
        Assert.All(failures, code => Assert.Equal(StratumError.MinusOne, code));
        Assert.Equal(10 * EthereumConstants.Pow2x32, credits.Sum());
    }
}
