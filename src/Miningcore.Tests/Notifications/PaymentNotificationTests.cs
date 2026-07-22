using Miningcore.Api;
using Miningcore.Api.WebSocketNotifications;
using Miningcore.Notifications;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Tests.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Notifications;

public class PaymentNotificationTests
{
    [Fact]
    public void WebSocketSerialization_NormalSuccessUsesOutcomeAwareAggregates()
    {
        var notification = new PaymentNotification("doge-test", null, 10, "DOGE", 4,
            new[] { "txid" }, null, null);
        var payload = SerializePayment(notification);

        Assert.Equal("success", payload.Value<string>("outcome"));
        Assert.Equal(4, payload.Value<int>("acceptedCount"));
        Assert.Equal(10, payload.Value<decimal>("acceptedAmount"));
        Assert.Null(payload["failedCount"]);
        Assert.Null(payload["failedAmount"]);
        Assert.Null(payload["uncertainCount"]);
        Assert.Null(payload["notAttemptedCount"]);
    }

    [Fact]
    public void WebSocketSerialization_NormalFailureUsesOutcomeAwareAggregates()
    {
        var notification = new PaymentNotification("doge-test", "rejected", 10,
            "DOGE", 4, null, null, null);
        var payload = SerializePayment(notification);

        Assert.Equal("failure", payload.Value<string>("outcome"));
        Assert.Equal(4, payload.Value<int>("failedCount"));
        Assert.Equal(10, payload.Value<decimal>("failedAmount"));
        Assert.Null(payload["acceptedCount"]);
        Assert.Null(payload["acceptedAmount"]);
        Assert.Null(payload["uncertainCount"]);
        Assert.Null(payload["notAttemptedCount"]);
    }

    [Fact]
    public void WebSocketSerialization_ExposesOnlySafeReconciliationSummary()
    {
        var notification = new PaymentNotification("doge-test", "rpc secret", 10,
            "DOGE")
        {
            Outcome = PaymentNotificationOutcome.Uncertain,
            SubmittedAmount = 9.9999m,
            PrecisionAdjustment = -0.0001m,
            Reconciliation = new PayoutReconciliation
            {
                Accepted = new[]
                {
                    Entry("DSecretAccepted", 1, "accepted-txid", "accepted detail"),
                },
                Failed = new[]
                {
                    Entry("DSecretFailed", 2, null, "wallet rejection"),
                },
                Uncertain = new[]
                {
                    Entry("DSecretUncertain", 3, "uncertain-txid", "rpc timeout"),
                },
                NotAttempted = new[]
                {
                    Entry("DSecretNotAttempted", 4, null, "cancelled"),
                },
            },
        };
        var payload = SerializePayment(notification);
        var json = payload.ToString(Formatting.None);

        Assert.Equal("payment", payload.Value<string>("type"));
        Assert.Equal("uncertain", payload.Value<string>("outcome"));
        Assert.Equal(10, payload.Value<decimal>("amount"));
        Assert.Equal(9.9999m, payload.Value<decimal>("submittedAmount"));
        Assert.Equal(-0.0001m, payload.Value<decimal>("precisionAdjustment"));
        Assert.Null(payload["roundingAdjustment"]);
        Assert.Equal(1, payload.Value<int>("acceptedCount"));
        Assert.Equal(1, payload.Value<decimal>("acceptedAmount"));
        Assert.Equal(1, payload.Value<int>("failedCount"));
        Assert.Equal(2, payload.Value<decimal>("failedAmount"));
        Assert.Equal(1, payload.Value<int>("uncertainCount"));
        Assert.Equal(3, payload.Value<decimal>("uncertainAmount"));
        Assert.Equal(1, payload.Value<int>("notAttemptedCount"));
        Assert.Equal(4, payload.Value<decimal>("notAttemptedAmount"));
        Assert.Null(payload["reconciliation"]);
        Assert.Null(payload["error"]);
        Assert.DoesNotContain("DSecret", json);
        Assert.DoesNotContain("txid", json);
        Assert.DoesNotContain("rpc secret", json);
        Assert.DoesNotContain("wallet rejection", json);
    }

