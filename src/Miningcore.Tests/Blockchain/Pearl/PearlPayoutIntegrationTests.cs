using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Blockchain.Pearl;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Tests.Persistence.Postgres;
using Miningcore.Tests.Util.Postgres;
using Miningcore.Time;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using NSubstitute;
using Xunit;
using Block = Miningcore.Persistence.Model.Block;

namespace Miningcore.Tests.Blockchain.Pearl;

// Real handler + HTTP RPC + real PostgreSQL ledger. Faults never replace payment persistence.
public class PearlPayoutIntegrationTests
{
    private const string Marker = "rprl1pg3h2up2s7a8reykv4nv087xs0r4ynswtw9nz69yxngaul6wjmk9sn7mfw9";
    internal sealed class Factory(string connection) : IConnectionFactory
    {
        public async Task<IDbConnection> OpenConnectionAsync()
        {
            var result = new NpgsqlConnection(connection);
            await result.OpenAsync();
            return result;
        }
    }

    internal sealed class Rpc : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Task loop;
        internal readonly int Port;
        internal string Genesis = PearlJobManager.Genesis(PearlNetwork.Regtest);
        internal string BestHash = new('a', 64);
        internal string CanonicalHash = new('a', 64);
        internal string BestHashAfterCanonicalLookup;
        internal ulong Height = 120;
        internal decimal Available = 1000000;
        internal JArray Unspent;
        internal bool Locked, LostResponse, Conflict, Partial, Offline, NodeMissing, NodeOffline, RejectSend, PartialBatch;
        internal int Sends, Relocks, Republishes;
        internal int CanonicalLookups;
        internal bool MissingBlockTransaction;
        internal JObject Evidence;
        internal string RawHeader;
        internal JArray SendArguments;
        internal readonly List<string> Calls = new();
        internal Rpc()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); Port = ((IPEndPoint) reservation.LocalEndpoint).Port; reservation.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/"); listener.Start(); loop = Serve();
        }
        internal DaemonEndpointConfig Endpoint(string category) => new() {Host = "127.0.0.1", Port = Port, Category = category};
        private async Task Serve()
        {
            try
            {
                while(listener.IsListening)
                {
                    var context = await listener.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream);
                    var request = JObject.Parse(await reader.ReadToEndAsync());
                    var method = request.Value<string>("method"); Calls.Add(method);
                    JToken result = null;
                    JObject error = null;
                    if(Offline) error = new JObject {["code"] = -28, ["message"] = "temporarily unavailable"};
                    else switch(method)
                    {
                        case "getblockhash":
                            var height = request["params"][0].Value<long>();
                            if(height != 0) CanonicalLookups++;
                            result = height == 0 ? Genesis : CanonicalHash;
                            if(height != 0 && BestHashAfterCanonicalLookup != null) BestHash = BestHashAfterCanonicalLookup;
                            break;
                        case "getbestblockhash": result = BestHash; break;
                        case "getblockheader": result = RawHeader; break;
                        case "getblockchaininfo": result = new JObject {["blocks"] = Height, ["bestblockhash"] = BestHash}; break;
                        case "validateaddress": result = new JObject {["ismine"] = true}; break;
                        case "chainsynced": result = true; break;
                        case "getbalance": Assert.Equal("default", request["params"][0].Value<string>()); result = Available; break;
                        case "listunspent": result = Unspent ?? new JArray(PearlWalletRequestTests.Coin(Available)); break;
                        case "getblock":
                            if(request["params"] is not JArray arguments || arguments.Count != 2 ||
                                arguments[1].Type != JTokenType.Integer || arguments[1].Value<int>() != 1)
                            { error = new JObject {["code"] = -32602}; break; }
                            result = new JObject {["hash"] = CanonicalHash, ["height"] = 10, ["confirmations"] = 110,
                                ["tx"] = MissingBlockTransaction ? new JArray() : new JArray(Evidence["txid"])}; break;
                        case "walletislocked": result = Locked; break;
                        case "walletpassphrase": Locked = false; break;
                        case "walletlock": Relocks++; Locked = true; break;
                        case "getnewaddress": result = Marker; break;
                        case "listaddresstransactions": result = Evidence == null ? new JArray() : new JArray(new JObject {["txid"] = Evidence["txid"]}); break;
                        case "sendmany":
                            Sends++; SendArguments = (JArray) request["params"].DeepClone();
                            if(RejectSend) {error = new JObject {["code"] = -6, ["message"] = "insufficient fee funds"}; break;}
                            var tx = Transaction.Create(Network.Main);
                            tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
                            foreach(var pair in ((JObject) SendArguments[1]).Properties())
                                tx.Outputs.Add(new TxOut(Money.Satoshis((long) (pair.Value.Value<decimal>() * 100000000)), PearlAddress.RewardScript(pair.Name, "rprl")));
                            if(PartialBatch) tx.Outputs.RemoveAt(0);
                            Evidence = new JObject {["txid"] = tx.GetHash().ToString(), ["hex"] = tx.ToHex(), ["confirmations"] = 0, ["walletconflicts"] = new JArray(), ["fee"] = -0.00001m};
                            result = Evidence["txid"];
                            if(LostResponse) {context.Response.Abort(); continue;}
                            break;
                        case "gettransaction":
                            result = Evidence.DeepClone();
                            if(Conflict) result["walletconflicts"] = new JArray(new string('b',64));
                            if(Partial)
                            {
                                var incomplete = Transaction.Parse(Evidence.Value<string>("hex"), Network.Main);
                                incomplete.Outputs.RemoveAt(0);
                                result["hex"] = incomplete.ToHex(); // independently computed txid cannot match durable identity
                            }
                            break;
                        case "getrawtransaction":
                            if(NodeOffline) error = new JObject {["code"] = -28, ["message"] = "node reconnecting"};
                            else if(NodeMissing) error = new JObject {["code"] = -5, ["message"] = "not found"};
                            else result = Evidence.DeepClone();
                            break;
                        case "sendrawtransaction":
                            Assert.Equal(Evidence.Value<string>("hex"), request["params"][0].Value<string>());
                            Republishes++; NodeMissing = false; result = Evidence["txid"]; break;
                        default: throw new InvalidOperationException("Unexpected RPC: " + method);
                    }
                    var bytes = Encoding.UTF8.GetBytes(new JObject {["id"] = request["id"], ["result"] = result, ["error"] = error}.ToString(Formatting.None));
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                }
            }
            catch(Exception ex) when(ex is HttpListenerException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync() {listener.Close(); await loop;}
    }

    internal sealed class Lab : IAsyncDisposable
    {
        internal NpgsqlConnection Db;
        internal Factory Connections;
        internal readonly Rpc Server = new();
        internal PoolConfig Config;
        private string schema;
        internal static async Task<Lab> Create(bool defaults = false)
        {
            var lab = new Lab();
            var connection = Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES");
            lab.schema = "pearl_payout_" + Guid.NewGuid().ToString("N");
            lab.Db = new NpgsqlConnection(connection); await lab.Db.OpenAsync();
            await lab.Db.ExecuteAsync($"CREATE SCHEMA {lab.schema}; SET search_path TO {lab.schema}, public");
            await lab.Db.ExecuteAsync((await File.ReadAllTextAsync(PostgresTestScripts.PathFor("createdb.sql"))).Replace("SET ROLE miningcore;", ""));
            lab.Connections = new Factory(new NpgsqlConnectionStringBuilder(connection) {SearchPath = lab.schema + ",public"}.ConnectionString);
            lab.Config = new PoolConfig {Id = "pearl", Address = PearlWorkTests.Address,
                Template = new PearlCoinTemplate {Name = "Pearl", Symbol = "PRL", Family = CoinFamily.Pearl},
                Extra = new Dictionary<string,object> {{"network", "regtest"}},
                RewardRecipients = Array.Empty<RewardRecipient>(),
                Daemons = new[] {lab.Server.Endpoint("node"), lab.Server.Endpoint("wallet")},
                PaymentProcessing = new PoolPaymentProcessingConfig {Enabled = true, MinimumPayment = .01m,
                    Extra = defaults ? null : new Dictionary<string,object> {{"feeRateGrainsPerKb", 20000}, {"minimumConfirmations", 120}, {"walletPassword", "test-only"}}}};
            return lab;
        }
        internal async Task<PearlPayoutHandler> Handler()
        {
            var mapper = AutoMapperFactory.CreateMapper();
            var handler = new PearlPayoutHandler(Connections, mapper, new ShareRepository(mapper), new BlockRepository(mapper),
                new BalanceRepository(mapper), new PaymentRepository(mapper), new StandardClock(), Substitute.For<IMessageBus>(), new JsonSerializerSettings());
            await handler.ConfigureAsync(new ClusterConfig(), Config, CancellationToken.None);
            return handler;
        }
        internal async Task<Balance[]> Seed(int count = 1, decimal amount = 1.234567899m)
        {
            var mapper = AutoMapperFactory.CreateMapper(); var repository = new BalanceRepository(mapper);
            var result = Enumerable.Range(1,count).Select(i => new Balance {PoolId = "pearl", Amount = amount,
                Address = Encoders.Bech32("rprl").Encode(1, System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i)))}).ToArray();
            foreach(var balance in result)
                await Connections.RunTx((con, tx) => repository.AddAmountAsync(con, tx, balance.PoolId, balance.Address, balance.Amount, "test seed"));
            return result;
        }
        internal Task<int> Count(string table) => Db.ExecuteScalarAsync<int>("SELECT count(*) FROM " + table);
        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Db.ExecuteAsync("ROLLBACK; RESET ROLE; SET search_path TO public");
            await Db.ExecuteAsync($"DROP SCHEMA {schema} CASCADE"); await Db.DisposeAsync();
        }
    }

    [PostgresIntegrationFact]
    public async Task ClassificationAndPaymentPreferTheWalletChainOverTheFirstHealthyNode()
    {
        await using var lab = await Lab.Create(true);
        await using var divergent = new Rpc {BestHash = new string('d',64), CanonicalHash = new string('d',64)};
        lab.Config.Daemons = new[] {divergent.Endpoint("node"), lab.Server.Endpoint("node"), lab.Server.Endpoint("wallet")};
        var handler = await lab.Handler();
        var balances = await lab.Seed();
        var confirmed = new Block {PoolId = "pearl", BlockHeight = 10, Hash = lab.Server.CanonicalHash, Status = BlockStatus.Confirmed};
        Assert.Empty(await handler.ClassifyBlocksAsync(null, new[] {confirmed}, CancellationToken.None));
        Assert.Equal(BlockStatus.Confirmed, confirmed.Status);
        Assert.Equal(0, divergent.CanonicalLookups);
        Assert.Equal(1, lab.Server.CanonicalLookups);
        await handler.PayoutAsync(null, balances, CancellationToken.None);
        Assert.Equal(1, lab.Server.Sends);
        Assert.Equal(1, await lab.Count("payments"));
    }

    [PostgresIntegrationFact]
    public async Task ExistingIntentCanReconcileWhenNoNodeMatchesTheWalletTip()
    {
        await using var lab = await Lab.Create(true);
        var balances = await lab.Seed();
        var handler = await lab.Handler();
        lab.Server.LostResponse = true;
        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => handler.PayoutAsync(null, balances, CancellationToken.None));
        await using var advanced = new Rpc {BestHash = new string('b',64), Evidence = lab.Server.Evidence};
        lab.Config.Daemons = new[] {advanced.Endpoint("node"), lab.Server.Endpoint("wallet")};
        handler = await lab.Handler();
        await handler.PayoutAsync(null, balances, CancellationToken.None);
        Assert.Equal(1, lab.Server.Sends);
        Assert.Equal(0, advanced.Sends);
        Assert.Equal(1, await lab.Count("payments"));
        Assert.Equal(0, await lab.Count("pearl_payout_intents"));
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.PayoutAsync(null, balances, CancellationToken.None));
        Assert.Equal(1, lab.Server.Sends);
    }

    [PostgresIntegrationFact]
    public async Task FragmentedInventoryLargerThanTheOrdinaryRpcLimitStillPaysFromLargeInputs()
    {
        await using var lab = await Lab.Create(true);
        lab.Server.Unspent = new JArray(Enumerable.Range(0, 10000).Select(i => PearlWalletRequestTests.Coin(.01m, i)));
        lab.Server.Unspent.Add(PearlWalletRequestTests.Coin(10m, 10000));
        Assert.True(Encoding.UTF8.GetByteCount(lab.Server.Unspent.ToString(Formatting.None)) > 2000000);
        var handler = await lab.Handler();
        await handler.PayoutAsync(null, await lab.Seed(), CancellationToken.None);
        Assert.Equal(1, lab.Server.Sends);
        Assert.Equal(1, await lab.Count("payments"));
        Assert.Equal(0, await lab.Count("pearl_payout_intents"));
    }

    [PostgresIntegrationFact]
    public async Task LaggingNodeDefersRewardsAndConfirmedBlockRemainsPayableAfterCatchup()
    {
        await using var lab = await Lab.Create(true);
        var handler = await lab.Handler();
        var balances = await lab.Seed();
        var repository = new BlockRepository(AutoMapperFactory.CreateMapper());
        var confirmed = new Block {PoolId = lab.Config.Id, BlockHeight = 121, Hash = new string('a',64),
            Status = BlockStatus.Confirmed, ConfirmationProgress = 1, Created = DateTime.UtcNow,
            TransactionConfirmationData = new string('b',64), Reward = 1};
        await lab.Connections.RunTx((con, tx) => repository.InsertAsync(con, tx, confirmed));
        var pending = new Block {PoolId = lab.Config.Id, BlockHeight = 121, Hash = confirmed.Hash,
            Status = BlockStatus.Pending, ConfirmationProgress = .5};
        lab.Server.Calls.Clear();
        Assert.Empty(await handler.ClassifyBlocksAsync(null, new[] {pending, confirmed}, CancellationToken.None));
        Assert.Equal(0, lab.Server.CanonicalLookups);
        Assert.Equal(BlockStatus.Pending, pending.Status);
        Assert.Equal(.5, pending.ConfirmationProgress);
        Assert.Equal(BlockStatus.Confirmed, confirmed.Status);
        Assert.Equal(1, confirmed.ConfirmationProgress);
        Assert.False(pending.NotifyBlockUnlockedOnUpdate);
        Assert.False(confirmed.NotifyBlockUnlockedOnUpdate);

        // Matching wallet/node tips and other spendable funds cannot back an above-tip reward.
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.PayoutAsync(null, balances, CancellationToken.None));
        Assert.Equal(0, lab.Server.Sends);
        Assert.Equal(0, await lab.Count("pearl_payout_intents"));
        Assert.Equal(0, await lab.Count("payments"));
        Assert.Equal(1.234567899m, await lab.Db.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal("confirmed", await lab.Db.ExecuteScalarAsync<string>("SELECT status FROM blocks"));

        // The same stored block is still eligible when the node reaches its height.
        lab.Server.Height = confirmed.BlockHeight;
        Assert.Equal(1, await lab.Count("pearl_payout_holds"));
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.PayoutAsync(null, balances, CancellationToken.None));
        Assert.Equal(0, lab.Server.Sends);
        var retry = Assert.Single(await handler.ReconciliationBlocksAsync(lab.Server.Height, 0, CancellationToken.None));
        lab.Server.Calls.Clear();
        Assert.Empty(await handler.ClassifyBlocksAsync(null, new[] {retry}, CancellationToken.None));
        Assert.Contains("getblockhash", lab.Server.Calls);
        Assert.Equal(BlockStatus.Confirmed, retry.Status);
        Assert.Equal("confirmed", await lab.Db.ExecuteScalarAsync<string>("SELECT status FROM blocks"));
        Assert.Equal(0, await lab.Count("pearl_payout_holds"));
        await handler.PayoutAsync(null, balances, CancellationToken.None);
        Assert.Equal(1, lab.Server.Sends);
        Assert.Equal(1, await lab.Count("payments"));
    }

    [PostgresIntegrationFact]
    public async Task CompetingCatchupBetweenClassificationAndPayoutCannotReleaseHold()
    {
        await using var lab = await Lab.Create(true);
        var handler = await lab.Handler();
        var recipients = await lab.Seed();
        await lab.Db.ExecuteAsync("""
            INSERT INTO blocks(poolid,blockheight,networkdifficulty,status,transactionconfirmationdata,hash,created)
            VALUES('pearl',121,1,'confirmed',@hash,@hash,now())
            """, new {hash = new string('a',64)});
        var block = Assert.Single(await handler.ReconciliationBlocksAsync(120, 0, CancellationToken.None));
        Assert.Empty(await handler.ClassifyBlocksAsync(null, new[] {block}, CancellationToken.None));
        lab.Server.Height = 121;
        lab.Server.CanonicalHash = lab.Server.BestHash = new string('b',64);
        // Catch-up between classification and payout must not authorize sendmany.
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
        Assert.Equal(0, lab.Server.Sends);
        Assert.Equal(0, await lab.Count("pearl_payout_intents"));
        Assert.Equal(1.234567899m, await lab.Db.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal(1, await lab.Count("pearl_payout_holds"));

        handler = await lab.Handler();
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
        // Held rewards override both cursor and ordinary reconciliation age limits.
        var retry = Assert.Single(await handler.ReconciliationBlocksAsync(50000, long.MaxValue, CancellationToken.None));
        var classified = Assert.Single(await handler.ClassifyBlocksAsync(null, new[] {retry}, CancellationToken.None));
        Assert.Equal(BlockStatus.Quarantined, classified.Status);
        var repository = new BlockRepository(AutoMapperFactory.CreateMapper());
        await lab.Connections.RunTx((con, tx) => repository.UpdateBlockAsync(con, tx, classified));
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
        Assert.Equal(0, lab.Server.Sends);
        Assert.Equal(0, await lab.Count("payments"));
        Assert.Equal(1, await lab.Count("pearl_payout_holds"));
    }

    [PostgresIntegrationFact]
    public async Task DeferredHoldsCoverUnvisitedBatchesAndRequireStableCanonicalEvidence()
    {
        await using var lab = await Lab.Create(true);
        var handler = await lab.Handler();
        var recipients = await lab.Seed();
        // Exercise upgrades as well as the fresh schema, including idempotent replay.
        await lab.Db.ExecuteAsync("DROP TABLE pearl_payout_holds");
        var migration = await File.ReadAllTextAsync(PostgresTestScripts.PathFor("add_pearl_candidates.sql"));
        await lab.Db.ExecuteAsync(migration);
        await lab.Db.ExecuteAsync(migration);
        await lab.Db.ExecuteAsync("""
            INSERT INTO blocks(poolid,blockheight,networkdifficulty,status,transactionconfirmationdata,hash,created)
            SELECT 'pearl',121,1,'confirmed',@hash,@hash,now() FROM generate_series(1,65)
            """, new {hash = new string('a',64)});
        Assert.Empty(await handler.ClassifyBlocksAsync(null, Array.Empty<Block>(), CancellationToken.None));
        Assert.Equal(65, await lab.Count("pearl_payout_holds"));
        lab.Server.Height = 5000;
        var batch = await handler.ReconciliationBlocksAsync(5000, long.MaxValue, CancellationToken.None);
        Assert.Equal(64, batch.Length);
        lab.Server.CanonicalHash = null;
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.ClassifyBlocksAsync(null, batch, CancellationToken.None));
        Assert.Equal(65, await lab.Count("pearl_payout_holds"));
        lab.Server.CanonicalHash = new string('a',64);
        lab.Server.BestHashAfterCanonicalLookup = new string('b',64);
        Assert.Empty(await handler.ClassifyBlocksAsync(null, batch, CancellationToken.None));
        Assert.Equal(65, await lab.Count("pearl_payout_holds"));
        lab.Server.BestHashAfterCanonicalLookup = null;
        Assert.Empty(await handler.ClassifyBlocksAsync(null, batch, CancellationToken.None));
        Assert.Equal(1, await lab.Count("pearl_payout_holds"));
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
        var remaining = Assert.Single(await handler.ReconciliationBlocksAsync(5000, long.MaxValue, CancellationToken.None));
        Assert.Empty(await handler.ClassifyBlocksAsync(null, new[] {remaining}, CancellationToken.None));
        Assert.Equal(0, await lab.Count("pearl_payout_holds"));
        // Even after verification, a changed tip requires another classification cycle.
        lab.Server.BestHash = new string('c',64);
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
        Assert.Equal(0, lab.Server.Sends);
        Assert.Empty(await handler.ClassifyBlocksAsync(null, new[] {remaining}, CancellationToken.None));
        await handler.PayoutAsync(null, recipients, CancellationToken.None);
        Assert.Equal(1, lab.Server.Sends);
        Assert.Equal(1, await lab.Count("payments"));
    }

    [PostgresIntegrationFact]
    public async Task AboveTipConfirmedRewardDoesNotBlockExistingIntentReconciliation()
    {
        await using var lab = await Lab.Create(true);
        var handler = await lab.Handler();
        var recipients = await lab.Seed();
        var repository = new BlockRepository(AutoMapperFactory.CreateMapper());
        var confirmed = new Block {PoolId = lab.Config.Id, BlockHeight = 120, Hash = new string('a',64),
            Status = BlockStatus.Confirmed, ConfirmationProgress = 1, Created = DateTime.UtcNow,
            TransactionConfirmationData = new string('b',64), Reward = 1};
        await lab.Connections.RunTx((con, tx) => repository.InsertAsync(con, tx, confirmed));
        lab.Server.LostResponse = true;
        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
        Assert.Equal(1, lab.Server.Sends);
        Assert.Equal(1, await lab.Count("pearl_payout_intents"));
        Assert.Equal(0, await lab.Count("payments"));

        // The transaction was already submitted; a restart on a shorter chain must
        // reconcile that exact transaction without authorizing a second send.
        lab.Server.Height = 119;
        Assert.Empty(await handler.ClassifyBlocksAsync(null, Array.Empty<Block>(), CancellationToken.None));
        Assert.Equal(1, await lab.Count("pearl_payout_holds"));
        await lab.Db.ExecuteAsync("UPDATE blocks SET status='quarantined'");
        handler = await lab.Handler();
        lab.Server.Calls.Clear();
        await handler.PayoutAsync(null, recipients, CancellationToken.None);
        Assert.DoesNotContain("getnewaddress", lab.Server.Calls);
        Assert.Equal(1, lab.Server.Sends);
        Assert.Equal(0, await lab.Count("pearl_payout_intents"));
        Assert.Equal(1, await lab.Count("payments"));
        Assert.Equal(1, await lab.Count("payment_batches"));
        Assert.Equal(.000000009m, await lab.Db.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        // With no intent left, new submissions are still held until catch-up.
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
        Assert.Equal(1, lab.Server.Sends);
        Assert.Equal(1, await lab.Count("payments"));
        Assert.Equal("quarantined", await lab.Db.ExecuteScalarAsync<string>("SELECT status FROM blocks"));
        Assert.Equal(1, await lab.Count("pearl_payout_holds"));
    }

    [PostgresIntegrationFact]
    public async Task MissingOrMalformedCanonicalHashCannotOrphanOrQuarantineRewards()
    {
        await using var lab = await Lab.Create(true);
        var handler = await lab.Handler();
        var pending = new Block {PoolId = lab.Config.Id, BlockHeight = 10, Hash = new string('a',64), Status = BlockStatus.Pending};
        var confirmed = new Block {PoolId = lab.Config.Id, BlockHeight = 11, Hash = new string('a',64), Status = BlockStatus.Confirmed};
        foreach(var invalid in new[] {null, "", "unavailable", new string('g',64)})
        {
            lab.Server.CanonicalHash = invalid;
            foreach(var block in new[] {pending, confirmed})
                await Assert.ThrowsAsync<InvalidDataException>(() => handler.ClassifyBlocksAsync(null, new[] {block}, CancellationToken.None));
            Assert.Equal(BlockStatus.Pending, pending.Status);
            Assert.Equal(BlockStatus.Confirmed, confirmed.Status);
        }
        // Valid competing-chain evidence still triggers the intended protection.
        lab.Server.CanonicalHash = new string('b',64);
        var classified = await handler.ClassifyBlocksAsync(null, new[] {pending, confirmed}, CancellationToken.None);
        Assert.Equal(2, classified.Length);
        Assert.Equal(BlockStatus.Orphaned, pending.Status);
        Assert.Equal(BlockStatus.Quarantined, confirmed.Status);
        var orphan = new Block {PoolId = lab.Config.Id, BlockHeight = 10, Hash = new string('a',64), Status = BlockStatus.Orphaned};
        Assert.Empty(await handler.ClassifyBlocksAsync(null, new[] {orphan}, CancellationToken.None));
        Assert.False(orphan.NotifyBlockUnlockedOnUpdate);
    }

    [PostgresIntegrationFact]
    public async Task MatchingInvalidTipsCannotAuthorizeWalletSubmission()
    {
        await using var lab = await Lab.Create(true);
        var handler = await lab.Handler(); var balances = await lab.Seed();
        foreach(var invalid in new[] {null, "", "unavailable", new string('g',64)})
        {
            // Both endpoints returning the same invalid value must not count as sync.
            lab.Server.BestHash = invalid;
            await Assert.ThrowsAsync<InvalidDataException>(() => handler.PayoutAsync(null, balances, CancellationToken.None));
            Assert.Equal(0, lab.Server.Sends);
            Assert.Equal(0, await lab.Count("pearl_payout_intents"));
            Assert.Equal(0, await lab.Count("payments"));
            Assert.Equal(1.234567899m, await lab.Db.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        }
        lab.Server.BestHash = new string('a',64);
        await handler.PayoutAsync(null, balances, CancellationToken.None);
        Assert.Equal(1, lab.Server.Sends);
        Assert.Equal(1, await lab.Count("payments"));
    }

    [PostgresIntegrationFact]
    public async Task AuditedNoBroadcastReleaseArchivesIntentAndPreservesBalances()
    {
        await using var lab = await Lab.Create(true);
        var handler = await lab.Handler(); var recipients = await lab.Seed();
        lab.Server.RejectSend = true;
        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
        var script = await File.ReadAllTextAsync(PostgresTestScripts.OperatorPathFor("release_pearl_payout_intent.sql"));
        var body = script[script.IndexOf("DO $$", StringComparison.Ordinal)..script.LastIndexOf("COMMIT;", StringComparison.Ordinal)];
        Task Release(string ack) => lab.Connections.RunTx(async (con, tx) =>
        {
            await con.ExecuteAsync("""
                SELECT set_config('miningcore.release_pool','pearl',true),
                set_config('miningcore.release_marker',@marker,true),
                set_config('miningcore.release_audit',@hash,true),
                set_config('miningcore.release_reason','Test audit of conclusive pre-broadcast failure',true),
                set_config('miningcore.release_ack',@ack,true)
                """, new {marker = Marker, hash = new string('a',64), ack}, tx);
            await con.ExecuteAsync(body, transaction: tx);
        });
        await Assert.ThrowsAsync<PostgresException>(() => Release("not-confirmed"));
        Assert.Equal(1, await lab.Count("pearl_payout_intents"));
        await lab.Db.ExecuteAsync("UPDATE pearl_payout_intents SET transactionid=@hash", new {hash = new string('b',64)});
        await Assert.ThrowsAsync<PostgresException>(() => Release("NO_TRANSACTION_CREATED"));
        Assert.Equal(0, await lab.Count("pearl_payout_intent_releases"));
        await lab.Db.ExecuteAsync("UPDATE pearl_payout_intents SET transactionid=NULL");
        await Release("NO_TRANSACTION_CREATED");
        Assert.Equal(0, await lab.Count("pearl_payout_intents"));
        Assert.Equal(1, await lab.Count("pearl_payout_intent_releases"));
        Assert.Equal(0, await lab.Count("payments"));
        Assert.Equal(1.234567899m, await lab.Db.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        lab.Server.RejectSend = false;
        lab.Server.Locked = false;
        handler = await lab.Handler();
        await handler.PayoutAsync(null, recipients, CancellationToken.None);
        Assert.Equal(1, await lab.Count("payments"));
    }

    [PostgresIntegrationFact]
    public async Task ConfirmedIntentWithoutTransactionIndexRequiresCanonicalBlockMembership()
    {
        await using var lab = await Lab.Create(true);
        var handler = await lab.Handler(); var recipients = await lab.Seed();
        lab.Server.LostResponse = true;
        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
        lab.Server.Evidence["confirmations"] = 110;
        lab.Server.Evidence["blockhash"] = lab.Server.CanonicalHash;
        lab.Server.NodeMissing = true;
        handler = await lab.Handler();
        lab.Server.MissingBlockTransaction = true;
        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
        Assert.Equal(0, await lab.Count("payments"));
        lab.Server.MissingBlockTransaction = false;
        await handler.PayoutAsync(null, recipients, CancellationToken.None);
        Assert.Equal(1, lab.Server.Sends);
        Assert.Equal(0, lab.Server.Republishes);
        Assert.Equal(1, await lab.Count("payments"));
        Assert.Equal(0, await lab.Count("pearl_payout_intents"));
    }

    [PostgresIntegrationFact]
    public async Task FeeShortfallDoesNotCreateIntentAndFundingCanResumeAfterRestart()
    {
        await using var lab = await Lab.Create(true);
        var handler = await lab.Handler(); var recipients = await lab.Seed(1, 1m);
        lab.Server.Available = 1.011m;
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
        Assert.Equal(0, lab.Server.Sends);
        Assert.Equal(0, await lab.Count("pearl_payout_intents"));
        Assert.Equal(1m, await lab.Db.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        lab.Server.Available = 2m;
        handler = await lab.Handler();
        await handler.PayoutAsync(null, recipients, CancellationToken.None);
        Assert.Equal(1, lab.Server.Sends);
        Assert.Equal(1, await lab.Count("payments"));
    }

    [PostgresIntegrationFact]
    public async Task PayoutFailsOverOnlyToANetworkAndTipValidatedNode()
    {
        await using var lab = await Lab.Create(true);
        await using var wrong = new Rpc {Genesis = new string('b',64)};
        await using var offline = new Rpc {Offline = true};
        lab.Config.Daemons = new[] {offline.Endpoint("node"), wrong.Endpoint("node"), lab.Server.Endpoint("node"), lab.Server.Endpoint("wallet")};
        var handler = await lab.Handler(); var recipients = await lab.Seed();
        await handler.PayoutAsync(null, recipients, CancellationToken.None);
        Assert.Equal(1, lab.Server.Sends);
        Assert.Equal(0, wrong.Sends);
        Assert.DoesNotContain("getrawtransaction", wrong.Calls);
        Assert.Equal(1, await lab.Count("payments"));
    }

    [PostgresIntegrationFact]
    public async Task AbsentExtensionsUseDefaultsAndExplicitOverridesArePreserved()
    {
        foreach(var defaults in new[] {true, false})
        {
            await using var lab = await Lab.Create(defaults);
            var handler = await lab.Handler(); var balances = await lab.Seed();
            await handler.PayoutAsync(null, balances, CancellationToken.None);
            Assert.Equal(defaults ? .0001m : .0002m, lab.Server.SendArguments[2].Value<decimal>());
            Assert.Equal("default", lab.Server.SendArguments[0].Value<string>());
            Assert.Equal(1, lab.Server.SendArguments[3].Value<int>());
            Assert.Equal(1.23456789m, lab.Server.SendArguments[1][balances[0].Address].Value<decimal>());
            Assert.Equal(.000000009m, await lab.Db.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
            Assert.Equal(1, await lab.Count("payments")); Assert.Equal(0, await lab.Count("pearl_payout_intents"));
            Assert.Equal(1, lab.Server.Relocks);
        }
    }

    [PostgresIntegrationFact]
    public async Task MissingPoolExtensionsSelectMainnetAndInvalidExplicitSettingsFailClosed()
    {
        await using var lab = await Lab.Create(true);
        lab.Config.Extra = null;
        lab.Server.Genesis = PearlJobManager.Genesis(PearlNetwork.Mainnet);
        await lab.Handler();
        foreach(var bad in new object[] {0, 1, 999, -1, 100000001})
        {
            lab.Config.PaymentProcessing.Extra = new Dictionary<string,object> {{"feeRateGrainsPerKb", bad}};
            await Assert.ThrowsAsync<InvalidDataException>(() => lab.Handler());
        }
        lab.Config.PaymentProcessing.Extra = new Dictionary<string,object> {{"minimumConfirmations", 100}};
        await Assert.ThrowsAsync<InvalidDataException>(() => lab.Handler());
    }

    [PostgresIntegrationFact]
    public async Task PreflightFailuresDoNotCreateIntentOrDebitAndBatchLimitPreservesRemainder()
    {
        foreach(var mode in new[] {"funds", "locked", "minimum"})
        {
            await using var lab = await Lab.Create(true);
            var handler = await lab.Handler(); var balances = await lab.Seed(amount: .05m);
            if(mode == "funds") lab.Server.Available = .06m;
            if(mode == "locked") lab.Server.Locked = true;
            if(mode == "minimum") {lab.Config.PaymentProcessing.MinimumPayment = .1m; await handler.PayoutAsync(null, balances, CancellationToken.None);}
            else await Assert.ThrowsAsync<InvalidDataException>(() => handler.PayoutAsync(null, balances, CancellationToken.None));
            Assert.Equal(0, lab.Server.Sends); Assert.Equal(0, await lab.Count("pearl_payout_intents"));
            Assert.Equal(.05m, await lab.Db.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        }
        await using var batch = await Lab.Create();
        var h = await batch.Handler(); var recipients = await batch.Seed(101, 1);
        await h.PayoutAsync(null, recipients, CancellationToken.None);
        Assert.Equal(100, await batch.Count("payments"));
        Assert.Equal(101, ((JObject) batch.Server.SendArguments[1]).Count); // recipients + owned marker
        Assert.Equal(1m, await batch.Db.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
    }

    [PostgresIntegrationFact]
    public async Task FailedOrPartialBatchRetainsIntentWithoutPayingAgain()
    {
        foreach(var partial in new[] {false, true})
        {
            await using var lab = await Lab.Create();
            var handler = await lab.Handler(); var recipients = await lab.Seed(2, 1);
            lab.Server.RejectSend = !partial; lab.Server.PartialBatch = partial;
            await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
            handler = await lab.Handler();
            await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
            Assert.Equal(1, lab.Server.Sends);
            Assert.Equal(1, await lab.Count("pearl_payout_intents")); Assert.Equal(0, await lab.Count("payments"));
            Assert.Equal(2m, await lab.Db.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
        }
    }

    [PostgresIntegrationFact]
    public async Task LostReplyReconnectConflictPartialEvidenceAndPendingRebroadcastNeverSendTwice()
    {
        await using var lab = await Lab.Create();
        var handler = await lab.Handler(); var recipients = await lab.Seed();
        lab.Server.LostResponse = true;
        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
        Assert.Equal(1, lab.Server.Sends); Assert.Equal(1, await lab.Count("pearl_payout_intents"));
        Assert.Equal(0, await lab.Count("payments"));
        // Recreate the production handler as after process loss; durable recipients survive.
        handler = await lab.Handler();
        Assert.Single(await handler.PendingRecipientsAsync(CancellationToken.None));
        foreach(var fault in new[] {"offline", "node-offline", "conflict", "partial"})
        {
            lab.Server.Offline = fault == "offline"; lab.Server.NodeOffline = fault == "node-offline"; lab.Server.Conflict = fault == "conflict"; lab.Server.Partial = fault == "partial";
            await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => handler.PayoutAsync(null, recipients, CancellationToken.None));
            Assert.Equal(1, lab.Server.Sends); Assert.Equal(1, await lab.Count("pearl_payout_intents")); Assert.Equal(0, await lab.Count("payments"));
        }
        lab.Server.Partial = false; lab.Server.NodeMissing = true;
        await handler.PayoutAsync(null, recipients, CancellationToken.None);
        Assert.Equal(1, lab.Server.Republishes); Assert.Equal(1, lab.Server.Sends);
        Assert.Equal(1, await lab.Count("payments")); Assert.Equal(1, await lab.Count("payment_batches"));
        Assert.Equal(0, await lab.Count("pearl_payout_intents"));
        // An empty subsequent manager selection cannot pay the old recipients again.
        await handler.PayoutAsync(null, Array.Empty<Balance>(), CancellationToken.None);
        Assert.Equal(1, lab.Server.Sends);
    }
}
