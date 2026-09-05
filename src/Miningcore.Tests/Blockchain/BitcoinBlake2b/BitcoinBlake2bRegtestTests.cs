using System;
using System.Buffers.Binary;
using System.IO;
using System.Data;
using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain.Bitcoin;
using Miningcore.Time;
using NBitcoin;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

internal sealed class BitcoinBlake2bIntegrationFactAttribute : FactAttribute
{
    internal const string BinaryEnvironmentVariable = "MININGCORE_TEST_BLAKE2B_BITCOIND";

    public BitcoinBlake2bIntegrationFactAttribute()
    {
        if(!File.Exists(Environment.GetEnvironmentVariable(BinaryEnvironmentVariable)))
            Skip = $"Set {BinaryEnvironmentVariable} to the pinned Knots binary";
    }
}

[Collection(BitcoinCorePayoutIntegrationCollection.Name)]
public class BitcoinBlake2bRegtestTests : TestBase
{
    [BitcoinBlake2bIntegrationFact]
    public Task HeaderV2_AllPayoutSchemes_SubmitAcceptedCustodialBlocks() => ExerciseAsync(false);

    [BitcoinBlake2bLedgerIntegrationFact]
    public Task HeaderV2_RealStratumPpsProof_CommitsExactlyOnceToPostgres() => ExerciseAsync(true);

