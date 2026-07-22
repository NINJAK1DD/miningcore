namespace Miningcore.Payments;

public record PayoutReconciliationEntry
{
    public string Address { get; init; }
    // Amount owed to the recipient before wallet-precision adjustment.
    public decimal Amount { get; init; }
    // Amount passed to the wallet RPC. Null means submission was not started or the
    // handler cannot determine the submitted amount.
    public decimal? SubmittedAmount { get; init; }
    public string TransactionId { get; init; }
    public string Detail { get; init; }
}

public record PayoutReconciliation
{
    public PayoutReconciliationEntry[] Accepted { get; init; } =
        Array.Empty<PayoutReconciliationEntry>();
    public PayoutReconciliationEntry[] Failed { get; init; } =
        Array.Empty<PayoutReconciliationEntry>();
    public PayoutReconciliationEntry[] Uncertain { get; init; } =
        Array.Empty<PayoutReconciliationEntry>();
    public PayoutReconciliationEntry[] NotAttempted { get; init; } =
        Array.Empty<PayoutReconciliationEntry>();
}
