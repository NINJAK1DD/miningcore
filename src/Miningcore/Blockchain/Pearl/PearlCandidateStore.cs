using System.Data;
using System.Numerics;
using Dapper;
using Miningcore.Extensions;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Newtonsoft.Json;
using Share = Miningcore.Blockchain.Share;
using Miningcore.Stratum;

namespace Miningcore.Blockchain.Pearl;

internal sealed class PearlCandidateStore(IConnectionFactory cf, IShareRepository shares, PoolConfig pool)
{
    internal sealed class Candidate
    {
        public string PoolId { get; set; }
        public string Identity { get; set; }
        public byte[] Header { get; set; }
        public byte[] Transactions { get; set; }
        public byte[] Proof { get; set; }
        public byte[] Target { get; set; }
        public string Share { get; set; }
        public byte[] Block { get; set; }
        public string BlockHash { get; set; }
        public DateTime Created { get; set; }
    }

    internal Task CheckSchemaAsync(CancellationToken ct) => cf.Run(async con =>
        await con.ExecuteAsync(new CommandDefinition("SELECT poolid, identity, header, transactions, proof, target, share, block, blockhash, created, lastattempt, retired FROM pearl_candidates LIMIT 0", cancellationToken: ct)));

    internal async Task CommitAsync(Share share, string identity, PearlJob job, byte[] proof, bool candidate, CancellationToken ct)
    {
        var id = ShareAccounting.ParseCanonicalId(share.AccountingId);
        var projections = new[] {ShareAccounting.ToPersistenceShare(share)};
        // Use the same immutable liability and payload as ShareRecorder replay. The
        // receipt, PPS credit/remainder/balance and candidate must commit before ACK.
        var credit = ShareAccounting.CreatePpsCredit(pool, share);
        var credits = credit == null ? Array.Empty<PpsShareCredit>() : new[] {credit};
        var batch = new ShareAccountingBatch {AccountingId = id, Shares = projections, PpsCredits = credits,
            Created = share.Created, PayloadHash = ShareAccounting.ComputePayloadHash(id, projections, credits)};
        try { await cf.RunTx(async (con, tx) =>
        {
            // Serialize capacity admission across all pools/processes, before allocating disk.
            if(candidate)
            {
                await con.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(244, 1)", transaction: tx, cancellationToken: ct));
            }
            var inserted = await shares.InsertAccountingBatchAsync(con, tx, batch, ct);
            if(candidate && inserted == ShareAccountingInsertResult.Inserted)
            {
                var count = await con.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM pearl_candidates WHERE poolid=@Id AND retired IS NULL", new {pool.Id}, tx, cancellationToken: ct));
                if(count >= 16) throw new StratumException(StratumError.Other, "Pearl candidate queue is full; retry this work later");
                var target = new byte[32];
                var encoded = job.Target.ToByteArray(true, true);
                encoded.CopyTo(target, 32 - encoded.Length);
                await con.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO pearl_candidates(poolid, identity, header, transactions, proof, target, share, created)
                    VALUES(@PoolId, @identity, @header, @transactions, @proof, @target, @payload, @Created)
                    """, new {share.PoolId, identity, header = job.Block.Header.ToArray(), transactions = job.Block.ExportTransactions(),
                        proof, target, payload = JsonConvert.SerializeObject(share), share.Created}, tx, cancellationToken: ct));
            }
        }, ct: ct, classifyCommitOutcome: true); }
        catch(TransactionCommittedCleanupException) { /* Receipt and candidate are durable. */ }
        catch(TransactionCommitOutcomeUncertainException)
        {
            // A missing receipt is not proof of rollback: the retry must keep this
            // exact payload, so an original COMMIT completing late remains idempotent.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var hash = await cf.Run(con => con.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT payloadhash FROM share_accounting_groups WHERE accountingid=@id", new {id}, cancellationToken: deadline.Token)));
            if(hash != batch.PayloadHash) throw;
        }
    }

    internal Task<Candidate> NextAsync(string poolId, CancellationToken ct) => cf.Run(con =>
        con.QuerySingleOrDefaultAsync<Candidate>(new CommandDefinition("SELECT * FROM pearl_candidates WHERE poolid=@poolId AND retired IS NULL AND (lastattempt IS NULL OR lastattempt < now() - interval '5 seconds') ORDER BY lastattempt NULLS FIRST, created LIMIT 1", new {poolId}, cancellationToken: ct)));

    internal Task<Candidate> ReadAsync(Candidate candidate, CancellationToken ct) => cf.Run(con =>
        con.QuerySingleOrDefaultAsync<Candidate>(new CommandDefinition("SELECT * FROM pearl_candidates WHERE poolid=@PoolId AND identity=@Identity", candidate, cancellationToken: ct)));

    internal Task MarkAttemptAsync(Candidate candidate, CancellationToken ct) => cf.Run(async con =>
        await con.ExecuteAsync(new CommandDefinition("UPDATE pearl_candidates SET lastattempt=now() WHERE poolid=@PoolId AND identity=@Identity", candidate, cancellationToken: ct)));

    internal Task<int> CountAsync(string poolId, CancellationToken ct) => cf.Run(con =>
        con.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM pearl_candidates WHERE poolid=@poolId AND retired IS NULL", new {poolId}, cancellationToken: ct)));

    internal Task ArchiveAsync(Candidate candidate, CancellationToken ct) => cf.Run(async con =>
        await con.ExecuteAsync(new CommandDefinition("UPDATE pearl_candidates SET retired=now() WHERE poolid=@PoolId AND identity=@Identity AND retired IS NULL", candidate, cancellationToken: ct)));

    internal Task SaveBlockAsync(Candidate candidate, byte[] block, string hash, CancellationToken ct) => cf.Run(async con =>
        await con.ExecuteAsync(new CommandDefinition("UPDATE pearl_candidates SET block=@block, blockhash=@hash WHERE poolid=@PoolId AND identity=@Identity AND block IS NULL",
            new {candidate.PoolId, candidate.Identity, block, hash}, cancellationToken: ct)));

    internal Task<bool> CompleteAsync(Candidate candidate, Block block, IBlockRepository blocks, CancellationToken ct) => cf.RunTx(async (con, tx) =>
    {
        // DELETE takes the row lock. Only its winner may insert a block, including after an uncertain commit.
        var removed = await con.ExecuteAsync(new CommandDefinition("DELETE FROM pearl_candidates WHERE poolid=@PoolId AND identity=@Identity", candidate, tx, cancellationToken: ct));
        if(removed == 1 && block != null) await blocks.InsertAsync(con, tx, block, ct);
        return removed == 1;
    }, ct: ct, classifyCommitOutcome: true);
}
