using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Dapper;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence.Postgres;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain.Bitcoin;
using Miningcore.Tests.Util.Postgres;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public partial class MergedMiningPayoutRegtestTests
{
    [MergedMiningDaemonIntegrationFact]
    public async Task PausedExample_RealStartupMiningPersistenceAndOwnershipRecovery()
    {
        ModuleInitializer.Initialize();
        await using var litecoin = await RegtestNode.StartAsync(RegtestChain.Litecoin);
        await using var dogecoin = await RegtestNode.StartAsync(RegtestChain.Dogecoin);
        await litecoin.MineMatureRewardAsync();
        await dogecoin.MineMatureRewardAsync();
        var ltcAddress = (await litecoin.RpcAsync("getnewaddress", true, "pause", "legacy")).Value<string>();
        var dogeAddress = (await dogecoin.RpcAsync("getnewaddress", true)).Value<string>();
        var schema = $"miningcore_pause_{Guid.NewGuid():N}";
        var connectionString = Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var directory = Path.Combine(Path.GetTempPath(), schema);
        Directory.CreateDirectory(directory);

        try
        {
            await connection.ExecuteAsync($"CREATE SCHEMA {schema}; SET search_path TO {schema}, public");
            await connection.ExecuteAsync((await File.ReadAllTextAsync(PostgresTestScripts.PathFor("createdb.sql")))
                .Replace("SET ROLE miningcore;", ""));
            var database = new PgConnectionFactory(new NpgsqlConnectionStringBuilder(connectionString)
                { SearchPath = $"{schema},public" }.ConnectionString);
            var mapper = AutoMapperFactory.CreateMapper();
            var shares = new ShareRepository(mapper);
            var blocks = new BlockRepository(mapper);
            var owner = Guid.NewGuid();
            await connection.ExecuteAsync("INSERT INTO payout_manager_ownership(id,generation,owner_id) VALUES(1,7,@owner) ON CONFLICT(id) DO UPDATE SET generation=7,owner_id=@owner", new { owner });

            // Start from the shipped example, changing only lab endpoints, addresses and schemes.
            var json = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                "examples", "litecoin_dogecoin_merged_mining_pool.json")));
            json["paymentProcessing"]["paused"] = true;
            json["api"]["enabled"] = false;
            json["shareRecoveryFile"] = Path.Combine(directory, "shares.json");
            json["shareRecoveryStateDirectory"] = directory;
            var poolJson = (JArray) json["pools"];
            poolJson[0]["address"] = ltcAddress;
            poolJson[1]["address"] = dogeAddress;
            var serializer = JsonSerializer.Create(ModuleInitializer.Container.Resolve<JsonSerializerSettings>());
            poolJson[0]["daemons"] = JArray.FromObject(new[] { litecoin.WalletEndpoint }, serializer);
            poolJson[1]["daemons"] = JArray.FromObject(new[] { dogecoin.WalletEndpoint }, serializer);
            poolJson[0]["allowPeerlessRegtest"] = true;
            poolJson[0]["blockRefreshInterval"] = 100;
            var filename = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(filename, json.ToString());
            var cluster = Program.ReadConfig(filename);
            Program.ValidateConfig(cluster, recoveryMode: false);
            Program.AssignPoolTemplates(cluster.Pools, ModuleInitializer.CoinTemplates);
            var clock = new StandardClock();
            var pausedPools = new List<PoolConfig>();

            // SOLO proves ordinary-share and beneficiary preservation; mixed PPS/PROP proves liabilities.
            foreach(var scheme in new[] { PayoutScheme.SOLO, PayoutScheme.PPS })
            {
                // Preserve each accounting contract through resume instead of changing schemes
                // under an existing backlog. Each pair has separate pool IDs and beneficiaries.
                cluster.Pools = json["pools"].ToObject<PoolConfig[]>(serializer);
                Program.AssignPoolTemplates(cluster.Pools, ModuleInitializer.CoinTemplates);
                foreach(var pool in cluster.Pools)
                {
                    pool.Id += "-" + scheme;
                    pool.RewardRecipients = Array.Empty<RewardRecipient>();
                    pool.PaymentProcessing.MinimumPayment = 0.0001m;
                    pool.PaymentProcessing.Extra = new Dictionary<string, object> { ["minersPayTxFees"] = false };
                }
                var merged = (JObject) cluster.Pools[0].Extra["mergedMining"];
                merged["auxPoolId"] = cluster.Pools[1].Id;
                pausedPools.AddRange(cluster.Pools);
                cluster.Pools[0].PaymentProcessing.PayoutScheme = scheme;
                cluster.Pools[1].PaymentProcessing.PayoutScheme = scheme == PayoutScheme.SOLO ? PayoutScheme.SOLO : PayoutScheme.PROP;
                Program.ValidateMergedMiningDeployment(cluster, requireAssignedTemplates: true);
                Program.ValidatePpsDeployment(cluster, requireAssignedTemplates: true);
                await Program.EnsureMergedMiningSchemaAsync(cluster, database, blocks, CancellationToken.None);
                await Program.EnsureShareAccountingSchemaAsync(cluster, database, shares, CancellationToken.None);
                Assert.False(Program.ShouldRunPaymentProcessor(cluster));
                var bus = new MessageBus();
                using var recorder = new ShareRecorder(database, mapper,
                    ModuleInitializer.Container.Resolve<JsonSerializerSettings>(), shares, blocks, cluster, bus);
                var committedCandidates = new List<Share>();
                var candidateRecorder = Substitute.For<IBlockCandidateRecorder>();
                candidateRecorder.PersistBlockCandidateAsync(Arg.Any<Share>()).Returns(async call =>
                {
                    var share = call.Arg<Share>();
                    await recorder.PersistBlockCandidateAsync(share);
                    lock(committedCandidates) committedCandidates.Add(share);
                });
                var pendingShares = new ConcurrentQueue<Task>();
                using var subscription = bus.Listen<Share>().Subscribe(share =>
                {
                    // This fixture drives the job manager without a TCP transport. Supply the
                    // endpoint metadata normally populated by Stratum for both projections.
                    share.IpAddress ??= "127.0.0.1";
                    if(share.PairedShare != null)
                        share.PairedShare.IpAddress ??= share.IpAddress;
                    var persisted = recorder.PersistSharesAsync(new[] { share });
                    pendingShares.Enqueue(persisted);
                    share.SetPersistenceAdmission(persisted);
                });
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                // Job managers have no IDisposable API; cancel their lifetime before subscriptions leave scope.
                using var stopManager = Disposable.Create(cancellation.Cancel);
                var manager = new MergedMiningBitcoinJobManager(ModuleInitializer.Container, clock, bus,
                    new BitcoinExtraNonceProvider("pause-regtest", null), candidateRecorder);
                manager.Configure(cluster.Pools[0], cluster);
                await manager.StartAsync(cancellation.Token);
                var firstJob = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var jobs = manager.Jobs.Subscribe(_ => firstJob.TrySetResult(), ex => firstJob.TrySetException(ex));
                await firstJob.Task.WaitAsync(cancellation.Token);
                var worker = new StratumConnection(new NullLogger(LogManager.LogFactory),
                    new RecyclableMemoryStreamManager(), clock, "pause-regtest", false);
                var context = new MergedMiningBitcoinWorkerContext
                {
                    Miner = ltcAddress, AuxiliaryMiner = dogeAddress, Worker = "pause", UserAgent = "regtest",
                };
                context.Init(0.000000001, null, clock);
                worker.SetContext(context);
                var subscriber = manager.GetSubscriberData(worker);
                var extraNonce2 = new string('0', (int) subscriber[1] * 2);
                for(var nonce = 0; nonce < 128; nonce++)
                {
                    var job = manager.GetJobForStratum();
                    context.AddJob(job, 4);
                    await manager.SubmitShareAsync(worker, new object[]
                    {
                        ltcAddress + ".pause", job.JobId, extraNonce2,
                        job.BlockTemplate.CurTime.ToString("x8"), nonce.ToString("x8"),
                    }, cancellation.Token);
                    await Task.WhenAll(pendingShares.ToArray());
                    if(committedCandidates.Any(x => x.PoolId == cluster.Pools[0].Id) &&
                       committedCandidates.Any(x => x.PoolId == cluster.Pools[1].Id))
                        break;
                }
                await cancellation.CancelAsync();
                Assert.Contains(committedCandidates, x => x.PoolId == cluster.Pools[0].Id && x.Miner == ltcAddress && x.BlockType == "merged-parent");
                Assert.Contains(committedCandidates, x => x.PoolId == cluster.Pools[1].Id && x.Miner == dogeAddress && x.BlockType == "auxpow");
                var parentBlock = await litecoin.RpcAsync("getblock", false,
                    committedCandidates.First(x => x.BlockType == "merged-parent").BlockHash);
                var auxiliaryBlock = await dogecoin.RpcAsync("getblock", false,
                    committedCandidates.First(x => x.BlockType == "auxpow").BlockHash);
                Assert.True(parentBlock["confirmations"].Value<int>() > 0);
                Assert.True(auxiliaryBlock["confirmations"].Value<int>() > 0);
                var blockCount = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM blocks");
                foreach(var share in committedCandidates)
                    await recorder.PersistBlockCandidateAsync(share);
                Assert.Equal(blockCount, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM blocks"));
                Assert.True(await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM shares") > 0);
                if(scheme == PayoutScheme.PPS)
                {
                    Assert.True(await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits") > 0);
                    Assert.True(await connection.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances") > 0);
                }
            }
            Assert.Equal(owner, await connection.ExecuteScalarAsync<Guid>("SELECT owner_id FROM payout_manager_ownership WHERE id=1"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payments"));
            Assert.DoesNotContain(await litecoin.RpcAsync("listtransactions", true, "*", 1000), x => x["category"]?.Value<string>() == "send");
            Assert.DoesNotContain(await dogecoin.RpcAsync("listtransactions", true, "*", 1000), x => x["category"]?.Value<string>() == "send");

            cluster.Pools = pausedPools.ToArray();
            cluster.PaymentProcessing.Paused = false;
            Assert.True(Program.ShouldRunPaymentProcessor(cluster));
            await using(var stale = new PostgresPayoutManagerLease(database))
                Assert.False(await stale.TryAcquireAsync(CancellationToken.None));
            // Explicit operator recovery, isolated test schema only; pause never performs this mutation.
            await connection.ExecuteAsync("UPDATE payout_manager_ownership SET owner_id=NULL WHERE id=1 AND owner_id=@owner", new { owner });
            await using(var resumed = new PostgresPayoutManagerLease(database))
            {
                Assert.True(await resumed.TryAcquireAsync(CancellationToken.None));
                await using var second = new PostgresPayoutManagerLease(database);
                Assert.False(await second.TryAcquireAsync(CancellationToken.None));
                Assert.Equal(8, await connection.ExecuteScalarAsync<int>("SELECT generation FROM payout_manager_ownership WHERE id=1"));

                // Repeat the real startup path with the pause lifted and valid payout ownership.
                using var resumedLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var stopResumedManager = Disposable.Create(resumedLifetime.Cancel);
                var resumedManager = new MergedMiningBitcoinJobManager(ModuleInitializer.Container, clock,
                    new MessageBus(), new BitcoinExtraNonceProvider("resumed-regtest", null),
                    Substitute.For<IBlockCandidateRecorder>());
                resumedManager.Configure(cluster.Pools[0], cluster);
                await resumedManager.StartAsync(resumedLifetime.Token);
                var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var resumedJobs = resumedManager.Jobs.Subscribe(_ => online.TrySetResult(), ex => online.TrySetException(ex));
                await online.Task.WaitAsync(resumedLifetime.Token);
                Assert.NotNull(resumedManager.GetJobForStratum());
                await resumedLifetime.CancelAsync();
            }
            await AssertResumedWalletLifecycleAsync(cluster, database, connection, litecoin, dogecoin);
        }
        finally
        {
            await connection.ExecuteAsync("SET search_path TO public");
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            Directory.Delete(directory, true);
        }
    }
}
