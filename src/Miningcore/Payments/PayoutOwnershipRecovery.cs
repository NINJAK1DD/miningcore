using System.Data;
using System.Text.Json;
using Dapper;
using Miningcore.Persistence;
using NLog;

namespace Miningcore.Payments;

internal sealed record PayoutOwnershipSnapshot
{
    public string Database { get; init; }
    public string DatabaseRole { get; init; }
    public string ServerAddress { get; init; }
    public int? ServerPort { get; init; }
    public string Schema { get; init; }
    public long RelationId { get; init; }
    public long Generation { get; init; }
    public Guid? OwnerId { get; init; }
    public string OwnerHost { get; init; }
    public int? OwnerProcessId { get; init; }
    public DateTime? Acquired { get; init; }
    public bool LockHeld { get; init; }
    public IReadOnlyList<PayoutRecoveryBatch> Batches { get; init; } = [];
}

internal sealed record PayoutRecoveryBatch(string PoolId, string TransactionId, DateTime Created);

internal interface IPayoutOwnershipRecovery
{
    Task<PayoutOwnershipSnapshot> InspectAsync(CancellationToken ct);
    Task ReleaseAsync(PayoutOwnershipSnapshot inspected, CancellationToken ct);
}

/// <summary>
/// Implements the guarded manual-release runbook. Database evidence cannot establish that
/// an old process is dead or that an unrecorded wallet submission did not succeed.
/// </summary>
internal sealed class PostgresPayoutOwnershipRecovery(IConnectionFactory cf) : IPayoutOwnershipRecovery
{
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private static readonly object lockParameters = new
    {
        lockNamespace = PostgresPayoutManagerLease.LockNamespace,
        lockKey = PostgresPayoutManagerLease.LockKey,
    };

