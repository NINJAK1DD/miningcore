using Dapper;
using Miningcore.Persistence;

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
        using var connection = await cf.OpenConnectionAsync();
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
}
