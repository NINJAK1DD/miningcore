using Miningcore.Payments;

namespace Miningcore.Notifications.Messages;

public enum PaymentNotificationOutcome
{
    Success,
    Failure,
    Uncertain,
}

public record PaymentNotification
{
    public PaymentNotification(string poolId, string error, decimal amount, string symbol, int recipientsCount, string[] txIds, string[] txExplorerLinks, decimal? txFee)
    {
        PoolId = poolId;
        Error = error;
        Amount = amount;
        RecipientsCount = recipientsCount;
        TxIds = txIds;
        TxFee = txFee;
        Symbol = symbol;
        TxExplorerLinks = txExplorerLinks;
        Outcome = string.IsNullOrEmpty(error)
            ? PaymentNotificationOutcome.Success
            : PaymentNotificationOutcome.Failure;
    }

    public PaymentNotification(string poolId, string error, decimal amount, string symbol) : this(poolId, error, amount, symbol, 0, null, null, null)
    {
    }

    public PaymentNotification()
    {
    }

    public string PoolId { get; set; }
    public decimal? TxFee { get; set; }
    public string[] TxIds { get; set; }
    public string[] TxExplorerLinks { get; set; }
    public string Symbol { get; set; }
    public int RecipientsCount { get; set; }
    public decimal Amount { get; set; }
    public string Error { get; set; }
    public PaymentNotificationOutcome Outcome { get; set; }
    public PayoutReconciliation Reconciliation { get; set; }
}
