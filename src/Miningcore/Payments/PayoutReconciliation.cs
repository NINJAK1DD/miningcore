namespace Miningcore.Payments;

public record PayoutReconciliationEntry
{
    public string Address { get; init; }
    public decimal Amount { get; init; }
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
