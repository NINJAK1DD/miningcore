using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Blockchain.Pearl;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Tests.Util.Postgres;
using Miningcore.Tests.Persistence.Postgres;
using Npgsql;
using NSubstitute;
using Xunit;
using Share = Miningcore.Blockchain.Share;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlAccountingIntegrationTests
{
    private sealed class Factory(string connectionString) : IConnectionFactory
    {
        public async Task<IDbConnection> OpenConnectionAsync()
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            return connection;
        }
    }

    [PostgresIntegrationFact]
    public async Task ShareAndCandidateCommitAtomicallyWithBoundedQueueAndExactlyOnceReplay()
    {
        var connectionString = Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES");
        var schema = "pearl_accounting_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        try
        {
            await admin.ExecuteAsync($"CREATE SCHEMA {schema}; SET search_path TO {schema}, public");
            await admin.ExecuteAsync((await File.ReadAllTextAsync(PostgresTestScripts.PathFor("createdb.sql"))).Replace("SET ROLE miningcore;", ""));
            var factory = new Factory(new NpgsqlConnectionStringBuilder(connectionString) {SearchPath = schema + ",public"}.ConnectionString);
            var repository = new ShareRepository(AutoMapperFactory.CreateMapper());
            var pool = new Miningcore.Configuration.PoolConfig {Id = "pearl"};
            var store = new PearlCandidateStore(factory, repository, pool);
            var worker = new PearlWorkerContext {Difficulty = 1};
            var job = worker.Issue(PearlWorkTests.Block(), 2);
            Share CreateShare() => new()
            {
                PoolId = "pearl", AccountingId = ShareAccounting.CreateId(), AccountingRole = Miningcore.Blockchain.ShareAccountingRole.Single,
                BlockHeight = 1, Miner = PearlWorkTests.Address, IpAddress = "127.0.0.1", SessionId = "session",
                Created = DateTime.UtcNow, Difficulty = 1, ShareDifficulty = 2, ActualDifficulty = 2,
                NetworkDifficulty = 100, RewardBasisSatoshis = job.Block.RewardGrains
            };
            var failed = CreateShare();
            var failedId = failed.AccountingId + failed.AccountingId;
            worker.RememberAttempt(job, "failed");
            await Assert.ThrowsAnyAsync<Exception>(() => worker.CommitAsync(job, "failed", failedId, failed,
                original => store.CommitAsync(original, failedId, job, Array.Empty<byte>(), true, CancellationToken.None)));
            Assert.DoesNotContain("failed", job.Attempts);
            Assert.DoesNotContain(failedId, job.Proofs);
            Assert.Equal(0, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares"));
            // A conclusive rollback can retry exactly once through the admission path.
            worker.RememberAttempt(job, "failed");
            await worker.CommitAsync(job, "failed", failedId, failed,
                original => store.CommitAsync(original, failedId, job, new byte[] {1}, false, CancellationToken.None));
            Assert.Equal(1, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares"));
            var first = CreateShare();
            await store.CommitAsync(first, first.AccountingId + first.AccountingId, job, new byte[] {1}, true, CancellationToken.None);
            await store.CommitAsync(first, first.AccountingId + first.AccountingId, job, new byte[] {1}, true, CancellationToken.None);
            Assert.Equal(2, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares"));
            Assert.Equal(1, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pearl_candidates"));
            first.Difficulty = 3;
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(first, first.AccountingId + first.AccountingId, job, new byte[] {1}, false, CancellationToken.None));
            for(var i = 1; i < 16; i++)
            {
                var share = CreateShare();
                await store.CommitAsync(share, share.AccountingId + share.AccountingId, job, new byte[] {1}, true, CancellationToken.None);
            }
            var excess = CreateShare();
            var excessId = excess.AccountingId + excess.AccountingId;
            worker.RememberAttempt(job, "full");
            await Assert.ThrowsAsync<Miningcore.Stratum.StratumException>(() => worker.CommitAsync(job, "full", excessId, excess,
                original => store.CommitAsync(original, excessId, job, new byte[] {1}, true, CancellationToken.None)));
            Assert.DoesNotContain("full", job.Attempts);
            Assert.DoesNotContain(excessId, job.Proofs);
            Assert.Equal(17, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares"));
            Assert.Equal(17, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM share_accounting_groups"));
            // Already-committed receipts remain idempotent even while the queue is full.
            first.Difficulty = 1;
            await store.CommitAsync(first, first.AccountingId + first.AccountingId, job, new byte[] {1}, true, CancellationToken.None);
            var reopened = new PearlCandidateStore(factory, repository, pool);
            var recovered = await reopened.NextAsync("pearl", CancellationToken.None);
            Assert.NotNull(recovered);
            Assert.Equal(job.Block.Header.ToArray(), recovered.Header);
            Assert.Equal(job.Block.ExportTransactions(), recovered.Transactions);
            await reopened.MarkAttemptAsync(recovered, CancellationToken.None);
            Assert.NotEqual(recovered.Identity, (await reopened.NextAsync("pearl", CancellationToken.None)).Identity);
            Assert.Equal(recovered.Identity, (await reopened.ReadAsync(recovered, CancellationToken.None)).Identity);
            var blocks = new BlockRepository(AutoMapperFactory.CreateMapper());
            var found = new Miningcore.Persistence.Model.Block {PoolId = "pearl", BlockHeight = 1,
                Hash = new string('a',64), TransactionConfirmationData = new string('b',64), Miner = PearlWorkTests.Address, Reward = 1,
                NetworkDifficulty = 1, Created = DateTime.UtcNow, Status = Miningcore.Persistence.Model.BlockStatus.Pending};
            var completions = await Task.WhenAll(reopened.CompleteAsync(recovered, found, blocks, CancellationToken.None),
                reopened.CompleteAsync(recovered, found, blocks, CancellationToken.None));
            Assert.Single(completions, completed => completed);
            Assert.Equal(1, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM blocks"));
            Assert.Equal(15, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pearl_candidates"));
            worker.RememberAttempt(job, "full");
            await worker.CommitAsync(job, "full", excessId, excess,
                original => reopened.CommitAsync(original, excessId, job, new byte[] {1}, true, CancellationToken.None));
            Assert.Equal(18, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares"));
            Assert.Equal(16, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pearl_candidates"));
            var archived = await reopened.NextAsync("pearl", CancellationToken.None);
            await reopened.ArchiveAsync(archived, CancellationToken.None);
            Assert.Equal(15, await reopened.CountAsync("pearl", CancellationToken.None));
            Assert.NotNull(await reopened.ReadAsync(archived, CancellationToken.None));
            var uncertain = CreateShare();
            var uncertainId = uncertain.AccountingId + uncertain.AccountingId;
            worker.RememberAttempt(job, "uncertain");
            await Assert.ThrowsAsync<Miningcore.Extensions.TransactionCommitOutcomeUncertainException>(() =>
                worker.CommitAsync(job, "uncertain", uncertainId, uncertain, async original =>
                {
                    await store.CommitAsync(original, uncertainId, job, new byte[] {1}, false, CancellationToken.None);
                    throw new Miningcore.Extensions.TransactionCommitOutcomeUncertainException("Lost commit acknowledgement", new IOException());
                }));
            var retryPayload = CreateShare(); // Different timestamp/ID must not replace pending evidence.
            worker.RememberAttempt(job, "uncertain");
            await worker.CommitAsync(job, "uncertain", uncertainId, retryPayload, original =>
            {
                Assert.Same(uncertain, original);
                return reopened.CommitAsync(original, uncertainId, job, new byte[] {1}, false, CancellationToken.None);
            });
            Assert.Equal(19, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares"));
            Assert.Empty(job.PendingShares);
            // Exercise the store's uncertain-commit receipt lookup after a real
            // PostgreSQL commit. The injected exception models a lost reply.
            var lostReply = Substitute.For<IShareRepository>();
            lostReply.InsertAccountingBatchAsync(Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(),
                Arg.Any<ShareAccountingBatch>(), Arg.Any<CancellationToken>()).Returns(async Task<ShareAccountingInsertResult> (call) =>
            {
                var con = call.Arg<IDbConnection>(); var tx = call.Arg<IDbTransaction>();
                await repository.InsertAccountingBatchAsync(con, tx, call.Arg<ShareAccountingBatch>(), CancellationToken.None);
                tx.Commit();
                throw new Miningcore.Extensions.TransactionCommitOutcomeUncertainException("Lost commit reply", new IOException());
            });
            var receipt = CreateShare();
            await new PearlCandidateStore(factory, lostReply, pool).CommitAsync(receipt,
                receipt.AccountingId + receipt.AccountingId, job, new byte[] {1}, false, CancellationToken.None);
            Assert.Equal(20, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares"));
            await store.CommitAsync(receipt, receipt.AccountingId + receipt.AccountingId, job, new byte[] {1}, false, CancellationToken.None);
            Assert.Equal(20, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares"));
        }
        finally
        {
            await admin.ExecuteAsync("ROLLBACK; RESET ROLE; SET search_path TO public");
            await admin.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
