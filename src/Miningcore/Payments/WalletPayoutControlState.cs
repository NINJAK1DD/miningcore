using Dapper;
using Miningcore.Persistence;
using System.Data;

namespace Miningcore.Payments;

/// <summary>Database-wide admission state, never a claim that an external wallet is idle.</summary>
public sealed class WalletPayoutControlState
{
    public int ProtocolVersion { get; set; }
    public long Revision { get; set; }
    public bool Paused { get; set; }
    public long OutstandingBatches { get; set; }

    public static async Task<WalletPayoutControlState> ReadAsync(IConnectionFactory cf, CancellationToken ct)
    {
        using var connection = cf is ICancellableConnectionFactory cancellable
            ? await cancellable.OpenConnectionAsync(ct)
            : await cf.OpenConnectionAsync();
        return await ReadAsync(connection, ct);
    }

    internal static async Task<WalletPayoutControlState> ReadAsync(IDbConnection connection, CancellationToken ct,
        IDbTransaction transaction = null)
    {
        var result = await connection.QuerySingleAsync<WalletPayoutControlState>(new CommandDefinition(
            """
            SELECT protocol_version AS ProtocolVersion, revision, paused,
                (SELECT count(*) FROM miningcore_payout_control.batches WHERE completed IS NULL) AS OutstandingBatches
            FROM miningcore_payout_control.state WHERE id=1
            """, transaction: transaction, commandTimeout: 5, cancellationToken: ct));
        if(result.ProtocolVersion != 2 || result.Revision < 1)
            throw new InvalidOperationException("Wallet payout control state is not supported");
        return result;
    }

    public static async Task PreflightAsync(IConnectionFactory cf, CancellationToken ct)
    {
        try
        {
            using var connection = cf is ICancellableConnectionFactory cancellable
                ? await cancellable.OpenConnectionAsync(ct) : await cf.OpenConnectionAsync();
            await ReadAsync(connection, ct); // Intentionally paused is compatible.
            // Inspect the actual SECURITY DEFINER owners, not the runtime login's
            // view of its own backend. USAGE requires inherited privileges; an
            // assumable but non-inherited membership does not expose backend_start.
            var ownersReady = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT count(*)=2 AND coalesce(bool_and(p.prosecdef AND
                    (r.rolsuper OR pg_has_role(r.oid,'pg_read_all_stats','USAGE'))),false)
                FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_roles r ON r.oid=p.proowner
                WHERE p.oid IN (
                    to_regprocedure('miningcore_payout_control.begin_batch(uuid,uuid,bigint,text,text)'),
                    to_regprocedure('miningcore_payout_control.finish_batch(uuid,uuid,bigint)'))
                """, commandTimeout: 5, cancellationToken: ct));
            if(!ownersReady)
                throw new InvalidOperationException("Wallet control function owners require effective pg_read_all_stats privileges (or superuser) for session binding");
            var ready = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT miningcore_payout_control.runtime_role_safe(current_user::name)
                    AND has_function_privilege(current_user,
                        'miningcore_payout_control.begin_batch(uuid,uuid,bigint,text,text)','EXECUTE')
                    AND has_function_privilege(current_user,
                        'miningcore_payout_control.finish_batch(uuid,uuid,bigint)','EXECUTE')
                """, commandTimeout: 5, cancellationToken: ct));
            if(!ready)
                throw new InvalidOperationException("Missing runtime permissions or unsafe effective control privileges");
        }
        catch(Exception ex) when(!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Wallet payout control preflight failed. Install protocol 2 with add_wallet_payout_control.sql, or upgrade protocol 1 with upgrade_wallet_payout_control_v2.sql, as a separate administrator with the exact runtime role. The control function owners must be superusers or have effective pg_read_all_stats privileges. Verify owner visibility and least-privilege runtime grants before starting payment processing.", ex);
        }
    }
}
