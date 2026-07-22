using Miningcore.Payments;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

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
    // Administrative error detail is consumed in-process and excluded from the public stream.
    [JsonIgnore]
    public string Error { get; set; }

    [JsonConverter(typeof(StringEnumConverter), true)]
    public PaymentNotificationOutcome Outcome { get; set; }

    // Recipient-level reconciliation is administrative. WebSocket clients receive only the
    // aggregate counts and amounts below.
    [JsonIgnore]
    public PayoutReconciliation Reconciliation { get; set; }

    public int AcceptedCount => Reconciliation?.Accepted?.Length ?? 0;
    public decimal AcceptedAmount => Reconciliation?.Accepted?.Sum(x => x.Amount) ?? 0;
    public int FailedCount => Reconciliation?.Failed?.Length ?? 0;
    public decimal FailedAmount => Reconciliation?.Failed?.Sum(x => x.Amount) ?? 0;
    public int UncertainCount => Reconciliation?.Uncertain?.Length ?? 0;
    public decimal UncertainAmount => Reconciliation?.Uncertain?.Sum(x => x.Amount) ?? 0;
    public int NotAttemptedCount => Reconciliation?.NotAttempted?.Length ?? 0;
    public decimal NotAttemptedAmount =>
        Reconciliation?.NotAttempted?.Sum(x => x.Amount) ?? 0;
}