    [Fact]
    public void EmailFormatting_EncodesEveryDynamicReconciliationField()
    {
        var notification = new PaymentNotification("pool<b>fake</b>&value",
            "reason<b>fake</b>&value", 1, "COIN<b>fake</b>&value")
        {
            Outcome = PaymentNotificationOutcome.Uncertain,
            Reconciliation = new PayoutReconciliation
            {
                Uncertain = new[]
                {
                    Entry("address<b>fake</b>&value", 1,
                        "tx<b>fake</b>&value", "detail<b>fake</b>&value"),
                },
            },
        };

        var rendered = NotificationService.FormatPaymentNotification(notification,
            notification.Symbol, null);

        Assert.Contains("pool&lt;b&gt;fake&lt;/b&gt;&amp;value", rendered.EmailMessage);
        Assert.Contains("COIN&lt;b&gt;fake&lt;/b&gt;&amp;value", rendered.EmailMessage);
        Assert.Contains("address&lt;b&gt;fake&lt;/b&gt;&amp;value", rendered.EmailMessage);
        Assert.Contains("tx&lt;b&gt;fake&lt;/b&gt;&amp;value", rendered.EmailMessage);
        Assert.Contains("detail&lt;b&gt;fake&lt;/b&gt;&amp;value", rendered.EmailMessage);
        Assert.Contains("reason&lt;b&gt;fake&lt;/b&gt;&amp;value", rendered.EmailMessage);
        Assert.DoesNotContain("<b>fake</b>", rendered.EmailMessage);
        Assert.Contains("<br/>", rendered.EmailMessage);
    }

    [Fact]
    public void EmailFormatting_LabelsRequestedAndTruncatedWalletTotals()
    {
        var notification = new PaymentNotification("doge-test", "unknown", 3.58020m,
            "DOGE")
        {
            Outcome = PaymentNotificationOutcome.Uncertain,
            SubmittedAmount = 3.5801m,
            PrecisionAdjustment = -0.00010m,
            Reconciliation = new PayoutReconciliation
            {
                Uncertain = new[]
                {
                    new PayoutReconciliationEntry
                    {
                        Address = "DTestBelow",
                        Amount = 1.23454m,
                        SubmittedAmount = 1.2345m,
                    },
                    new PayoutReconciliationEntry
                    {
                        Address = "DTestAbove",
                        Amount = 2.34566m,
                        SubmittedAmount = 2.3456m,
                    },
                },
            },
        };

        var rendered = NotificationService.FormatPaymentNotification(notification,
            "DOGE", null);

        Assert.Contains("Payout batch totalling 3.58020 DOGE requested",
            rendered.EmailMessage);
        Assert.Contains("Wallet request total across attempted recipients: 3.5801 DOGE",
            rendered.EmailMessage);
        Assert.Contains("precision adjustment: -0.00010 DOGE", rendered.EmailMessage);
        Assert.Contains("1.23454 DOGE to DTestBelow, wallet request 1.2345 DOGE",
            rendered.EmailMessage);
        Assert.Contains("2.34566 DOGE to DTestAbove, wallet request 2.3456 DOGE",
            rendered.EmailMessage);
    }

    [Fact]
    public void FailureFormatting_ReportsWalletRequestWithoutRepeatingAmountOwed()
    {
        var notification = new PaymentNotification("doge-test", "rejected", 3.58020m,
            "DOGE", 2, null, null, null)
        {
            SubmittedAmount = 3.5801m,
            PrecisionAdjustment = -0.00010m,
        };

        var rendered = NotificationService.FormatPaymentNotification(notification,
            "DOGE", null);

        Assert.Contains("Wallet request for 3.5801 DOGE failed", rendered.EmailMessage);
        Assert.Contains("amount owed 3.58020 DOGE", rendered.EmailMessage);
        Assert.Contains("precision adjustment -0.00010 DOGE", rendered.EmailMessage);
        Assert.Equal(1, rendered.EmailMessage.Split("amount owed").Length - 1);
    }

    private static PayoutReconciliationEntry Entry(string address, decimal amount,
        string transactionId, string detail)
    {
        return new PayoutReconciliationEntry
        {
            Address = address,
            Amount = amount,
            TransactionId = transactionId,
            Detail = detail,
        };
    }

    private static JObject SerializePayment(PaymentNotification notification)
    {
        var serializer = JsonSerializer.Create(Globals.JsonSerializerSettings);
        var json = WebSocketNotificationSerializer.Serialize(
            WsNotificationType.Payment, notification, serializer);

        return JObject.Parse(json);
    }
}
