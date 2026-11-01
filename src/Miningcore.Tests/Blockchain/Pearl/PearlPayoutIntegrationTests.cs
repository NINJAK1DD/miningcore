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
        internal decimal Available = 1000000;
        internal bool Locked, LostResponse, Conflict, Partial, Offline, NodeMissing, NodeOffline, RejectSend, PartialBatch;
        internal int Sends, Relocks, Republishes;
        internal JObject Evidence;
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
                        case "getblockhash": result = Genesis; break;
                        case "getbestblockhash": result = new string('a',64); break;
                        case "validateaddress": result = new JObject {["ismine"] = true}; break;
                        case "chainsynced": result = true; break;
                        case "getbalance": Assert.Equal("default", request["params"][0].Value<string>()); result = Available; break;
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
        foreach(var bad in new object[] {0, -1, 100000001})
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