    private static async Task<bool> TryLockAsync(IDbConnection con, IDbTransaction tx, CancellationToken ct) =>
        await con.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT pg_try_advisory_xact_lock(@lockNamespace, @lockKey)",
            lockParameters, tx, commandTimeout: 5, cancellationToken: ct));

    public async Task<PayoutOwnershipSnapshot> InspectAsync(CancellationToken ct)
    {
        using var con = await OpenAsync(ct);
        using var tx = con.BeginTransaction();
        var available = await TryLockAsync(con, tx, ct);
        const string query = @"SELECT current_database() AS Database, current_user AS DatabaseRole,
                inet_server_addr()::text AS ServerAddress, inet_server_port() AS ServerPort,
                ns.nspname AS Schema, rel.oid::bigint AS RelationId,
                owner.generation AS Generation, owner.owner_id AS OwnerId,
                owner.owner_host AS OwnerHost, owner.owner_process_id AS OwnerProcessId,
                owner.acquired AS Acquired
            FROM pg_class rel JOIN pg_namespace ns ON ns.oid = rel.relnamespace
            LEFT JOIN payout_manager_ownership owner ON owner.id = 1
            WHERE rel.oid = to_regclass('payout_manager_ownership')";
        var snapshot = await con.QuerySingleAsync<PayoutOwnershipSnapshot>(new CommandDefinition(
            query, transaction: tx, commandTimeout: 5, cancellationToken: ct));
        IReadOnlyList<PayoutRecoveryBatch> batches = [];
        if(snapshot.OwnerId.HasValue)
        {
            // These are only recorded batches, not a journal of in-flight wallet operations.
            batches = (await con.QueryAsync<PayoutRecoveryBatch>(new CommandDefinition(@"
                SELECT poolid AS PoolId, transactionconfirmationdata AS TransactionId, created AS Created
                FROM payment_batches
                WHERE @acquired::timestamptz IS NULL OR created >= @acquired
                ORDER BY created DESC, poolid, transactionconfirmationdata LIMIT 10",
                new { acquired = snapshot.Acquired }, tx, commandTimeout: 5,
                cancellationToken: ct))).ToArray();
        }
        tx.Rollback(); // Release the inspection lock before waiting for human input.
        return snapshot with { LockHeld = !available, Batches = batches };
    }

    public async Task ReleaseAsync(PayoutOwnershipSnapshot inspected, CancellationToken ct)
    {
        if(inspected.OwnerId == null || inspected.LockHeld)
            throw new InvalidOperationException("No releasable ownership record was inspected.");

        using var con = await OpenAsync(ct);
        using var tx = con.BeginTransaction();
        if(!await TryLockAsync(con, tx, ct))
            throw new InvalidOperationException("A payout manager now holds the database lock. Protection was kept.");

        var table = "\"" + inspected.Schema.Replace("\"", "\"\"") + "\".payout_manager_ownership";
        await con.ExecuteAsync(new CommandDefinition(
            $"SET LOCAL lock_timeout = '5s'; LOCK TABLE {table} IN ROW EXCLUSIVE MODE",
            transaction: tx, commandTimeout: 5, cancellationToken: ct));
        var sameRelation = await con.ExecuteScalarAsync<bool>(new CommandDefinition(@"
            SELECT current_database() = @Database AND current_user = @DatabaseRole
                AND to_regclass('payout_manager_ownership')::oid::bigint = @RelationId
                AND to_regclass(@table)::oid::bigint = @RelationId",
            new { inspected.Database, inspected.DatabaseRole, inspected.RelationId, table }, tx,
            commandTimeout: 5, cancellationToken: ct));
        if(!sameRelation)
            throw new InvalidOperationException("The inspected database or ownership table changed. Protection was kept.");

        logger.Warn("Operator requested payout ownership release for generation {0}, token {1}",
            inspected.Generation, inspected.OwnerId);
        var changed = await con.ExecuteAsync(new CommandDefinition($@"
            UPDATE {table} SET owner_id = NULL, owner_host = NULL, owner_process_id = NULL, released = now()
            WHERE id = 1 AND owner_id = @OwnerId AND generation = @Generation",
            new { inspected.OwnerId, inspected.Generation }, tx, commandTimeout: 5, cancellationToken: ct));
        if(changed != 1)
            throw new InvalidOperationException("The inspected payout owner changed. Protection was kept; inspect again.");
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        logger.Warn("Operator released payout ownership generation {0}, token {1}; wallet reconciliation was operator-confirmed",
            inspected.Generation, inspected.OwnerId);
    }

    private Task<IDbConnection> OpenAsync(CancellationToken ct) => cf is ICancellableConnectionFactory cancellable
        ? cancellable.OpenConnectionAsync(ct)
        : cf.OpenConnectionAsync();
}

internal static class PayoutOwnershipRecoveryAssistant
{
    internal const string RecoveryHint = "Stop the service restart loop, then run Miningcore with " +
        "--recover-payout-ownership -c <the-same-config-file> in a terminal. " +
        "See docs/database.md#recover-payout-manager-ownership-safely.";

