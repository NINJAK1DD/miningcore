using System.Data;
using System.Text.Json;
using Dapper;
using Miningcore.Persistence;
using NLog;
using Npgsql;

namespace Miningcore.Payments;

internal sealed record PayoutOwnershipSnapshot
{
    public string Database { get; init; }
    public long DatabaseId { get; init; }
    public string DatabaseRole { get; init; }
    public string ServerAddress { get; init; }
    public int? ServerPort { get; init; }
    public string Schema { get; init; }
    public long RelationId { get; init; }
    public string BatchSchema { get; init; }
    public long? BatchRelationId { get; init; }
    public string EvidenceWarning { get; init; }
    public long Generation { get; init; }
    public Guid? OwnerId { get; init; }
    public string OwnerHost { get; init; }
    public int? OwnerProcessId { get; init; }
    public DateTime? Acquired { get; init; }
    public bool LockHeld { get; init; }
    public IReadOnlyList<PayoutRecoveryBatch> Batches { get; init; } = [];
}

internal sealed record PayoutRecoveryBatch(string PoolId, string TransactionId, DateTime Created);
internal sealed record PayoutRecoveryReceipt(Guid EventId, DateTime Released);
internal enum PayoutRecoveryResult { Ready = 0, Busy = 1, Kept = 78 }
internal sealed class PayoutRecoveryException(string message, int exitCode = ProcessExitCodes.PayoutOwnershipBlocked)
    : InvalidOperationException(message)
{
    public int ExitCode { get; } = exitCode;
}

internal interface IPayoutOwnershipRecovery
{
    Task<PayoutOwnershipSnapshot> InspectAsync(CancellationToken ct);
    Task<PayoutRecoveryReceipt> ReleaseAsync(PayoutOwnershipSnapshot inspected, string incidentReference, CancellationToken ct);
}

internal sealed class PostgresPayoutOwnershipRecovery(IConnectionFactory cf, Action<IDbTransaction> commit = null) : IPayoutOwnershipRecovery
{
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    internal const string OwnershipMigration = "The payout-manager ownership schema is missing. Apply add_payout_manager_ownership.sql before recovery.";
    internal const string AuditMigration = "Recovery audit storage is unavailable. Apply add_payout_recovery_audit.sql as a separate database administrator and restore protected audit schema USAGE, SELECT and record_payout_recovery EXECUTE permissions. Protection was kept.";
    private static readonly object lockParameters = new
    {
        lockNamespace = PostgresPayoutManagerLease.LockNamespace,
        lockKey = PostgresPayoutManagerLease.LockKey,
    };
    internal static string AuditSchema(long relationId) => "miningcore_audit_" + relationId;
    private static string Table(string schema, string name) => "\"" + schema.Replace("\"", "\"\"") + "\".\"" + name + "\"";

