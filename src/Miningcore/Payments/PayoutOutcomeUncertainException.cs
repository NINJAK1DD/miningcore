namespace Miningcore.Payments;

/// <summary>
/// Indicates that a wallet submission may have been accepted even though Miningcore did
/// not receive a conclusive result. Durable payout ownership must remain held until an
/// operator reconciles wallet history.
/// </summary>
public class PayoutOutcomeUncertainException : Exception
{
    public PayoutOutcomeUncertainException(string message) : base(message)
    {
    }

    public PayoutOutcomeUncertainException(string message, Exception innerException) :
        base(message, innerException)
    {
    }

    public PayoutOutcomeUncertainException(string message, Exception innerException,
        PayoutReconciliation reconciliation) : base(message, innerException)
    {
        Reconciliation = reconciliation;
    }

    public PayoutReconciliation Reconciliation { get; }
}
