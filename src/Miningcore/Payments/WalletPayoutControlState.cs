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

    internal static async Task<WalletPayoutControlState> ReadAsync(IDbConnection connection, CancellationToken ct)
    {
        var result = await connection.QuerySingleAsync<WalletPayoutControlState>(new CommandDefinition(
            """
            SELECT protocol_version AS ProtocolVersion, revision, paused,
                (SELECT count(*) FROM miningcore_payout_control.batches WHERE completed IS NULL) AS OutstandingBatches
            FROM miningcore_payout_control.state WHERE id=1
            """, commandTimeout: 5, cancellationToken: ct));
        if(result.ProtocolVersion != 1 || result.Revision < 1)
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
            throw new InvalidOperationException("Wallet payout control preflight failed. Apply add_wallet_payout_control.sql as a separate administrator with the exact runtime role and verify its least-privilege grants before starting payment processing.", ex);
        }
    }
}
