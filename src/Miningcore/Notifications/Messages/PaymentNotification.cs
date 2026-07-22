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
    // Requested/owed batch total before wallet-precision adjustment.
    public decimal Amount { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public decimal? SubmittedAmount { get; set; }
    // Bitcoin-family handlers calculate this as a non-positive truncation adjustment.
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public decimal? PrecisionAdjustment { get; set; }
    // Administrative error detail is consumed in-process and excluded from the public stream.
    [JsonIgnore]
    public string Error { get; set; }

    [JsonConverter(typeof(StringEnumConverter), true)]
    public PaymentNotificationOutcome Outcome { get; set; }

    // Recipient-level reconciliation is administrative. WebSocket clients receive only the
    // aggregate counts and amounts below.
    [JsonIgnore]
    public PayoutReconciliation Reconciliation { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? AcceptedCount => Reconciliation != null
        ? Reconciliation.Accepted?.Length ?? 0
        : Outcome == PaymentNotificationOutcome.Success
            ? RecipientsCount
            : null;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public decimal? AcceptedAmount => Reconciliation != null
        ? Reconciliation.Accepted?.Sum(x => x.Amount) ?? 0
        : Outcome == PaymentNotificationOutcome.Success
            ? Amount
            : null;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? FailedCount => Reconciliation != null
        ? Reconciliation.Failed?.Length ?? 0
        : Outcome == PaymentNotificationOutcome.Failure
            ? RecipientsCount
            : null;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public decimal? FailedAmount => Reconciliation != null
        ? Reconciliation.Failed?.Sum(x => x.Amount) ?? 0
        : Outcome == PaymentNotificationOutcome.Failure
            ? Amount
            : null;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? UncertainCount => Reconciliation != null
        ? Reconciliation.Uncertain?.Length ?? 0
        : null;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public decimal? UncertainAmount => Reconciliation != null
        ? Reconciliation.Uncertain?.Sum(x => x.Amount) ?? 0
        : null;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? NotAttemptedCount => Reconciliation != null
        ? Reconciliation.NotAttempted?.Length ?? 0
        : null;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public decimal? NotAttemptedAmount => Reconciliation != null
        ? Reconciliation.NotAttempted?.Sum(x => x.Amount) ?? 0
        : null;
}
