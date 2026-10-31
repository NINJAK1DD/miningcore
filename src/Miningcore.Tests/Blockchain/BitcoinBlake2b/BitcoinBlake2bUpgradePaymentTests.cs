using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Dapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Rpc;
using Miningcore.Tests.Blockchain.Bitcoin;
using Miningcore.Time;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;
using Node = Miningcore.Tests.Blockchain.Bitcoin.BitcoinPayoutHandlerRegtestTests.BitcoinCoreRegtestNode;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

[Collection(BitcoinCorePayoutIntegrationCollection.Name)]
public class BitcoinBlake2bUpgradePaymentTests : TestBase
{
    private sealed class CountingHandler(IComponentContext context, IConnectionFactory factory,
        IMessageBus messages, Action submitted) : BitcoinBlake2bPayoutHandler(context, factory,
        AutoMapperFactory.CreateMapper(), new ShareRepository(AutoMapperFactory.CreateMapper()),
        new BlockRepository(AutoMapperFactory.CreateMapper()), new BalanceRepository(AutoMapperFactory.CreateMapper()),
        new PaymentRepository(AutoMapperFactory.CreateMapper()), new StandardClock(), messages,
        new ActiveBlockGracePeriodTracker(), new BitcoinBlake2bPayoutContractTracker())
    {
        protected override async Task<RpcResponse<string>> SendManyAsync(object[] args, CancellationToken ct)
        {
            submitted();
            return await base.SendManyAsync(args, ct);
        }
    }

    [BitcoinBlake2bLedgerIntegrationFact]
    public async Task SuccessfulAndUncertainPayments_SurviveMempoolLossRestartAndRevalidationWithoutDuplicate()
    {
        foreach(var broadcast in new[] { true, false })
        {
            await using var db = await BitcoinBlake2bLedgerProbe.CreateAsync(fullSchema: true);
            await using var node = await Node.StartAsync(broadcast,
                Environment.GetEnvironmentVariable(BitcoinBlake2bIntegrationFactAttribute.BinaryEnvironmentVariable),
                new[] { "-testactivationheight=blake2b@20", "-blake2b_headline=Miningcore BLAKE2b regtest", "-persistmempool=0" });
            var address = await node.GetNewAddressAsync();
            var config = new PoolConfig
            {
                Id = "upgrade-payment", Coin = "bitcoin-blake2b", Template = ModuleInitializer.CoinTemplates["bitcoin-blake2b"],
                Address = address, Daemons = new[] { node.WalletEndpoint }, RewardRecipients = Array.Empty<RewardRecipient>(),
                PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true },
                Extra = new Dictionary<string, object> { ["minimumConfirmations"] = 1 },
            };
            await db.Observer.ExecuteAsync("INSERT INTO balances(poolid,address,amount,created,updated) VALUES(@pool,@address,1,now(),now())",
                new { pool = config.Id, address });
            var pool = Substitute.For<IMiningPool>(); pool.Config.Returns(config);
            var sends = 0;
            var messages = Substitute.For<IMessageBus>();
            var balances = new BalanceRepository(AutoMapperFactory.CreateMapper());
            async Task PayCurrentBalances()
            {
                var handler = new CountingHandler(container, db.Factory, messages, () => sends++);
                await handler.ConfigureAsync(new ClusterConfig(), config, CancellationToken.None);
                var payable = await balances.GetPoolBalancesOverThresholdAsync(db.Observer, config.Id, 0.01m);
                await handler.PayoutAsync(pool, payable, CancellationToken.None);
            }
            if(broadcast) await PayCurrentBalances();
            else
            {
                var uncertain = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(PayCurrentBalances);
                Assert.NotEmpty(uncertain.Reconciliation.Uncertain);
                Assert.DoesNotContain(messages.ReceivedCalls().Select(x => x.GetArguments()[0]).OfType<PaymentNotification>(),
                    x => x.Error == null);
            }
            var txid = await db.Observer.ExecuteScalarAsync<string>("SELECT transactionconfirmationdata FROM payments");
            Assert.NotNull(txid);
            Assert.Equal(1, sends);
            Assert.Equal(1, await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM payment_batches"));
            Assert.Equal(0m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
            var original = await node.WalletRpcAsync("gettransaction", txid);
            if(broadcast) Assert.NotNull(await node.RootRpcAsync("getmempoolentry", txid));
            await node.RestartAsync(walletBroadcast: false);
            await node.WalletRpcAsync("rescanblockchain", 0);
            Assert.NotNull((await node.TryWalletRpcAsync("getmempoolentry", txid)).Error);
            Assert.Equal(0, (await node.WalletRpcAsync("gettransaction", txid))["confirmations"].Value<int>());
            // Production balance selection plus fresh handler: no new payout
            // merely because the original transaction left the mempool.
            await PayCurrentBalances();
            Assert.Equal(1, sends);
            Assert.Equal(txid, await db.Observer.ExecuteScalarAsync<string>("SELECT transactionconfirmationdata FROM payments"));
            await node.RootRpcAsync("sendrawtransaction", original["hex"].Value<string>());
            await node.GenerateAsync(1, CancellationToken.None);
            Assert.True((await node.WalletRpcAsync("gettransaction", txid))["confirmations"].Value<int>() > 0);
            await PayCurrentBalances();
            Assert.Equal(1, sends);
            Assert.Equal(1, await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM payments"));
            Assert.Equal(1, await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM payment_batches"));
            Assert.Equal(-1m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balance_changes"));
        }
    }
}
