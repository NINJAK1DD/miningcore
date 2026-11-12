namespace Miningcore.Payments;

/// <summary>
/// Prevents concurrent payout managers with a durable ownership token that survives
/// database-session or process loss. Ownership is released only after a clean stop.
/// </summary>
public interface IPayoutManagerLease : IAsyncDisposable
{
    string AcquisitionFailure { get; }
    bool RequiresRecovery { get; }
    Task<bool> TryAcquireAsync(CancellationToken ct);
    Task EnsureHeldAsync(CancellationToken ct);
    Task<bool> TryBeginWalletPayoutAsync(CancellationToken ct);
    Task CompleteWalletPayoutAsync(CancellationToken ct);
    void BeginFinancialOperation();
    void CompleteFinancialOperation();
    void MarkFinancialOutcomeUncertain();
}
