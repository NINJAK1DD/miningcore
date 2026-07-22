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
    public void WebSocketSerialization_ExposesOnlySafeReconciliationSummary()
    {
        var notification = new PaymentNotification("doge-test", "rpc secret", 10,
            "DOGE")
        {
            Outcome = PaymentNotificationOutcome.Uncertain,
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
        var serializer = JsonSerializer.Create(Globals.JsonSerializerSettings);

        var json = WebSocketNotificationSerializer.Serialize(
            WsNotificationType.Payment, notification, serializer);
        var payload = JObject.Parse(json);

        Assert.Equal("payment", payload.Value<string>("type"));
        Assert.Equal("uncertain", payload.Value<string>("outcome"));
        Assert.Equal(10, payload.Value<decimal>("amount"));
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
}