    public async Task<PayoutOwnershipSnapshot> InspectAsync(CancellationToken ct)
    {
        try
        {
            using var con = await OpenAsync(ct);
            // An observer must not take the payout lock and disrupt concurrent startup.
            const string query = @"SELECT current_database() AS Database, current_user AS DatabaseRole,
                    (SELECT oid::bigint FROM pg_database WHERE datname=current_database()) AS DatabaseId,
                    inet_server_addr()::text AS ServerAddress, inet_server_port() AS ServerPort,
                    ns.nspname AS Schema, rel.oid::bigint AS RelationId,
                    owner.generation AS Generation, owner.owner_id AS OwnerId,
                    owner.owner_host AS OwnerHost, owner.owner_process_id AS OwnerProcessId,
                    owner.acquired AS Acquired,
                    EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory'
                        AND database=(SELECT oid FROM pg_database WHERE datname=current_database())
                        AND classid=@lockNamespace::oid AND objid=@lockKey::oid AND objsubid=2 AND granted) AS LockHeld
                FROM pg_class rel JOIN pg_namespace ns ON ns.oid=rel.relnamespace
                LEFT JOIN payout_manager_ownership owner ON owner.id=1
                WHERE rel.oid=to_regclass('payout_manager_ownership')";
            var snapshot = await con.QuerySingleOrDefaultAsync<PayoutOwnershipSnapshot>(new CommandDefinition(
                query, lockParameters, commandTimeout: 5, cancellationToken: ct));
            if(snapshot == null)
                throw new PayoutRecoveryException(OwnershipMigration, ProcessExitCodes.GeneralFailure);
            if(snapshot.OwnerId == null || snapshot.LockHeld)
                return snapshot;

            // Optional evidence has its own timeout/failure boundary, outside a transaction.
            try
            {
                var batch = await con.QuerySingleOrDefaultAsync<PayoutOwnershipSnapshot>(new CommandDefinition(@"
                    SELECT ns.nspname AS BatchSchema, rel.oid::bigint AS BatchRelationId
                    FROM pg_class rel JOIN pg_namespace ns ON ns.oid=rel.relnamespace
                    WHERE rel.oid=to_regclass('payment_batches')", commandTimeout: 2, cancellationToken: ct));
                snapshot = snapshot with { BatchSchema = batch?.BatchSchema, BatchRelationId = batch?.BatchRelationId };
                if(batch?.BatchSchema != snapshot.Schema)
                    return snapshot with { EvidenceWarning = "Payment history is unavailable: payment_batches is missing or resolves outside the ownership schema. Verify search_path and reconcile against the correct database externally." };
                var batches = await con.QueryAsync<PayoutRecoveryBatch>(new CommandDefinition($@"
                    SELECT poolid AS PoolId, transactionconfirmationdata AS TransactionId, created AS Created
                    FROM {Table(snapshot.BatchSchema, "payment_batches")}
                    ORDER BY created DESC, poolid, transactionconfirmationdata LIMIT 10",
                    commandTimeout: 2, cancellationToken: ct));
                return snapshot with { Batches = batches.ToArray() };
            }
            catch(NpgsqlException) when(!ct.IsCancellationRequested)
            {
                return snapshot with { EvidenceWarning = "Payment history could not be read within its time/permission limit. Ownership inspection succeeded; reconcile history externally. Apply add_payout_recovery_audit.sql for the history index." };
            }
        }
        catch(NpgsqlException) when(ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Ownership inspection cancelled; no release was attempted.", ct);
        }
        catch(PostgresException ex) when(ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new PayoutRecoveryException(OwnershipMigration, ProcessExitCodes.GeneralFailure);
        }
        catch(PostgresException ex) when(ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new PayoutRecoveryException("The configured PostgreSQL role cannot inspect payout ownership. Ask the administrator to grant SELECT on payout_manager_ownership; see add_payout_manager_ownership.sql. Nothing was changed.", ProcessExitCodes.GeneralFailure);
        }
        catch(NpgsqlException) when(!ct.IsCancellationRequested)
        {
            throw new PayoutRecoveryException("Payout ownership inspection failed. Check database connectivity, permissions and migrations. No release was attempted.", ProcessExitCodes.GeneralFailure);
        }
    }

    public async Task<PayoutRecoveryReceipt> ReleaseAsync(PayoutOwnershipSnapshot inspected, string incidentReference, CancellationToken ct)
    {
        if(inspected.OwnerId == null || inspected.LockHeld)
            throw new PayoutRecoveryException("No releasable ownership record was inspected. Protection was kept.");
        if(string.IsNullOrWhiteSpace(incidentReference) || incidentReference.Length > 200 || incidentReference.Any(char.IsControl))
            throw new PayoutRecoveryException("An incident reference of 1–200 characters without control characters is required. Protection was kept.");
        var eventId = Guid.NewGuid();
        var committing = false;
        var writingAudit = false;
        try
        {
            using var con = await OpenAsync(ct);
            using var tx = con.BeginTransaction();
            if(!await con.ExecuteScalarAsync<bool>(new CommandDefinition(
                   "SELECT pg_try_advisory_xact_lock(@lockNamespace, @lockKey)", lockParameters, tx,
                   commandTimeout: 5, cancellationToken: ct)))
                throw new PayoutRecoveryException("A payout manager now holds the database lock. Protection was kept.");
            var table = Table(inspected.Schema, "payout_manager_ownership");
            await con.ExecuteAsync(new CommandDefinition(
                $"SET LOCAL lock_timeout='5s'; LOCK TABLE {table} IN ROW EXCLUSIVE MODE",
                transaction: tx, commandTimeout: 5, cancellationToken: ct));
            var sameRelation = await con.ExecuteScalarAsync<bool>(new CommandDefinition(@"
                SELECT current_database()=@Database AND current_user=@DatabaseRole
                    AND (SELECT oid::bigint FROM pg_database WHERE datname=current_database())=@DatabaseId
                    AND inet_server_addr()::text IS NOT DISTINCT FROM @ServerAddress
                    AND inet_server_port() IS NOT DISTINCT FROM @ServerPort
                    AND to_regclass('payout_manager_ownership')::oid::bigint=@RelationId
                    AND to_regclass(@table)::oid::bigint=@RelationId",
                new { inspected.Database, inspected.DatabaseId, inspected.DatabaseRole, inspected.ServerAddress,
                    inspected.ServerPort, inspected.RelationId, table }, tx, commandTimeout: 5, cancellationToken: ct));
            if(!sameRelation)
                throw new PayoutRecoveryException("The inspected database, role, endpoint or ownership table changed. Protection was kept.");
            if(inspected.EvidenceWarning == null && inspected.BatchRelationId.HasValue)
            {
                var history = Table(inspected.BatchSchema, "payment_batches");
                try
                {
                    await con.ExecuteAsync(new CommandDefinition($"LOCK TABLE {history} IN ACCESS SHARE MODE",
                        transaction: tx, commandTimeout: 5, cancellationToken: ct));
                    if(!await con.ExecuteScalarAsync<bool>(new CommandDefinition(@"
                        SELECT to_regclass('payment_batches')::oid::bigint=@BatchRelationId
                            AND to_regclass(@history)::oid::bigint=@BatchRelationId",
                        new { inspected.BatchRelationId, history }, tx, commandTimeout: 5, cancellationToken: ct)))
                        throw new PayoutRecoveryException("The payment-history relation changed. Protection was kept; inspect and reconcile again.");
                }
                catch(PostgresException ex) when(ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedFunction or PostgresErrorCodes.InvalidSchemaName or PostgresErrorCodes.InsufficientPrivilege)
                {
                    throw new PayoutRecoveryException("The inspected payment-history relation is unavailable. Protection was kept; inspect and reconcile again.");
                }
            }
            var auditFunction = Table(AuditSchema(inspected.RelationId), "record_payout_recovery");
            writingAudit = true;
            // Missing/denied audit append rolls back the ownership UPDATE as well.
            var receipt = await con.QuerySingleOrDefaultAsync<PayoutRecoveryReceipt>(new CommandDefinition($@"
                WITH previous AS MATERIALIZED (
                    SELECT * FROM {table} WHERE id=1 AND owner_id=@OwnerId AND generation=@Generation FOR UPDATE
                ), released AS (
                    UPDATE {table} target SET owner_id=NULL, owner_host=NULL, owner_process_id=NULL, released=clock_timestamp()
                    FROM previous WHERE target.id=previous.id
                    RETURNING previous.owner_id, previous.generation, previous.owner_host,
                        previous.owner_process_id, previous.acquired, target.released
                )
                SELECT receipt.event_id AS EventId, receipt.released AS Released
                FROM released CROSS JOIN LATERAL {auditFunction}(@eventId, released.owner_id,
                    released.generation, released.owner_host, released.owner_process_id,
                    released.acquired, released.released, @host, @pid, @incidentReference) receipt",
                new { inspected.OwnerId, inspected.Generation, inspected.Schema, inspected.RelationId,
                    eventId, host = Environment.MachineName, pid = Environment.ProcessId, incidentReference },
                tx, commandTimeout: 5, cancellationToken: ct));
            if(receipt == null)
                throw new PayoutRecoveryException("The inspected payout owner changed. Protection was kept; inspect again.");
            ct.ThrowIfCancellationRequested();
            committing = true;
            if(commit == null)
                tx.Commit();
            else
                commit(tx);
            logger.Warn("Operator released payout ownership generation {0}, token {1}; audit event {2}; incident {3}; released {4:O}",
                inspected.Generation, inspected.OwnerId, receipt.EventId, JsonSerializer.Serialize(incidentReference), receipt.Released);
            return receipt;
        }
        catch(NpgsqlException) when(!committing && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Release cancelled before commit. Protection was kept.", ct);
        }
        catch(PostgresException ex) when(!committing && ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedFunction or PostgresErrorCodes.InvalidSchemaName or PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new PayoutRecoveryException(writingAudit ? AuditMigration :
                "The inspected ownership table is unavailable or the configured role cannot lock/update it. Protection was kept. Check schema permissions and inspect again.");
        }
        catch(NpgsqlException) when(!ct.IsCancellationRequested || committing)
        {
            throw new PayoutRecoveryException(committing
                ? $"Release commit outcome is unknown. Inspect audit event {eventId} and ownership before any retry; do not assume protection remains."
                : "Release failed before commit. Protection was kept. Check connectivity and permissions, then inspect again.",
                ProcessExitCodes.GeneralFailure);
        }
    }

    private Task<IDbConnection> OpenAsync(CancellationToken ct) => cf is ICancellableConnectionFactory cancellable
        ? cancellable.OpenConnectionAsync(ct) : cf.OpenConnectionAsync();
}

internal static class PayoutOwnershipRecoveryAssistant
{
    internal const string RecoveryHint = "Stop the service restart loop, then run Miningcore with " +
        "--recover-payout-ownership -c <the-same-config-file> in a terminal. " +
        "See docs/database.md#recover-payout-manager-ownership-safely.";
    internal static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(2);