    private async Task ExerciseAsync(bool requirePostgres)
    {
        await using var ledger = requirePostgres ? await BitcoinBlake2bLedgerProbe.CreateAsync() : null;
        await using var node = await BitcoinPayoutHandlerRegtestTests.BitcoinCoreRegtestNode.StartAsync(
            true, Environment.GetEnvironmentVariable(BitcoinBlake2bIntegrationFactAttribute.BinaryEnvironmentVariable),
            new[] { "-testactivationheight=blake2b@20", "-blake2b_headline=Miningcore BLAKE2b regtest" }, 19);
        BitcoinBlake2bJobManager.ValidateDaemonIdentity(
            (JObject) await node.RootRpcAsync("getnetworkinfo"), "regtest");
        var coin = Assert.IsType<BitcoinBlake2bTemplate>(ModuleInitializer.CoinTemplates["bitcoin-blake2b"]);
        var destination = BitcoinAddress.Create(await node.GetNewAddressAsync(), Network.RegTest);
        var recipient = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var pool = new PoolConfig
        {
            Id = "blake2b-regtest", Coin = "bitcoin-blake2b", Template = coin,
            Address = destination.ToString(),
            Extra = new Dictionary<string, object> { ["allowPeerlessRegtest"] = true },
            Banning = new PoolShareBasedBanningConfig { Enabled = false, CheckThreshold = 10 },
            RewardRecipients = new[] { new RewardRecipient { Address = recipient.ToString(), Percentage = 2 } },
        };
        var schemes = new[] { PayoutScheme.SOLO, PayoutScheme.PPS, PayoutScheme.PROP, PayoutScheme.PPLNS };
        var acceptedBlocks = new List<Miningcore.Persistence.Model.Block>();
        var acceptedShares = new List<Share>();
        for(var index = 0; index < schemes.Length; index++)
        {
            var height = 20 + index;
            pool.Id = "blake2b-regtest-" + schemes[index].ToString().ToLowerInvariant();
            pool.Daemons = new[] { node.WalletEndpoint };
            pool.PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true, PayoutScheme = schemes[index] };
            var recorder = Substitute.For<IBlockCandidateRecorder>();
            var bus = Substitute.For<IMessageBus>();
            Share published = null;
            bus.When(x => x.SendMessage(Arg.Any<Share>(), Arg.Any<string>())).Do(call =>
            {
                published = call.Arg<Share>();
                Assert.Single(ShareAccounting.ValidateAndFlatten(published,
                    new Dictionary<string, PoolConfig> { [pool.Id] = pool }));
            });
            var manager = new SubmissionManager(container, clock, bus, recorder);
            manager.Configure(pool, new ClusterConfig());
            using var managerStop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await manager.StartAsync(managerStop.Token);
            var template = (await node.RootRpcAsync("getblocktemplate",
                new JObject { ["rules"] = new JArray("segwit", "blake2b") })).ToObject<BlockTemplate>();
            Assert.Equal((uint) height, template.Height);
            Assert.Contains("!blake2b", template.Rules);
            var job = await manager.FetchJobAsync(managerStop.Token);
            await using var wire = new BitcoinBlake2bWireSession(container, clock, pool, manager, bus);
            var subscribe = await wire.RequestAsync("mining.subscribe", "Miningcore-header-v2-test");
            Assert.Equal(8, subscribe["result"][2].Value<int>());
            Assert.Equal(8, subscribe["result"][1].Value<string>().Length);
            Assert.Equal("mining.set_difficulty", (await wire.ReadAsync())["method"].Value<string>());
            var notification = await wire.ReadAsync();
            Assert.Equal("mining.notify", notification["method"].Value<string>());
            var wireJob = (JArray) notification["params"];
            var notify = wireJob.ToObject<object[]>();
            notify[4] = wireJob[4].ToObject<string[]>();
            var worker = wire.Connection;
            var authorize = await wire.RequestAsync("mining.authorize", destination + ".test", "d=0.000000002");
            Assert.True(authorize["result"].Value<bool>());
            Assert.Equal("mining.set_difficulty", (await wire.ReadAsync())["method"].Value<string>());
            var authorizedJob = await wire.ReadAsync();
            Assert.Equal("mining.notify", authorizedJob["method"].Value<string>());
            Assert.Equal(BitcoinBlake2bHeader.EncodeCompactTarget(BitcoinBlake2bHeader.TargetForDifficulty(2e-9))
                .ToString("x8"), authorizedJob["params"][6].Value<string>());
            // A FIFO response fence detects an extra notify from authorize;
            // RequestAsync intentionally discards intervening notifications.
            await wire.SendRequestAsync("mining.configure", new[] { "version-rolling" },
                new Dictionary<string, object> { ["version-rolling.mask"] = "1fffe000" });
            Assert.Null((await wire.ReadAsync())["method"]);
            worker.ContextAs<BitcoinWorkerContext>().SetDifficulty(0);
            Assert.Throws<StratumException>(() => wire.CreateJob());
            worker.ContextAs<BitcoinWorkerContext>().SetDifficulty(2e-9);
            var configure = await wire.RequestAsync("mining.configure", new[] { "version-rolling" },
                new Dictionary<string, object> { ["version-rolling.mask"] = "1fffe000" });
            Assert.False(configure["result"]["version-rolling"].Value<bool>());
            Assert.Null(worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask);
            var minimum = await wire.RequestAsync("mining.configure", new[] { "minimum-difficulty" },
                new Dictionary<string, object> { ["minimum-difficulty.value"] = 3e-9 });
            Assert.True(minimum["result"]["minimum-difficulty"].Value<bool>());
            var difficultyMessage = await wire.ReadAsync();
            Assert.Equal("mining.set_difficulty", difficultyMessage["method"].Value<string>());
            Assert.Equal(3e-9, difficultyMessage["params"][0].Value<double>());
            var configuredJob = await wire.ReadAsync();
            Assert.Equal("mining.notify", configuredJob["method"].Value<string>());
            Assert.Equal(BitcoinBlake2bHeader.EncodeCompactTarget(BitcoinBlake2bHeader.TargetForDifficulty(3e-9))
                .ToString("x8"), configuredJob["params"][6].Value<string>());
            Assert.NotEqual(notify[0], configuredJob["params"][0].Value<string>());
            var rejected = await wire.RequestAsync("mining.submit", destination + ".test",
                notify[0], "0000000000000000", notify[7], 1000000000000000L);
            Assert.NotNull(rejected["error"]);
            Assert.Null(published);
            Assert.Equal(1, worker.ContextAs<BitcoinWorkerContext>().Stats.InvalidShares);

            // VarDiff changes must update both the numeric notification and
            // the immutable compact target in notify, in that order.
            worker.ContextAs<BitcoinWorkerContext>().EnqueueNewDifficulty(2e-9);
            await wire.AnnounceJobAsync(job.GetJobParams(false));
            Assert.Equal(2e-9, (await wire.ReadAsync())["params"][0].Value<double>());
            var harderJob = (await wire.ReadAsync())["params"];
            Assert.NotEqual(notify[0], harderJob[0].Value<string>());
            Assert.Equal(BitcoinBlake2bHeader.EncodeCompactTarget(
                BitcoinBlake2bHeader.TargetForDifficulty(2e-9)).ToString("x8"), harderJob[6].Value<string>());
            worker.ContextAs<BitcoinWorkerContext>().EnqueueNewDifficulty(1e-9);
            await wire.AnnounceJobAsync(job.GetJobParams(false));
            Assert.Equal(1e-9, (await wire.ReadAsync())["params"][0].Value<double>());
            Assert.Equal(notify[0], (await wire.ReadAsync())["params"][0].Value<string>());
            Assert.Equal(78, Assert.IsType<string>(notify[2]).Length);
            Assert.Empty(Assert.IsType<string[]>(notify[4]));
            var time = Assert.IsType<string>(notify[7]);
            Share candidate = null;
            var nonceBytes = new byte[8];
            for(ulong nonce = 0; nonce < 100000; nonce++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(nonceBytes, nonce);
                // Independent Sia-style miner construction from the wire job,
                // not the pool's header builder: BLAKE2b(0 || coinb1 || EN1 || EN2),
                // then BLAKE2b(hidden-prev || nonce || time || root). Mirrors
                // the pinned CONVOY profile-0 miner work contract.
                var coinbaseInput = new byte[] { 0 }.Concat(((string) notify[2]).HexToByteArray())
                    .Concat(worker.ContextAs<BitcoinWorkerContext>().ExtraNonce1.HexToByteArray())
                    .Concat(new byte[8]).ToArray();
                var hasher = new Blake2b();
                var workRoot = new byte[32];
                hasher.Digest(coinbaseInput, workRoot);
                var asicInput = ((string) notify[1]).HexToByteArray().Concat(nonceBytes)
                    .Concat(time.HexToByteArray()).Concat(workRoot).ToArray();
                var proof = new byte[32];
                hasher.Digest(asicInput, proof);
                if(BitcoinBlake2bHeader.HashValue(proof) > BitcoinBlake2bHeader.DecodeCompactTarget(
                    BitcoinBlake2bHeader.ParseCompactBits((string) notify[6])))
                    continue;
                try
                {
                    var response = await wire.RequestAsync("mining.submit", destination + ".test",
                        notify[0], "0000000000000000", time, nonceBytes.ToHexString());
                    Assert.True(response["result"]?.Value<bool>() == true, response.ToString());
                    candidate = Assert.IsType<Share>(published);
                    if(candidate.IsBlockCandidate)
                        Assert.Equal(proof.ToHexString(), candidate.BlockHash);
                    if(candidate.IsBlockCandidate)
                        break;
                }
                catch(StratumException ex) when(ex.Code == StratumError.LowDifficultyShare) { }
            }
            Assert.True(candidate?.IsBlockCandidate);
            Assert.Equal(1e-9, candidate.Difficulty);
            if(schemes[index] == PayoutScheme.PPS)
            {
                Assert.True(candidate.BlockRecordEmitted);
                Assert.NotNull(candidate.PpsCalculatedAmount);
                Assert.NotNull(ShareAccounting.CreatePpsCredit(pool, candidate));
                if(ledger != null)
                    await ledger.AssertPpsPersistenceAsync(candidate, pool);
                await recorder.Received(1).PersistBlockCandidateAsync(Arg.Is<Share>(x =>
                    x.BlockOnly && x.PpsCalculatedAmount == null && x.RewardBasisSatoshis == 0));
            }
            else
                Assert.Null(candidate.PpsCalculatedAmount);
            Assert.Equal(candidate.BlockHash, (await node.RootRpcAsync("getbestblockhash")).Value<string>());
            var accepted = (JObject) await node.RootRpcAsync("getblock", candidate.BlockHash, 2);
            var coinbase = accepted["tx"][0];
            var outputs = coinbase["vout"].ToArray();
            // This isolated family uses conventional wallet settlement, not
            // Bitcoin direct SOLO. Fees are allocated by accounting, so the
            // complete coinbase value must first belong to the pool wallet.
            Assert.Contains(outputs, x => x["scriptPubKey"]?["hex"]?.Value<string>() == destination.ScriptPubKey.ToHex() &&
                x["value"].Value<decimal>() == template.CoinbaseValue / 100000000m);
            Assert.DoesNotContain(outputs, x => x["scriptPubKey"]?["hex"]?.Value<string>() == recipient.ScriptPubKey.ToHex());
            acceptedBlocks.Add(new Miningcore.Persistence.Model.Block
            {
                PoolId = pool.Id, BlockHeight = (ulong) height, Hash = candidate.BlockHash,
                Miner = candidate.Miner,
                TransactionConfirmationData = candidate.TransactionConfirmationData,
                Status = Miningcore.Persistence.Model.BlockStatus.Pending, Created = clock.Now,
            });
            acceptedShares.Add(candidate);
            var blockHex = (await node.RootRpcAsync("getblock", candidate.BlockHash, 0)).Value<string>();
            Assert.Equal("duplicate", (await node.RootRpcAsync("submitblock", blockHex)).Value<string>());
        }

