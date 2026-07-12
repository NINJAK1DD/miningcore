namespace Miningcore.Payments;

/// <summary>
/// Provides database-backed exclusive ownership of payout and block-reconciliation work.
/// </summary>
public interface IPayoutManagerLease : IAsyncDisposable
{
    Task<bool> TryAcquireAsync(CancellationToken ct);
    Task EnsureHeldAsync(CancellationToken ct);
}