    internal static async Task<PayoutRecoveryResult> RunAsync(IPayoutOwnershipRecovery recovery, TextReader input,
        TextWriter output, bool interactive, CancellationToken ct, bool quietIfUnowned = false, TimeSpan? promptTimeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = promptTimeout ?? PromptTimeout;
        deadline.CancelAfter(timeout);
        async Task<string> ReadPromptAsync()
        {
            deadline.CancelAfter(timeout);
            var answer = await ReadLineAsync(input, deadline.Token);
            deadline.CancelAfter(timeout);
            return answer;
        }
        try
        {
            return await RunCoreAsync(recovery, input, output, interactive, deadline.Token, quietIfUnowned, ReadPromptAsync);
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            await output.WriteLineAsync("Recovery session timed out. No further release will be attempted; inspect ownership and the audit log before retrying if release had already begun.");
            return PayoutRecoveryResult.Kept;
        }
    }

    private static async Task<PayoutRecoveryResult> RunCoreAsync(IPayoutOwnershipRecovery recovery, TextReader input,
        TextWriter output, bool interactive, CancellationToken ct, bool quietIfUnowned, Func<Task<string>> readPrompt)
    {
        while(true)
        {
            var snapshot = await recovery.InspectAsync(ct);
            if(snapshot.OwnerId == null && !snapshot.LockHeld)
            {
                if(!quietIfUnowned)
                    await output.WriteLineAsync("No payout ownership marker is blocking startup. Nothing was changed.");
                return PayoutRecoveryResult.Ready;
            }
            await ShowOwnerAsync(snapshot, output);
            if(snapshot.LockHeld)
            {
                await output.WriteLineAsync("A live database session holds the payout lock. Release is refused; normal service startup may retry. Stop the previous instance cleanly first.");
                return PayoutRecoveryResult.Busy;
            }
            await output.WriteLineAsync("The database lock is available, but this does NOT prove the previous process has stopped or that all wallet payments were recorded.");
            if(!interactive)
            {
                await output.WriteLineAsync("Recovery requires an interactive terminal. Protection was kept. " + RecoveryHint);
                return PayoutRecoveryResult.Kept;
            }
            await output.WriteLineAsync("Each prompt expires after two minutes of inactivity; timeout keeps protection.");
            if(quietIfUnowned)
                await output.WriteLineAsync("Use --no-payout-recovery-prompt to disable automatic startup prompts.");
            await output.WriteLineAsync("[1] Keep protection and exit (default)\n[2] Refresh recorded payments and recovery checks\n[3] Release protection after completing the checks");
            await output.WriteAsync("Choice [1]: ");
            var choice = await readPrompt();
            if(choice is null or "" or "1")
                return await KeepAsync(output);
            if(choice is not ("2" or "3"))
            {
                await output.WriteLineAsync("Enter 1, 2 or 3.");
                continue;
            }
            // Refresh immediately before displaying reconciliation evidence and confirmations.
            var fresh = await recovery.InspectAsync(ct);
            if(fresh.OwnerId != snapshot.OwnerId || fresh.Generation != snapshot.Generation ||
                fresh.RelationId != snapshot.RelationId || fresh.Schema != snapshot.Schema || fresh.DatabaseRole != snapshot.DatabaseRole ||
                fresh.DatabaseId != snapshot.DatabaseId || fresh.Database != snapshot.Database ||
                fresh.ServerAddress != snapshot.ServerAddress || fresh.ServerPort != snapshot.ServerPort || fresh.LockHeld)
            {
                await output.WriteLineAsync("Ownership or database identity changed while reviewing. Start a new inspection; protection was kept.");
                return PayoutRecoveryResult.Kept;
            }
            snapshot = fresh;
            await ShowOwnerAsync(snapshot, output);
            await output.WriteLineAsync($"Payment evidence relation: {Display(snapshot.BatchSchema)}.payment_batches; OID {snapshot.BatchRelationId?.ToString() ?? "unavailable"}");
            if(snapshot.EvidenceWarning != null)
                await output.WriteLineAsync(snapshot.EvidenceWarning);
            else
            {
                await output.WriteLineAsync("Latest recorded payment batches (up to 10, across all times; not a complete wallet history):");
                foreach(var batch in snapshot.Batches)
                    await output.WriteLineAsync($"  {batch.Created.ToUniversalTime():O} pool {Display(batch.PoolId)} transaction {Display(batch.TransactionId)} [{(snapshot.Acquired.HasValue ? batch.Created >= snapshot.Acquired.Value ? "at/after acquisition" : "before acquisition" : "acquisition unknown")}]");
                if(snapshot.Batches.Count == 0)
                    await output.WriteLineAsync("  No recorded batches found. This does NOT prove no payment was sent.");
            }
            await output.WriteLineAsync("Stop every previous Miningcore instance using this database, including other hosts/containers, and disable automatic restarts. A PID or a missing lock alone is not proof.");
            await output.WriteLineAsync("Review payout logs from acquisition through interruption, including rotated logs. Compare every affected wallet's transaction history with payments and payment_batches. Resolve every sent-but-unrecorded or uncertain payment before release. If evidence is unavailable, reconcile externally or keep protection. See docs/database.md#recover-payout-manager-ownership-safely.");
            if(choice == "2")
                continue;
            await output.WriteAsync("Type STOPPED to confirm every previous instance is stopped and cannot restart: ");
            if(await readPrompt() != "STOPPED")
                return await KeepAsync(output);
            await output.WriteAsync("Type CHECKED to confirm wallet/log reconciliation is complete and no uncertain payout remains: ");
            if(await readPrompt() != "CHECKED")
                return await KeepAsync(output);
            await output.WriteAsync("Incident reference (1-200 characters, no secrets): ");
            var incident = await readPrompt();
            if(string.IsNullOrWhiteSpace(incident) || incident.Length > 200 || incident.Any(char.IsControl))
            {
                await output.WriteLineAsync("Invalid incident reference: enter 1–200 characters without control characters.");
                return await KeepAsync(output);
            }
            await output.WriteAsync($"To release only this record, type RELEASE {snapshot.OwnerId}: ");
            if(await readPrompt() != $"RELEASE {snapshot.OwnerId}")
                return await KeepAsync(output);
            ct.ThrowIfCancellationRequested();
            var receipt = await recovery.ReleaseAsync(snapshot, incident, ct);
            await output.WriteLineAsync($"Released ownership generation {snapshot.Generation}, token {snapshot.OwnerId} at {receipt.Released.ToUniversalTime():O}. Audit event {receipt.EventId} in {AuditTable(snapshot)}. Normal startup must acquire new ownership before payouts can run.");
            return PayoutRecoveryResult.Ready;
        }
    }

