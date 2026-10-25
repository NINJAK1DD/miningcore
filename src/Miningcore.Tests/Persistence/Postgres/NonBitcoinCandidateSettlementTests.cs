using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Blockchain.Alephium;
using Miningcore.Blockchain.Ergo;
using Miningcore.Blockchain.Ethereum;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Persistence.Repositories;
using Miningcore.Tests.Mining;
using Miningcore.Tests.Util;
using Miningcore.Tests.Util.Postgres;
using Newtonsoft.Json.Linq;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class NonBitcoinCandidateSettlementTests
{
    [PostgresIntegrationFact]
    public async Task AllConcretePolicies_ReplayOnceRollbackCleanlyAndSettleAllSupportedSchemes()
    {
        var schema = $"candidate_credit_{Guid.NewGuid():N}";
        var cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES")) { SearchPath = schema, Pooling = false };
        await using var connection = new NpgsqlConnection(cs.ConnectionString);
        await connection.OpenAsync();
        try
        {
            await connection.ExecuteAsync($"CREATE SCHEMA {schema}");
            await connection.ExecuteAsync(@"CREATE TABLE shares(
                poolid text NOT NULL, blockheight bigint NOT NULL, difficulty double precision NOT NULL,
                networkdifficulty double precision NOT NULL, sharedifficulty double precision NULL,
                actualdifficulty double precision NULL, miner text NOT NULL, worker text NULL,
                useragent text NULL, ipaddress text NOT NULL, source text NULL, sessionid text NULL, created timestamptz NOT NULL);
                CREATE TABLE balances(poolid text NOT NULL, address text NOT NULL, amount decimal(28,12) NOT NULL DEFAULT 0,
                    created timestamptz NOT NULL, updated timestamptz NOT NULL, PRIMARY KEY(poolid,address));
                CREATE TABLE balance_changes(id bigserial PRIMARY KEY, poolid text NOT NULL, address text NOT NULL,
                    amount decimal(28,12) NOT NULL, usage text NULL, tags text[] NULL, created timestamptz NOT NULL);");
            await connection.ExecuteAsync((await File.ReadAllTextAsync(PostgresTestScripts.PathFor("add_share_accounting.sql")))
                .Replace("\\set ON_ERROR_STOP on", string.Empty, StringComparison.Ordinal));
            var mapper = AutoMapperFactory.CreateMapper();
            var repository = new ShareRepository(mapper);
            var factory = new PgConnectionFactory(cs.ConnectionString);
            var balances = new BalanceRepository(mapper);
            foreach(var name in NonBitcoinCandidateCreditTests.Jobs.Select(x => (string) x[0]).Distinct())
            {
                var type = typeof(AlephiumJob).Assembly.GetType(name);
                var job = RuntimeHelpers.GetUninitializedObject(type);
                NonBitcoinCandidateCreditTests.ConfigurePolicy(job);
                var ceiling = CandidateShareCredit.Limit(job);
                var context = new WorkerContextBase();
                context.Init(ceiling * 200, null, new MockMasterClock());
                context.JobForDifficulty(job, (_, _) => throw new InvalidOperationException(), "settlement");

                var credit = context.ValidateShareDifficulty(ceiling * 2, true, job);
                var peer = new WorkerContextBase();
                peer.Init(ceiling, null, new MockMasterClock());
                peer.JobForDifficulty(job, (_, _) => throw new InvalidOperationException(), "ordinary-peer");
                var peerCredit = peer.ValidateShareDifficulty(ceiling, false, job);
                Assert.Equal(ceiling, credit);
                Assert.Equal(ceiling, peerCredit);
                Assert.InRange((context.Difficulty / peerCredit), 199.999, 200.001);
                // Without the cap this candidate would receive 200 ordinary shares'
                // weight. The independently admitted peer must still receive half.
                Assert.InRange(context.Difficulty / (context.Difficulty + peerCredit), .995, .996);
                var scale = job is ErgoJob ? ErgoConstants.ShareMultiplier : 1d;
                var stored = job is EthereumJob ? credit * EthereumConstants.Pow2x32 : credit / scale;
                var peerStored = job is EthereumJob ? peerCredit * EthereumConstants.Pow2x32 : peerCredit / scale;
                IPayoutHandler normalization = job switch
                {
                    AlephiumJob => (IPayoutHandler) RuntimeHelpers.GetUninitializedObject(typeof(AlephiumPayoutHandler)),
                    ErgoJob => (IPayoutHandler) RuntimeHelpers.GetUninitializedObject(typeof(ErgoPayoutHandler)),
                    KaspaJob => (IPayoutHandler) RuntimeHelpers.GetUninitializedObject(typeof(KaspaPayoutHandler)),
                    _ => null
                };
                if(job is KaspaJob)
                    NonBitcoinCandidateCreditTests.Set(normalization, "poolConfig", new PoolConfig { Template = new KaspaCoinTemplate
                        { Symbol = job is Miningcore.Blockchain.Kaspa.Custom.Spectre.SpectreJob ? "SPR" : "KAS" } });
                var adjustment = normalization?.AdjustShareDifficulty(1d) ?? 1d;
                foreach(var payout in new[] { PayoutScheme.PROP, PayoutScheme.PPLNS, PayoutScheme.PPLNSBF })
                {
                    var id = name + payout;
                    var created = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
                    var batches = new ShareAccountingBatch[2];
                    for(var i = 0; i < batches.Length; i++)
                    {
                        var accountingId = Guid.NewGuid();
                        var share = new Share { PoolId = id, BlockHeight = 100, Miner = i == 0 ? "candidate" : "peer",
                            Difficulty = i == 0 ? stored : peerStored, NetworkDifficulty = stored * adjustment, IpAddress = "127.0.0.1",
                            AccountingId = accountingId, AccountingRole = 1, RewardBasisSatoshis = 5_000_000_000, Created = created.AddSeconds(-i) };
                        batches[i] = new ShareAccountingBatch { AccountingId = accountingId, Created = share.Created,
                            Shares = new[] { share }, PpsCredits = Array.Empty<PpsShareCredit>(),
                            PayloadHash = ShareAccounting.ComputePayloadHash(accountingId, new[] { share }, Array.Empty<PpsShareCredit>()) };
                        await using var tx = await connection.BeginTransactionAsync();
                        Assert.Equal(ShareAccountingInsertResult.Inserted, await repository.InsertAccountingBatchAsync(connection, tx, batches[i], CancellationToken.None));
                        await tx.CommitAsync();
                    }
                    await using(var tx = await connection.BeginTransactionAsync())
                    {
                        foreach(var batch in batches)
                            Assert.Equal(ShareAccountingInsertResult.AlreadyCommitted, await repository.InsertAccountingBatchAsync(connection, tx, batch, CancellationToken.None));
                        await tx.CommitAsync();
                    }
                    Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM shares WHERE poolid=@id", new { id }));
                    var pool = Substitute.For<IMiningPool>();
                    pool.Config.Returns(new PoolConfig { Id = id, PaymentProcessing = new PoolPaymentProcessingConfig
                        { PayoutScheme = payout, PayoutSchemeConfig = JObject.FromObject(new { Factor = 2, BlockFinderPercentage = 5 }) } });
                    var handler = Substitute.For<IPayoutHandler>();
                    handler.AdjustShareDifficulty(Arg.Any<double>()).Returns(c => c.Arg<double>() * adjustment);
                    IPayoutScheme scheme = payout switch
                    {
                        PayoutScheme.PROP => new PROPPaymentScheme(factory, repository, Substitute.For<IBlockRepository>(), balances),
                        PayoutScheme.PPLNS => new PPLNSPaymentScheme(factory, repository, Substitute.For<IBlockRepository>(), balances),
                        _ => new PPLNSBFPaymentScheme(factory, repository, Substitute.For<IBlockRepository>(), balances)
                    };
                    var block = new Block { PoolId = id, BlockHeight = 100, Miner = "candidate", Created = created.AddSeconds(1) };
                    await using(var tx = await connection.BeginTransactionAsync())
                    {
                        await scheme.UpdateBalancesAsync(connection, tx, pool, handler, block, 50m, CancellationToken.None);
                        await tx.RollbackAsync();
                    }
                    Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM balances WHERE poolid=@id", new { id }));
                    await using(var tx = await connection.BeginTransactionAsync())
                    {
                        await scheme.UpdateBalancesAsync(connection, tx, pool, handler, block, 50m, CancellationToken.None);
                        await tx.CommitAsync();
                    }
                    Assert.Equal(payout == PayoutScheme.PPLNSBF ? 26.25m : 25m, await balances.GetBalanceAsync(connection, id, "candidate"));
                    Assert.Equal(payout == PayoutScheme.PPLNSBF ? 23.75m : 25m, await balances.GetBalanceAsync(connection, id, "peer"));
                    Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM balance_changes WHERE poolid=@id", new { id }));
                    await using(var tx = await connection.BeginTransactionAsync())
                    {
                        foreach(var batch in batches)
                            Assert.Equal(ShareAccountingInsertResult.AlreadyCommitted, await repository.InsertAccountingBatchAsync(connection, tx, batch, CancellationToken.None));
                        await tx.CommitAsync();
                    }
                    Assert.Equal(payout == PayoutScheme.PROP ? 0 : 2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM shares WHERE poolid=@id", new { id }));
                    Assert.Equal(50m, await connection.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances WHERE poolid=@id", new { id }));
                }
                var family = System.Enum.Parse<CoinFamily>(type.Namespace.Split('.')[2]);
                var pps = new PoolConfig { Id = name, Template = new BitcoinTemplate { Family = family },
                    PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true, PayoutScheme = PayoutScheme.PPS } };
                Assert.Contains("restricted", Assert.Throws<InvalidDataException>(() => ShareAccounting.AttachPpsCreditEvidence(pps,
                    new Miningcore.Blockchain.Share { Difficulty = stored, NetworkDifficulty = stored * adjustment,
                        RewardBasisSatoshis = 5_000_000_000, Created = DateTime.UtcNow })).Message);
                var recovered = new Miningcore.Blockchain.Share { PpsCalculatedAmount = 1m, Difficulty = stored,
                    NetworkDifficulty = stored * adjustment, RewardBasisSatoshis = 5_000_000_000, Created = DateTime.UtcNow };
                Assert.Contains("restricted", Assert.Throws<InvalidDataException>(() => ShareAccounting.CreatePpsCredit(
                    new PoolConfig { Id = name, Template = pps.Template }, recovered, allowMissingPpsConfiguration: true)).Message);
            }
        }
        finally
        {
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
