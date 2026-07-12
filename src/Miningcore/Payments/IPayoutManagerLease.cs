namespace Miningcore.Payments;

/// <summary>
/// Prevents two healthy payout managers from starting concurrently against one database.
/// This is an operational guard, not a fencing token for work already in progress.
/// </summary>
public interface IPayoutManagerLease : IAsyncDisposable
{
    Task<bool> TryAcquireAsync(CancellationToken ct);
    Task EnsureHeldAsync(CancellationToken ct);
}