    private static string AuditTable(PayoutOwnershipSnapshot snapshot) => PostgresPayoutOwnershipRecovery.AuditSchema(snapshot.RelationId) + ".payout_manager_recovery_events";

    private static async Task ShowOwnerAsync(PayoutOwnershipSnapshot snapshot, TextWriter output)
    {
        await output.WriteLineAsync($"Database: {Display(snapshot.Database)}; role: {Display(snapshot.DatabaseRole)}; schema: {Display(snapshot.Schema)}; ownership OID {snapshot.RelationId}");
        await output.WriteLineAsync($"Protected audit relation: {Display(AuditTable(snapshot))}");
        await output.WriteLineAsync($"Database endpoint (rechecked on release): {Display(snapshot.ServerAddress)}; port: {snapshot.ServerPort?.ToString() ?? "unknown"}");
        await output.WriteLineAsync($"Previous host: {Display(snapshot.OwnerHost)}; process: {snapshot.OwnerProcessId?.ToString() ?? "unknown"}");
        await output.WriteLineAsync($"Ownership acquired (UTC): {snapshot.Acquired?.ToUniversalTime().ToString("O") ?? "unknown"}");
        await output.WriteLineAsync($"Owner token: {snapshot.OwnerId}; generation: {snapshot.Generation}");
    }
    private static async Task<PayoutRecoveryResult> KeepAsync(TextWriter output)
    {
        await output.WriteLineAsync("Protection was kept. Miningcore will not start payouts.");
        return PayoutRecoveryResult.Kept;
    }
    // Console.In can read synchronously. The abandoned CLI reader cannot mutate state;
    // on cancellation the process exits rather than creating another reader.
    private static async Task<string> ReadLineAsync(TextReader input, CancellationToken ct) =>
        (await Task.Run(() => input.ReadLineAsync(ct).AsTask(), ct).WaitAsync(ct))?.Trim();
    private static string Display(string value) => JsonSerializer.Serialize(value ?? "unknown");
}
