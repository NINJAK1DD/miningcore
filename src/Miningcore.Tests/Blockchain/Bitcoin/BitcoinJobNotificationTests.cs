using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Blockchain.Satoshicash;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using Miningcore.Time;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;
using Transaction = NBitcoin.Transaction;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinJobNotificationTests : TestBase
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void ManagerNotification_InterleavedWorkerIssuancePreservesFlags(
        bool direct, bool firstClean)
    {
        var source = CreateJob("bitcoin", 3);
        var clock = MockMasterClock.FromTicks(DateTimeOffset.FromUnixTimeSeconds(
            source.BlockTemplate.CurTime).UtcTicks);
        var manager = new NotificationJobManager(container, clock);
        manager.Configure(new PoolConfig
        {
            Id = "notification-test",
            Coin = "bitcoin",
            Template = ModuleInitializer.CoinTemplates["bitcoin"],
            Daemons = new[] { new DaemonEndpointConfig() },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true, PayoutScheme = PayoutScheme.SOLO,
            },
            Extra = new Dictionary<string, object> { ["soloCoinbasePayout"] = direct },
        }, new ClusterConfig());
        manager.SetCurrent(source);

        var broadcast = manager.Notification(firstClean);
        var miner = new KeyId(Enumerable.Repeat((byte) 1, 20).ToArray());
        // These are the two production manager paths used by BitcoinPool.CreateWorkerJob.
        var workerJob = direct
            ? manager.GetDirectJobForStratum(miner.GetAddress(Network.RegTest).ToString(), miner, 7)
            : manager.GetJobForStratum();
        var workerNotification = Assert.IsType<object[]>(workerJob.GetJobParams(!firstClean));
        // Deliberately issue and discard later notifications: these calls must not
        // retroactively change either outstanding manager/worker notification.
        _ = manager.Notification(!firstClean);
        _ = workerJob.GetJobParams(firstClean);

        Assert.Equal(firstClean, broadcast[8]);
        Assert.Equal(!firstClean, workerNotification[8]);
        Assert.Equal(source.JobId, broadcast[0]);
        Assert.Equal(workerJob.JobId, workerNotification[0]);
        if(direct)
        {
            Assert.NotSame(source, workerJob);
            Assert.Equal(7L, workerJob.DirectPayoutGeneration);
            var coinbase = Transaction.Parse((string) workerNotification[2] + "00000001" +
                "00000000000000" + (string) workerNotification[3], Network.RegTest);
            Assert.Equal(miner.ScriptPubKey, coinbase.Outputs[0].ScriptPubKey);
        }
        else
            Assert.Same(source, workerJob);
    }

    public static IEnumerable<object[]> NotificationCases()
    {
        foreach(var path in new[] { "bitcoin", "direct", "litecoin", "merged", "satoshicash" })
        foreach(var transactionCount in new[] { 0, 3 })
        foreach(var firstClean in new[] { true, false })
            yield return new object[] { path, transactionCount, firstClean };
    }

    [Theory]
    [MemberData(nameof(NotificationCases))]
    public void GetJobParams_ParallelIssuanceOwnsEveryMutableContainer(
        string path, int transactionCount, bool firstClean)
    {
        var job = CreateJob(path, transactionCount);
        var notifications = new object[128][];
        Parallel.For(0, notifications.Length, i =>
            notifications[i] = Assert.IsType<object[]>(job.GetJobParams(
                i % 2 == 0 ? firstClean : !firstClean)));

        Assert.Equal(notifications.Length, notifications.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(notifications.Length, notifications.Select(x => x[4])
            .Distinct(ReferenceEqualityComparer.Instance).Count());
        for(var i = 0; i < notifications.Length; i++)
            Assert.Equal(i % 2 == 0 ? firstClean : !firstClean, notifications[i][8]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NotifyAsync_QueuedSnapshotsRetainFlagsAndBranchesOnTheWire(bool firstClean)
    {
        var job = CreateJob("bitcoin", 3);
        var clock = MockMasterClock.FromTicks(DateTimeOffset.FromUnixTimeSeconds(
            job.BlockTemplate.CurTime).UtcTicks);
        var connection = new StratumConnection(NLog.LogManager.GetCurrentClassLogger(),
            container.Resolve<RecyclableMemoryStreamManager>(), clock, "notification-queue", false);
        var first = Assert.IsType<object[]>(job.GetJobParams(firstClean));
        var expectedFirst = JArray.FromObject(first);
        await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, first);
        var second = Assert.IsType<object[]>(job.GetJobParams(!firstClean));
        var expectedSecond = JArray.FromObject(second);
        await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, second);

        // The real send queue already owns both notifications, but its consumer
        // starts only below. Interference therefore precedes serialization exactly.
        var later = Assert.IsType<object[]>(job.GetJobParams(firstClean));
        Array.Fill((string[]) later[4], "changed-branch");
        Array.Fill(later, "changed-field");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var endpoint = (IPEndPoint) listener.LocalEndpoint;
        var accept = listener.AcceptSocketAsync(stop.Token);
        await client.ConnectAsync(endpoint.Address, endpoint.Port, stop.Token);
        using var accepted = await accept;
        var errors = new List<Exception>();
        var dispatch = connection.DispatchAsync(accepted, stop.Token,
            new StratumEndpoint(endpoint, new PoolEndpoint()),
            (IPEndPoint) accepted.RemoteEndPoint, null,
            (_, _, _) => Task.CompletedTask, _ => { }, (_, error) => errors.Add(error));
        var wireError = await Record.ExceptionAsync(async () =>
        {
            // Disposing the reader must not close the peer before dispatch drains.
            using var reader = new StreamReader(client.GetStream(), leaveOpen: true);
            foreach(var expected in new[] { expectedFirst, expectedSecond })
            {
                var line = await reader.ReadLineAsync(stop.Token);
                var wire = JObject.Parse(Assert.IsType<string>(line));
                Assert.Equal(BitcoinStratumMethods.MiningNotify, wire.Value<string>("method"));
                Assert.True(JToken.DeepEquals(expected, wire["params"]));
            }
        });
        var dispatchError = await Record.ExceptionAsync(async () =>
        {
            stop.Cancel();
            await dispatch.WaitAsync(TimeSpan.FromSeconds(10));
        });
        // Report the wire failure first if teardown also failed. The peer remains
        // open until dispatch has observed cancellation and completed its I/O.
        if(wireError != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(wireError).Throw();
        Assert.Null(dispatchError);
        Assert.Empty(errors);
    }

    [Fact]
    public void GetJobParams_ClonesStringArraysIndependentOfTheirSlot()
    {
        var template = new object[] { "id", new[] { "first" }, null, new[] { "second" }, false };
        var job = new LayoutJob(template);
        var first = Assert.IsType<object[]>(job.GetJobParams(true));
        Assert.Null(first[2]);
        ((string[]) first[1])[0] = "changed";
        ((string[]) first[3])[0] = "changed";
        var second = Assert.IsType<object[]>(job.GetJobParams(false));
        Assert.Equal("first", ((string[]) second[1])[0]);
        Assert.Equal("second", ((string[]) second[3])[0]);
        Assert.False((bool) template[^1]);
    }

    [Theory]
    [MemberData(nameof(NotificationCases))]
    public void GetJobParams_DelayedSerializationPreservesEachNotification(
        string path, int transactionCount, bool firstClean)
    {
        var job = CreateJob(path, transactionCount);
        var first = Assert.IsType<object[]>(job.GetJobParams(firstClean));
        var expected = JsonConvert.SerializeObject(first);
        var queued = new JsonRpcRequest<object[]>(BitcoinStratumMethods.MiningNotify, first, null);

        // Deterministically issue the opposing flag before serializing queued work,
        // just as the Stratum send queue can do. No timing or scheduling assumption.
        var second = Assert.IsType<object[]>(job.GetJobParams(!firstClean));
        var firstWire = JObject.Parse(JsonConvert.SerializeObject(queued));
        Assert.Equal(firstClean, firstWire["params"]![8]!.Value<bool>());
        Assert.Equal(expected, JsonConvert.SerializeObject(first));
        Assert.Equal(!firstClean, second[8]);
        Assert.NotSame(first, second);

        var third = Assert.IsType<object[]>(job.GetJobParams(firstClean));
        Assert.Equal(!firstClean, second[8]);
        Assert.NotSame(first, third);
        Assert.Equal(expected, JsonConvert.SerializeObject(third));
    }

    [Theory]
    [MemberData(nameof(NotificationCases))]
    public void GetJobParams_CallerMutationCannotChangeOtherOrFutureNotifications(
        string path, int transactionCount, bool clean)
    {
        var job = CreateJob(path, transactionCount);
        var first = Assert.IsType<object[]>(job.GetJobParams(clean));
        var second = Assert.IsType<object[]>(job.GetJobParams(clean));
        var expected = JsonConvert.SerializeObject(second);

        // Both the outer array and the mutable Merkle container belong to the caller.
        var branches = Assert.IsType<string[]>(first[4]);
        for(var i = 0; i < branches.Length; i++)
            branches[i] = "caller-modified-branch";
        Array.Fill(first, "caller-modified-field");

        Assert.Equal(expected, JsonConvert.SerializeObject(second));
        Assert.Equal(expected, JsonConvert.SerializeObject(job.GetJobParams(clean)));
    }

    [Theory]
    [MemberData(nameof(NotificationCases))]
    public void GetJobParams_PreservesWireLayoutAndCachesImmutableValues(
        string path, int transactionCount, bool clean)
    {
        var job = CreateJob(path, transactionCount);
        var first = Assert.IsType<object[]>(job.GetJobParams(clean));
        var second = Assert.IsType<object[]>(job.GetJobParams(clean));
        Assert.Equal(9, first.Length);
        Assert.Equal(job.JobId, first[0]);
        Assert.Equal(job.BlockTemplate.PreviousBlockhash.HexToByteArray()
            .ReverseByteOrder().ToHexString(), first[1]);
        var coinbase = Transaction.Parse((string) first[2] + "00000001" +
            "00000000000000" + (string) first[3], Network.RegTest);
        Assert.Single(coinbase.Inputs);
        Assert.Equal(job.BlockTemplate.CoinbaseValue,
            coinbase.Outputs.Sum(output => output.Value.Satoshi));
        Assert.Equal("20000000", first[5]);
        Assert.Equal("207fffff", first[6]);
        Assert.Equal("6553f100", first[7]);
        Assert.Equal(clean, first[8]);

        for(var i = 0; i < 8; i++)
        {
            if(i != 4)
            {
                Assert.IsType<string>(first[i]);
                Assert.Same(first[i], second[i]);
            }
        }

        var branches = Assert.IsType<string[]>(first[4]);
        var otherBranches = Assert.IsType<string[]>(second[4]);
        Assert.NotSame(branches, otherBranches);
        Assert.Equal(transactionCount == 0 ? 0 : 2, branches.Length);
        Assert.Equal(branches, otherBranches);
        for(var i = 0; i < branches.Length; i++)
            Assert.Same(branches[i], otherBranches[i]);

        var wire = JArray.Parse(JsonConvert.SerializeObject(first));
        Assert.IsType<JArray>(wire[4]);
        Assert.Equal(JTokenType.Boolean, wire[8].Type);
        Assert.Equal(JsonConvert.SerializeObject(first), JsonConvert.SerializeObject(second));
    }

    private static BitcoinJob CreateJob(string path, int transactionCount)
    {
        var coinId = path switch
        {
            "direct" => "bitcoin",
            "merged" => "litecoin",
            _ => path,
        };
        var coin = Assert.IsType<BitcoinTemplate>(ModuleInitializer.CoinTemplates[coinId]);
        var config = new PoolConfig { Id = "notification-test", Coin = coinId, Template = coin };
        var block = new BlockTemplate
        {
            Version = 0x20000000,
            PreviousBlockhash = string.Concat(Enumerable.Range(0, 32).Select(x => x.ToString("x2"))),
            CoinbaseValue = 5_000_000_000,
            Target = "7" + new string('f', 63),
            CurTime = 1_700_000_000,
            Bits = "207fffff",
            Height = 101,
            Extra = new Dictionary<string, object> { ["rx_epoch_duration"] = 604800 },
            // Distinct fixture transactions exercise nonempty multi-level branches.
            Transactions = Enumerable.Range(1, transactionCount).Select(CreateTransaction).ToArray(),
        };
        var clock = MockMasterClock.FromTicks(DateTimeOffset.FromUnixTimeSeconds(block.CurTime).UtcTicks);
        var destination = new KeyId(new byte[20]);
        var cluster = new ClusterConfig();

        if(path == "merged")
        {
            var merged = new MergedMiningBitcoinJob();
            merged.InitMerged(block, new AuxBlockTemplate
            {
                Hash = new string('a', 64), Bits = "207fffff",
            }, path, config, null, cluster, clock, destination, Network.RegTest,
                false, coin.ShareMultiplier, coin.CoinbaseHasherValue,
                coin.HeaderHasherValue, coin.BlockHasherValue);
            return merged;
        }

        if(path == "satoshicash")
        {
            var scash = new SatoshicashJob();
            scash.Init(block, path, config, null, cluster, clock, destination, Network.RegTest,
                false, coin.ShareMultiplier, coin.CoinbaseHasherValue,
                coin.HeaderHasherValue, coin.BlockHasherValue, "notification-test");
            return scash;
        }

        var job = new BitcoinJob();
        BitcoinDirectCoinbaseTemplate direct = null;
        if(path == "direct")
        {
            var miner = destination.GetAddress(Network.RegTest);
            direct = new BitcoinDirectCoinbaseTemplate
            {
                MinerAddress = miner.ToString(),
                MinerDestination = destination,
                MinerScriptPubKey = destination.ScriptPubKey.ToHex(),
                Recipients = Array.Empty<BitcoinDirectCoinbaseRecipient>(),
            };
        }
        job.InitDirect(block, path, config, null, cluster, clock, destination, Network.RegTest,
            false, coin.ShareMultiplier, coin.CoinbaseHasherValue,
            coin.HeaderHasherValue, coin.BlockHasherValue, direct);
        return job;
    }

    private static BitcoinBlockTransaction CreateTransaction(int index)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(index.ToString("x64")), 0)));
        tx.Outputs.Add(Money.Satoshis(1), new KeyId(new byte[20]));
        return new BitcoinBlockTransaction
        {
            TxId = tx.GetHash().ToString(),
            Hash = tx.GetWitHash().ToString(),
            Data = tx.ToHex(),
            Weight = tx.ToBytes().Length * 4,
        };
    }

    private sealed class NotificationJobManager : BitcoinJobManager
    {
        public NotificationJobManager(IComponentContext ctx, IMasterClock clock)
            : base(ctx, clock, new MessageBus(), Substitute.For<IExtraNonceProvider>())
        {
        }

        public void SetCurrent(BitcoinJob job)
        {
            currentJob = job;
            network = Network.RegTest;
            poolAddressDestination = new KeyId(new byte[20]);
        }

        public object[] Notification(bool clean) => (object[]) GetJobParamsForStratum(clean);
    }

    private sealed class LayoutJob : BitcoinJob
    {
        public LayoutJob(object[] template) => jobParams = template;
    }
}