        var cf = Substitute.For<IConnectionFactory>();
        var con = Substitute.For<IDbConnection>();
        var tx = Substitute.For<IDbTransaction>();
        cf.OpenConnectionAsync().Returns(con);
        con.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(tx);
        var balances = Substitute.For<IBalanceRepository>();
        var payments = Substitute.For<IPaymentRepository>();
        payments.TryBeginPaymentBatchAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>()).Returns(true);
        var handler = new BitcoinPayoutHandler(container, cf, AutoMapperFactory.CreateMapper(),
            Substitute.For<IShareRepository>(), Substitute.For<Miningcore.Persistence.Repositories.IBlockRepository>(), balances,
            payments, clock, Substitute.For<IMessageBus>(), new ActiveBlockGracePeriodTracker());
        pool.Address = destination.ToString();
        await handler.ConfigureAsync(new ClusterConfig(), pool, CancellationToken.None);
        var miningPool = Substitute.For<IMiningPool>();
        miningPool.Config.Returns(pool);
        var immature = await handler.ClassifyBlocksAsync(miningPool, acceptedBlocks.ToArray(), CancellationToken.None);
        Assert.Equal(4, immature.Length);
        Assert.All(immature, block => Assert.Equal(Miningcore.Persistence.Model.BlockStatus.Pending, block.Status));
        await node.GenerateAsync(101, CancellationToken.None);
        var matured = await handler.ClassifyBlocksAsync(miningPool, acceptedBlocks.ToArray(), CancellationToken.None);
        Assert.Equal(4, matured.Length);
        foreach(var block in matured)
        {
            var index = (int) block.BlockHeight - 20;
            pool.Id = block.PoolId;
            pool.PaymentProcessing.PayoutScheme = schemes[index];
            Assert.Equal(Miningcore.Persistence.Model.BlockStatus.Confirmed, block.Status);
            Assert.Equal(50m, block.Reward);
            Assert.Equal(49m, await handler.UpdateBlockRewardBalancesAsync(con, tx, miningPool, block, CancellationToken.None));
            if(ledger != null)
                await ledger.AssertSchemeSettlementAsync(acceptedShares[index], pool, block, handler);
        }
        await balances.Received(4).AddAmountAsync(con, tx, Arg.Any<string>(), recipient.ToString(), 1m, Arg.Any<string>());

        await handler.PayoutAsync(miningPool, new[]
        {
            new Miningcore.Persistence.Model.Balance { PoolId = pool.Id, Address = recipient.ToString(), Amount = 1m },
        }, CancellationToken.None);
        var paymentCall = Assert.Single(payments.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IPaymentRepository.TryBeginPaymentBatchAsync)));
        var transactionId = Assert.IsType<string>(paymentCall.GetArguments()[3]);
        Assert.NotNull(await node.RootRpcAsync("getmempoolentry", transactionId));
        await node.GenerateAsync(1, CancellationToken.None);
        Assert.True((await node.WalletRpcAsync("gettransaction", transactionId))["confirmations"].Value<int>() > 0);
    }

    private sealed class SubmissionManager : BitcoinBlake2bJobManager
    {
        internal SubmissionManager(IComponentContext ctx, IMasterClock clock,
            IMessageBus bus, IBlockCandidateRecorder recorder) :
            base(ctx, clock, bus, new BitcoinBlake2bExtraNonceProvider(), recorder) { }

        // Deterministic test clocking replaces only the background trigger.
        // Startup RPCs, activation validation and job construction are real.
        protected override void SetupJobUpdates(CancellationToken ct) { }
        internal async Task<BitcoinBlake2bJob> FetchJobAsync(CancellationToken ct)
        {
            await UpdateJob(ct, true);
            return Assert.IsType<BitcoinBlake2bJob>(GetJobForStratum());
        }
    }
}
