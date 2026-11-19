using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Numerics;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Pearl;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence.Repositories;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain.Pearl;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public partial class WalletPayoutControlIntegrationTests
{
    [PearlRelayIntegrationFact]
    public async Task PearlPpsAcceptanceKeepsCommittedIdentityAcrossRelayDuplicatesAndRestart()
    {
        await using var db = await Database.Create();
        var config = new ClusterConfig
        {
            ClusterName = "canonical-pearl-source",
            PaymentProcessing = new() { Enabled = true, Paused = true },
            ShareRelay = new() { PublishUrl = RelayUrl(), SharedEncryptionKey = RelayTestKey },
            Pools = new[] { new PoolConfig { Id = "pearl-relay", Coin = "pearl", Enabled = true,
                Address = PearlWorkTests.Address, Template = ModuleInitializer.CoinTemplates["pearl"],
                RewardRecipients = Array.Empty<RewardRecipient>(),
                Ports = new Dictionary<int, PoolEndpoint>(),
                Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 1 } },
                PaymentProcessing = new() { Enabled = true, PayoutScheme = PayoutScheme.PPS } } },
        };
        var pc = config.Pools[0];
        await using var sender = new AccountingLab(db, config);
        await sender.Start();
        var mapper = AutoMapperFactory.CreateMapper();
        var repository = new ShareRepository(mapper);
        var store = new PearlCandidateStore(db.Factory, repository, pc);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var scope = container.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(repository).As<IShareRepository>();
            builder.RegisterInstance(new BlockRepository(mapper)).As<IBlockRepository>();
            builder.RegisterInstance(sender.Coordinator).As<IMiningFailStopCoordinator>().ExternallyOwned();
        });
        var clock = new StandardClock();
        var logger = LogManager.CreateNullLogger();
        var pool = new PearlPool(scope, jsonSerializerSettings, db.Factory, Substitute.For<IStatsRepository>(),
            mapper, clock, sender.Bus, new RecyclableMemoryStreamManager(),
            new NicehashService(Substitute.For<IHttpClientFactory>(), cache));
        pool.Configure(pc, config);
        static void Set(object target, string name, object value)
        {
            for(var type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if(field != null) { field.SetValue(target, value); return; }
            }
            throw new MissingFieldException(name);
        }
        using var metadataStream = GetType().Assembly.GetManifestResourceStream("Miningcore.Tests.Fixtures.Pearl.srb-proof.json");
        using var metadataReader = new StreamReader(metadataStream);
        var metadata = JObject.Parse(await metadataReader.ReadToEndAsync());
        var proof = PearlPlainProofTests.RealProof();
        var header = Convert.FromHexString(metadata.Value<string>("header"));
        var target = new BigInteger(Convert.FromHexString(metadata.Value<string>("target")), true, true);
        var block = PearlWorkTests.Block();
        // Assign a captured real proof to an isolated fixture job. The production
        // native verifier checks its actual header/proof; no public chain is used.
        Set(block, "header", header);
        var worker = new PearlWorkerContext { Miner = PearlWorkTests.Address, Worker = "relay", IsAuthorized = true };
        worker.Init(PearlWork.Difficulty(target), null, clock);
        var job = worker.Issue(block, 2);
        var manager = new PearlJobManager(pc, PearlNetwork.Regtest, jsonSerializerSettings, sender.Bus, logger);
        Set(manager, "current", new PearlJobManager.Snapshot("fixture", block.PreviousBlockHash, "{}",
            TimeProvider.System.GetTimestamp(), block.Height, 1));
        var verifier = new PearlProofVerifier(Environment.GetEnvironmentVariable("MININGCORE_TEST_PEARL_WORKER"));
        await verifier.CheckVersionAsync(CancellationToken.None);
        Set(pool, "manager", manager);
        Set(pool, "config", new PearlPoolConfig());
        Set(pool, "verifier", verifier);
        Set(pool, "candidates", store);
        Set(pool, "logger", logger);
        var connection = new StratumConnection(logger, new RecyclableMemoryStreamManager(), clock, "pearl-relay", false);
        connection.SetContext(worker);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SendMessageOverride = (value, _) =>
        {
            Assert.True(JObject.FromObject(value).Value<bool>("result"));
            acknowledged.TrySetResult();
            return Task.CompletedTask;
        };
        Share accepted = null;
        using var acceptedSubscription = sender.Bus.Listen<Share>().Subscribe(value => accepted = value);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        pc.Ports[((IPEndPoint) listener.LocalEndpoint).Port] = new PoolEndpoint { Difficulty = worker.Difficulty };
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint) listener.LocalEndpoint);
        using var socket = await listener.AcceptSocketAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var dispatch = connection.DispatchAsync(socket, stop.Token,
            new StratumEndpoint((IPEndPoint) listener.LocalEndpoint, new PoolEndpoint()),
            (IPEndPoint) socket.RemoteEndPoint, null, (_, _, _) => Task.CompletedTask, _ => { }, (_, _) => { });
        using(var relay = new ShareRelay(config, sender.Bus, sender.Coordinator))
        {
            await relay.StartAsync(CancellationToken.None);
            try
            {
                var request = new JsonRpcRequest { Id = 1, Method = "mining.submit", Params = new JObject
                { ["job_id"] = job.Id, ["plain_proof"] = Convert.ToBase64String(proof) } };
                await (Task) typeof(PearlPool).GetMethod("SubmitAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(pool, new object[] { connection, request, CancellationToken.None });
                await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { stop.Cancel(); await dispatch; await relay.StopAsync(CancellationToken.None); }
        }
        Assert.NotNull(accepted);
        Assert.Equal("canonical-pearl-source", accepted.Source);
        var id = Guid.Parse(accepted.AccountingId);
        string PayloadHash(Share value) => ShareAccounting.ComputePayloadHash(id,
            new[] { ShareAccounting.ToPersistenceShare(value) }, new[] { ShareAccounting.CreatePpsCredit(pc, value) });
        var hash = await db.Admin.ExecuteScalarAsync<string>("SELECT payloadhash FROM share_accounting_groups WHERE accountingid=@id", new { id });
        Assert.Equal(PayloadHash(accepted), hash);
        Assert.Equal(1, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pearl_candidates"));

        for(var restart = 0; restart < 2; restart++)
        {
            // Reopen the durable candidate and recreate the publisher/receiver/recorder.
            store = new PearlCandidateStore(db.Factory, repository, pc);
            var retained = await store.NextAsync(pc.Id, CancellationToken.None);
            var replay = JsonConvert.DeserializeObject<Share>(retained.Share);
            await store.CommitAsync(replay, retained.Identity, job, proof, true, CancellationToken.None);
            config.ClusterName = "different-publisher-after-restart";
            var receiving = new ClusterConfig { Pools = new[] { pc }, PaymentProcessing = new() { Enabled = true, Paused = true },
                ShareRelays = new[] { new ShareRelayEndpointConfig { Url = config.ShareRelay.PublishUrl, SharedEncryptionKey = RelayTestKey } } };
            await using var recorder = new AccountingLab(db, receiving);
            await recorder.Start();
            using var receiver = new ShareReceiver(receiving, clock, recorder.Bus, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
            var received = new ConcurrentQueue<Share>();
            using var observed = recorder.Bus.Listen<Share>().Subscribe(received.Enqueue);
            var announced = Substitute.For<IMiningPool>();
            announced.Config.Returns(pc); announced.NetworkStats.Returns(new BlockchainStats()); announced.ShareMultiplier.Returns(1d);
            await receiver.StartAsync(CancellationToken.None);
            recorder.Bus.SendMessage(new PoolStatusNotification { Pool = announced, Status = PoolStatus.Online });
            await Until(() => Task.FromResult(receiver.AttachedPoolCount == 1), 5);
            using var relay = new ShareRelay(config, sender.Bus, sender.Coordinator);
            await relay.StartAsync(CancellationToken.None);
            try
            {
                // Repeated delivery also covers the PUB/SUB slow-joiner window.
                for(var attempt = 0; attempt < 50 && received.Count < 3; attempt++)
                {
                    sender.Bus.SendMessage(replay);
                    await Task.Delay(100);
                }
                Assert.True(received.Count >= 3);
                await Task.WhenAll(received.Select(x => x.PersistenceAdmission)).WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { await relay.StopAsync(CancellationToken.None); await receiver.StopAsync(CancellationToken.None); await recorder.Stop(); }
            Assert.All(received, value => { Assert.Equal("canonical-pearl-source", value.Source); Assert.Equal(hash, PayloadHash(value)); });
            Assert.False(recorder.Coordinator.IsFailStopRequested);
            Assert.False(File.Exists(receiving.ShareRecoveryFile));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(receiving.ShareRecoveryFile), "*quarantine*", SearchOption.AllDirectories));
            Assert.Equal(1, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM share_accounting_groups"));
            Assert.Equal(1, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits"));
            Assert.Equal(1, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares"));
            Assert.Equal(accepted.PpsCalculatedAmount.Value, await db.Admin.ExecuteScalarAsync<decimal>(
                "SELECT (SELECT sum(amount) FROM balances) + (SELECT sum(amount) FROM pps_credit_remainders)"));
        }
        Assert.False(sender.Coordinator.IsFailStopRequested);
        var childConfig = JsonConvert.DeserializeObject<ClusterConfig>(JsonConvert.SerializeObject(config));
        var childDirectory = Directory.CreateTempSubdirectory("pearl-relay-child-");
        childConfig.ShareRecoveryFile = Path.Combine(childDirectory.FullName, "shares.json");
        childConfig.ShareRecoveryStateDirectory = Path.Combine(childDirectory.FullName, "state");
        var processes = new HashSet<int>();
        try
        {
            for(var restart = 0; restart < 2; restart++)
            {
                await using var child = await TopologyProcess.Start(db, childConfig, mode: "pearl-relay-replay");
                var state = await child.State();
                Assert.True(processes.Add(state.Value<int>("processId")));
                Assert.True(state.Value<int>("deliveries") >= 3);
                Assert.Equal(hash, Assert.Single(state["hashes"].Values<string>()));
                Assert.Equal("canonical-pearl-source", Assert.Single(state["sources"].Values<string>()));
                Assert.False(state.Value<bool>("failStop"));
                Assert.False(state.Value<bool>("journal"));
                Assert.Equal(0, state.Value<int>("quarantines"));
                await child.Stop();
                Assert.Equal(1, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM share_accounting_groups"));
                Assert.Equal(1, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits"));
                Assert.Equal(1, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares"));
                Assert.Equal(accepted.PpsCalculatedAmount.Value, await db.Admin.ExecuteScalarAsync<decimal>(
                    "SELECT (SELECT sum(amount) FROM balances) + (SELECT sum(amount) FROM pps_credit_remainders)"));
            }
        }
        finally { childDirectory.Delete(true); }
        output.WriteLine("Native-verified Pearl PPS acceptance committed before acknowledgement; duplicate CURVE deliveries across two fresh OS processes retained one receipt and liability with identical hashes and no quarantine/fail-stop.");
    }
}

public sealed class PearlRelayIntegrationFactAttribute : FactAttribute
{
    public PearlRelayIntegrationFactAttribute()
    {
        if(!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES")) ||
           string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MININGCORE_TEST_PEARL_WORKER")))
            Skip = "Requires isolated PostgreSQL and the pinned Linux Pearl proof worker (MININGCORE_TEST_PEARL_WORKER)";
    }
}
