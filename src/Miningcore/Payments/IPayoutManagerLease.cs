namespace Miningcore.Payments;

/// <summary>
/// Prevents concurrent payout managers with a durable ownership token that survives
/// database-session or process loss. Ownership is released only after a clean stop.
/// </summary>
public interface IPayoutManagerLease : IAsyncDisposable
{
    Task<bool> TryAcquireAsync(CancellationToken ct);
    Task EnsureHeldAsync(CancellationToken ct);
    void BeginFinancialOperation();
    void CompleteFinancialOperation();
    void MarkFinancialOutcomeUncertain();
}