    internal static async Task<bool> RunAsync(IPayoutOwnershipRecovery recovery, TextReader input,
        TextWriter output, bool interactive, CancellationToken ct, bool quietIfUnowned = false)
    {
        var snapshot = await recovery.InspectAsync(ct);
        if(snapshot.OwnerId == null && !snapshot.LockHeld)
        {
            if(!quietIfUnowned)
                await output.WriteLineAsync("No payout ownership marker is blocking startup. Nothing was changed.");
            return true;
        }

        await output.WriteLineAsync("Miningcore needs your help before starting payouts.");
        await output.WriteLineAsync($"Database: {Display(snapshot.Database)}; role: {Display(snapshot.DatabaseRole)}; schema: {Display(snapshot.Schema)}");
        await output.WriteLineAsync($"Database server: {Display(snapshot.ServerAddress)}; port: {snapshot.ServerPort?.ToString() ?? "unknown"}");
        await output.WriteLineAsync($"Previous host: {Display(snapshot.OwnerHost)}; process: {snapshot.OwnerProcessId?.ToString() ?? "unknown"}");
        await output.WriteLineAsync($"Ownership acquired (UTC): {snapshot.Acquired?.ToUniversalTime().ToString("O") ?? "unknown"}");
        await output.WriteLineAsync($"Owner token: {snapshot.OwnerId}; generation: {snapshot.Generation}");
        if(snapshot.LockHeld)
        {
            await output.WriteLineAsync("A live database session holds the payout lock. Release is refused. Stop the previous instance cleanly first.");
            return false;
        }

        await output.WriteLineAsync("The database lock is available, but this does NOT prove the previous process has stopped or that all wallet payments were recorded.");
        if(!interactive)
        {
            await output.WriteLineAsync("Recovery requires an interactive terminal. Protection was kept. " + RecoveryHint);
            return false;
        }

        while(true)
        {
            await output.WriteLineAsync("[1] Keep protection and exit (default)\n[2] Review recorded payments and recovery checks\n[3] Release protection after completing the checks");
            await output.WriteAsync("Choice [1]: ");
            var choice = (await ReadLineAsync(input, ct))?.Trim();
            if(choice is null or "" or "1")
                return await KeepAsync(output);
            if(choice is not ("2" or "3"))
            {
                await output.WriteLineAsync("Enter 1, 2 or 3.");
                continue;
            }

            await output.WriteLineAsync("Latest recorded payment batches since ownership was acquired (up to 10; not a complete wallet history):");
            foreach(var batch in snapshot.Batches)
                await output.WriteLineAsync($"  {batch.Created.ToUniversalTime():O} pool {Display(batch.PoolId)} transaction {Display(batch.TransactionId)}");
            if(snapshot.Batches.Count == 0)
                await output.WriteLineAsync("  No recorded batches found. This does NOT prove no payment was sent.");
            await output.WriteLineAsync("Stop every previous Miningcore instance using this database, including other hosts/containers, and disable automatic restarts. A PID or a missing lock alone is not proof.");
            await output.WriteLineAsync("Review payout logs from acquisition through the interruption, including rotated logs. Compare every affected wallet's transaction history with payments and payment_batches. Resolve any sent-but-unrecorded or uncertain payment before release. See docs/database.md#recover-payout-manager-ownership-safely. If unsure, keep protection and exit.");
            if(choice == "2")
                continue;

            await output.WriteAsync("Type STOPPED to confirm every previous instance is stopped and cannot restart: ");
            if(await ReadLineAsync(input, ct) != "STOPPED")
                return await KeepAsync(output);
            await output.WriteAsync("Type CHECKED to confirm wallet/log reconciliation is complete and no uncertain payout remains: ");
            if(await ReadLineAsync(input, ct) != "CHECKED")
                return await KeepAsync(output);
            await output.WriteAsync($"To release only this record, type RELEASE {snapshot.OwnerId}: ");
            if(await ReadLineAsync(input, ct) != $"RELEASE {snapshot.OwnerId}")
                return await KeepAsync(output);

            ct.ThrowIfCancellationRequested();
            await recovery.ReleaseAsync(snapshot, ct);
            await output.WriteLineAsync($"Released ownership generation {snapshot.Generation}, token {snapshot.OwnerId} at {DateTime.UtcNow:O}. Normal startup must acquire new ownership before payouts can run.");
            return true;
        }
    }

    private static async Task<bool> KeepAsync(TextWriter output)
    {
        await output.WriteLineAsync("Protection was kept. Miningcore will not start payouts.");
        return false;
    }

    // Console.In can implement ReadLineAsync with a synchronous read. Keep Ctrl+C
    // responsive even then; the abandoned reader cannot execute any recovery action.
    private static Task<string> ReadLineAsync(TextReader input, CancellationToken ct) =>
        Task.Run(() => input.ReadLineAsync(ct).AsTask(), ct).WaitAsync(ct);

    // Database strings must not inject terminal controls or misleading extra prompt lines.
    private static string Display(string value) => JsonSerializer.Serialize(value ?? "unknown");
}
