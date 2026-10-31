using Miningcore.Rpc;
using System.Net;
using System.Net.Security;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Pushover;
using NLog;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Notifications;

public class NotificationService : StartupGatedBackgroundService,
    ICriticalNotificationSender
{
    internal static readonly TimeSpan OrdinaryTransportTimeout =
        TimeSpan.FromSeconds(15);

    public NotificationService(
        ClusterConfig clusterConfig,
        PushoverClient pushoverClient,
        IMessageBus messageBus)
    {
        Contract.RequiresNonNull(clusterConfig);
        Contract.RequiresNonNull(messageBus);

        this.clusterConfig = clusterConfig;
        this.messageBus = messageBus;
        this.pushoverClient = pushoverClient;
        activation = new NotificationActivation(clusterConfig.Notifications);

        poolConfigs = clusterConfig.Pools.ToDictionary(x => x.Id, x => x);
    }

    private readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly ClusterConfig clusterConfig;
    private readonly Dictionary<string, PoolConfig> poolConfigs;
    private readonly NotificationActivation activation;
    private readonly IMessageBus messageBus;
    private readonly PushoverClient pushoverClient;

    public string FormatAmount(decimal amount, string poolId)
    {
        return $"{amount:0.#####} {poolConfigs[poolId].Template.Symbol}";
    }

    private async Task OnAdminNotificationAsync(AdminNotification notification, CancellationToken ct)
    {
        await SendOrdinaryAsync(notification.Subject, notification.Message,
            notification.Message, ct);
    }

    public async Task SendCriticalAdminNotificationAsync(AdminNotification notification,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if(!activation.AdminEnabled)
            return;

        if(!activation.EmailEnabled && !activation.PushoverEnabled)
            throw new InvalidOperationException(
                "Enabled critical notifications have no configured email recipient or Pushover transport");

        // Start both selected transports before awaiting either one. A slow SMTP
        // connection must not consume the caller's entire durability-alert timeout
        // before Pushover has a chance to send.
        var attempts = new List<Task<Exception>>();
        if(activation.EmailEnabled)
            attempts.Add(AttemptCriticalTransportAsync(
                () => SendEmailAsync(activation.EmailRecipient, notification.Subject,
                    notification.Message, ct),
                "Critical administrative email delivery failed"));

        if(activation.PushoverEnabled)
            attempts.Add(AttemptCriticalTransportAsync(async () =>
            {
                activation.ValidatePushoverProvider();
                await pushoverClient.PushMessage(notification.Subject,
                    notification.Message, PushoverMessagePriority.None, ct);
            }, "Critical administrative Pushover delivery failed"));

        var errors = (await Task.WhenAll(attempts))
            .Where(error => error != null).ToArray();
        if(errors.Length > 0)
            throw new AggregateException(
                "One or more critical administrative notification transports failed",
                errors);
    }

    private static async Task<Exception> AttemptCriticalTransportAsync(
        Func<Task> send, string failureMessage)
    {
        // Do not let synchronous work in one transport delay the other one's start.
        await Task.Yield();
        try
        {
            await send();
            return null;
        }
        catch(Exception ex)
        {
            return new IOException(failureMessage, ex);
        }
    }

    private async Task SendOrdinaryAsync(string subject, string emailMessage,
        string pushoverMessage, CancellationToken ct)
    {
        var attempts = new List<Task>();
        if(activation.EmailEnabled)
            attempts.Add(AttemptOrdinaryTransportAsync(token =>
                SendEmailAsync(activation.EmailRecipient, subject, emailMessage,
                    token), ct));
        if(activation.PushoverEnabled)
            attempts.Add(AttemptOrdinaryTransportAsync(token =>
                pushoverClient.PushMessage(subject, pushoverMessage,
                    PushoverMessagePriority.None, token), ct));
        await Task.WhenAll(attempts);
    }

    private async Task AttemptOrdinaryTransportAsync(
        Func<CancellationToken, Task> send, CancellationToken ct)
    {
        await Task.Yield();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OrdinaryTransportTimeout);
        await Guard(async () => await send(timeout.Token).WaitAsync(timeout.Token),
            LogGuarded);
    }

    private async Task OnBlockFoundNotificationAsync(BlockFoundNotification notification, CancellationToken ct)
    {
        const string subject = "Block Notification";
        var message = $"Pool {notification.PoolId} found block candidate {notification.BlockHeight}";

        if(clusterConfig.Notifications?.Admin?.NotifyBlockFound == true)
            await SendOrdinaryAsync(subject, message, message, ct);
    }

    private async Task OnPaymentNotificationAsync(PaymentNotification notification, CancellationToken ct)
    {
        var coin = poolConfigs[notification.PoolId].Template;
        var (subject, emailMessage, pushoverMessage, isSuccess) =
            FormatPaymentNotification(notification,
            coin.Symbol, coin.ExplorerTxLink);

        if(isSuccess && clusterConfig.Notifications?.Admin?.NotifyPaymentSuccess != true)
            return;

        await SendOrdinaryAsync(subject, emailMessage, pushoverMessage, ct);
    }

    internal static (string Subject, string EmailMessage, string PushoverMessage,
        bool IsSuccess)
        FormatPaymentNotification(PaymentNotification notification, string symbol,
        string explorerTxLink)
    {
        var outcome = notification.Outcome;

        // Preserve the legacy object-initializer contract where Error alone represented failure.
        if(outcome == PaymentNotificationOutcome.Success &&
            !string.IsNullOrEmpty(notification.Error))
            outcome = PaymentNotificationOutcome.Failure;

        if(outcome == PaymentNotificationOutcome.Success)
        {
            var txIds = notification.TxIds ?? Array.Empty<string>();
            var txLinks = string.IsNullOrEmpty(explorerTxLink)
                ? Array.Empty<string>()
                : txIds.Select(txHash => string.Format(explorerTxLink, txHash)).ToArray();
            const string subject = "Payout Success Notification";
            var emailAmount = FormatHtmlSuccessfulAmount(notification, symbol);
            var pushoverAmount = FormatSuccessfulAmount(notification, symbol);
            var emailMessage = emailAmount + " " +
                $"from pool {HtmlEncode(notification.PoolId)} to " +
                $"{notification.RecipientsCount} recipients in transaction(s) " +
                $"{string.Join(", ", txLinks.Select(HtmlEncode))}";
            var pushoverMessage = pushoverAmount + " " +
                $"from pool {notification.PoolId} to {notification.RecipientsCount} " +
                $"recipients in transaction(s) {string.Join(", ", txLinks)}";

            return (subject, emailMessage, TruncateForPushover(pushoverMessage), true);
        }

        if(outcome == PaymentNotificationOutcome.Uncertain)
        {
            const string subject = "Payout Outcome Uncertain Notification";
            var sections = new List<string>
            {
                $"Payout batch totalling {FormatExactHtmlAmount(notification.Amount, symbol)} requested " +
                $"from pool {HtmlEncode(notification.PoolId)} has an uncertain outcome and requires " +
                "reconciliation.",
            };

            AppendSubmittedAmountSummary(sections, notification, symbol, true);

            AppendReconciliationSection(sections, "Accepted and persisted",
                notification.Reconciliation?.Accepted, symbol);
            AppendReconciliationSection(sections, "Conclusively failed",
                notification.Reconciliation?.Failed, symbol);
            AppendReconciliationSection(sections, "Uncertain",
                notification.Reconciliation?.Uncertain, symbol);
            AppendReconciliationSection(sections, "Not attempted",
                notification.Reconciliation?.NotAttempted, symbol);

            if(!string.IsNullOrWhiteSpace(notification.Error))
                sections.Add(RpcConsumerDiagnostics.WithheldError);
            sections.Add("Reconcile wallet history before retrying or releasing ownership.");
            if(notification.FailureDiagnostic != null)
                sections.Add(HtmlEncode(notification.FailureDiagnostic.Summary));

            var pushoverSections = new List<string>
            {
                $"Payout batch {FormatExactAmount(notification.Amount, symbol)} requested from pool " +
                $"{notification.PoolId} is uncertain; reconcile before releasing ownership.",
            };
            AppendSubmittedAmountSummary(pushoverSections, notification, symbol, false);
            AppendPushoverReconciliationSummary(pushoverSections,
                "Accepted/persisted", notification.Reconciliation?.Accepted, symbol);
            AppendPushoverReconciliationSummary(pushoverSections,
                "Failed", notification.Reconciliation?.Failed, symbol);
            AppendPushoverReconciliationSummary(pushoverSections,
                "Uncertain", notification.Reconciliation?.Uncertain, symbol);
            AppendPushoverReconciliationSummary(pushoverSections,
                "Not attempted", notification.Reconciliation?.NotAttempted, symbol);
            pushoverSections.Add("See email for the recipient/transaction reconciliation summary. Consult retained private reconciliation evidence and wallet history for withheld error details.");
            if(notification.FailureDiagnostic != null)
                pushoverSections.Add(notification.FailureDiagnostic.Summary);

            return (subject, string.Join("<br/>", sections),
                TruncateForPushover(string.Join("\n", pushoverSections)), false);
        }

        // Conclusive failures do not carry the uncertain-outcome reconciliation
        // requirement. Keep both channels credential-safe without implying a held lease.
        const string conclusiveFailureGuidance =
            "Payout failed conclusively; sensitive error detail is withheld. " +
            "Check the operation diagnostics and correct the cause before retrying.";
        var failureSummary = notification.FailureDiagnostic?.Summary;
        var emailFailureMessage = FormatHtmlFailedAmount(notification, symbol) + " " +
            $"from pool {HtmlEncode(notification.PoolId)}: " +
            conclusiveFailureGuidance + (failureSummary == null ? string.Empty : " " + HtmlEncode(failureSummary));
        var pushoverFailureMessage = FormatFailedAmount(notification, symbol) + " " +
            $"from pool {notification.PoolId}: {conclusiveFailureGuidance}" +
            (failureSummary == null ? string.Empty : " " + failureSummary);
        return ("Payout Failure Notification", emailFailureMessage,
            TruncateForPushover(pushoverFailureMessage), false);
    }

    private static void AppendReconciliationSection(List<string> sections, string heading,
        PayoutReconciliationEntry[] entries, string symbol)
    {
        if(entries == null || entries.Length == 0)
            return;

        var details = entries.Select(x =>
        {
            var parts = new List<string>
            {
                $"{FormatExactHtmlAmount(x.Amount, symbol)} to {HtmlEncode(x.Address)}",
            };

            if(x.SubmittedAmount.HasValue && x.SubmittedAmount.Value != x.Amount)
            {
                var adjustment = x.SubmittedAmount.Value - x.Amount;
                parts.Add($"wallet request " +
                    $"{FormatExactHtmlAmount(x.SubmittedAmount.Value, symbol)} " +
                    $"(precision adjustment {FormatSignedExactHtmlAmount(adjustment, symbol)})");
            }

            var transactionIds = (x.TransactionIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
            if(transactionIds.Length == 0 &&
                !string.IsNullOrWhiteSpace(x.TransactionId))
                transactionIds = new[] { x.TransactionId };

            if(transactionIds.Length == 1)
                parts.Add($"transaction {HtmlEncode(RpcConsumerDiagnostics.TransactionId(transactionIds[0]))}");
            else if(transactionIds.Length > 1)
            {
                var transactionDetail = $"transactions " +
                    string.Join(", ", transactionIds.Select(RpcConsumerDiagnostics.TransactionId).Select(HtmlEncode));
                if(!string.IsNullOrWhiteSpace(x.TransactionId))
                    transactionDetail += $" (canonical {HtmlEncode(RpcConsumerDiagnostics.TransactionId(x.TransactionId))})";
                parts.Add(transactionDetail);
            }

            if(!string.IsNullOrWhiteSpace(x.Detail))
                parts.Add("Sensitive reconciliation detail withheld; retain the original evidence for manual reconciliation.");

            return string.Join(", ", parts);
        });

        sections.Add($"{heading}: {string.Join("; ", details)}");
    }

    private static void AppendPushoverReconciliationSummary(List<string> sections,
        string heading, PayoutReconciliationEntry[] entries, string symbol)
    {
        if(entries == null || entries.Length == 0)
            return;

        sections.Add($"{heading}: {FormatExactAmount(entries.Sum(x => x.Amount), symbol)} " +
            $"({entries.Length} recipient(s))");
    }

    private static void AppendSubmittedAmountSummary(List<string> sections,
        PaymentNotification notification, string symbol, bool html)
    {
        if(!notification.SubmittedAmount.HasValue)
            return;

        // A zero adjustment means the wallet request equals the attempted amount owed. Repeating
        // that total adds no reconciliation information, so only adjusted requests get a summary.
        if(notification.PrecisionAdjustment.GetValueOrDefault() == 0)
            return;

        var adjustment = notification.PrecisionAdjustment ?? 0;
        var amount = html
            ? FormatExactHtmlAmount(notification.SubmittedAmount.Value, symbol)
            : FormatExactAmount(notification.SubmittedAmount.Value, symbol);
        var formattedAdjustment = html
            ? FormatSignedExactHtmlAmount(adjustment, symbol)
            : FormatSignedExactAmount(adjustment, symbol);

        sections.Add($"Wallet request total across attempted recipients: {amount}; " +
            $"precision adjustment: {formattedAdjustment}.");
    }

    private static string FormatHtmlSuccessfulAmount(PaymentNotification notification,
        string symbol)
    {
        if(!notification.SubmittedAmount.HasValue)
            return $"Paid {FormatExactHtmlAmount(notification.Amount, symbol)}";

        return $"Wallet request submitted for " +
            $"{FormatExactHtmlAmount(notification.SubmittedAmount.Value, symbol)}" +
            FormatHtmlPrecisionDetail(notification, symbol);
    }

    private static string FormatSuccessfulAmount(PaymentNotification notification,
        string symbol)
    {
        if(!notification.SubmittedAmount.HasValue)
            return $"Paid {FormatExactAmount(notification.Amount, symbol)}";

        return $"Wallet request submitted for " +
            $"{FormatExactAmount(notification.SubmittedAmount.Value, symbol)}" +
            FormatPrecisionDetail(notification, symbol);
    }

    private static string FormatHtmlFailedAmount(PaymentNotification notification,
        string symbol)
    {
        if(!notification.SubmittedAmount.HasValue)
            return $"Failed to pay out " +
                $"{FormatExactHtmlAmount(notification.Amount, symbol)}";

        return $"Wallet request for " +
            $"{FormatExactHtmlAmount(notification.SubmittedAmount.Value, symbol)} failed" +
            FormatHtmlPrecisionDetail(notification, symbol);
    }

    private static string FormatFailedAmount(PaymentNotification notification,
        string symbol)
    {
        if(!notification.SubmittedAmount.HasValue)
            return $"Failed to pay out {FormatExactAmount(notification.Amount, symbol)}";

        return $"Wallet request for " +
            $"{FormatExactAmount(notification.SubmittedAmount.Value, symbol)} failed" +
            FormatPrecisionDetail(notification, symbol);
    }

    private static string FormatHtmlPrecisionDetail(PaymentNotification notification,
        string symbol)
    {
        if(notification.SubmittedAmount.Value == notification.Amount)
            return string.Empty;

        return $" (amount owed {FormatExactHtmlAmount(notification.Amount, symbol)}; " +
            $"precision adjustment " +
            $"{FormatSignedExactHtmlAmount(notification.PrecisionAdjustment ?? 0, symbol)})";
    }

    private static string FormatPrecisionDetail(PaymentNotification notification,
        string symbol)
    {
        if(notification.SubmittedAmount.Value == notification.Amount)
            return string.Empty;

        return $" (amount owed {FormatExactAmount(notification.Amount, symbol)}; " +
            $"precision adjustment " +
            $"{FormatSignedExactAmount(notification.PrecisionAdjustment ?? 0, symbol)})";
    }

    private static string FormatExactAmount(decimal amount, string symbol)
    {
        return $"{PayoutAmountFormatter.FormatExact(amount)} {symbol}";
    }

    private static string FormatSignedExactAmount(decimal amount, string symbol)
    {
        var sign = amount > 0 ? "+" : string.Empty;
        return $"{sign}{FormatExactAmount(amount, symbol)}";
    }

    private static string FormatExactHtmlAmount(decimal amount, string symbol)
    {
        return $"{PayoutAmountFormatter.FormatExact(amount)} {HtmlEncode(symbol)}";
    }

    private static string FormatSignedExactHtmlAmount(decimal amount, string symbol)
    {
        var sign = amount > 0 ? "+" : string.Empty;
        return $"{sign}{FormatExactHtmlAmount(amount, symbol)}";
    }

    private static string HtmlEncode(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    internal static string TruncateForPushover(string message)
    {
        return PushoverMessagePolicy.Message(message);
    }

    private static readonly RemoteCertificateValidationCallback StrictCertificateValidation =
        (_, _, _, errors) => SmtpTransportPolicy.CertificateIsValid(errors);

    public Task SendEmailAsync(string recipient, string subject, string body, CancellationToken ct) =>
        SendEmailAsync(recipient, subject, body, ct, StrictCertificateValidation);

    // The internal overload permits tests to use an isolated certificate trust root.
    // Every production caller uses the strict platform validation callback above.
    internal async Task SendEmailAsync(string recipient, string subject, string body,
        CancellationToken ct, RemoteCertificateValidationCallback certificateValidation)
    {
        ArgumentNullException.ThrowIfNull(certificateValidation);
        // The critical-sender singleton is separate from the hosted instance under the
        // current Autofac registrations, even in normal operation. Recovery also resolves
        // it without hosting. Delivery cannot rely on the hosted startup validation.
        NotificationActivation.RequireAddress(recipient,
            "notifications.admin.emailAddress");
        // Validate and send from one consistent copy of the configuration.
        var (email, socketOptions) = activation.ValidateEmailProvider();
        logger.Info(() => $"Sending '{subject.ToLower()}' email to {recipient}");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(email.FromName, email.FromAddress));
        message.To.Add(new MailboxAddress("", recipient));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = body };

        using(var client = new SmtpClient())
        {
            client.CheckCertificateRevocation = true;
            client.ServerCertificateValidationCallback = certificateValidation;
            await client.ConnectAsync(email.Host, email.Port,
                socketOptions, ct);
            if(socketOptions != SecureSocketOptions.None && !client.IsSecure)
                throw new InvalidOperationException("SMTP TLS negotiation did not complete");
            if(socketOptions != SecureSocketOptions.None && !string.IsNullOrEmpty(email.User))
                await client.AuthenticateAsync(email.User, email.Password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }

        logger.Info(() => $"Sent '{subject.ToLower()}' email to {recipient}");
    }

    private void LogGuarded(Exception ex)
    {
        RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "NotificationService.LogGuarded", failure: ex);
    }

    private IObservable<IObservable<Unit>> Subscribe<T>(Func<T, CancellationToken, Task> handler, CancellationToken ct)
    {
        return messageBus.Listen<T>()
            .Select(msg => Observable.FromAsync(() =>
                Guard(()=> handler(msg, ct), LogGuarded)));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            var obs = new List<IObservable<IObservable<Unit>>>();

            // Also validate the actual hosted instance: it is distinct from the
            // Autofac critical sender and can be constructed without Program.ValidateConfig.
            var validationErrors = activation.GetValidationErrors();
            if(validationErrors.Count > 0)
                throw new PoolStartupException(string.Join("; ", validationErrors));

            if(clusterConfig.Notifications?.Enabled == true &&
                !activation.AdminEnabled)
                logger.Warn("notifications.enabled is deprecated and ignored; notifications.admin.enabled is false, so administrative alerts are inactive");
            if(activation.AdminEnabled)
            {
                obs.Add(Subscribe<AdminNotification>(OnAdminNotificationAsync, ct));
                obs.Add(Subscribe<BlockFoundNotification>(OnBlockFoundNotificationAsync, ct));
                obs.Add(Subscribe<PaymentNotification>(OnPaymentNotificationAsync, ct));
            }

            if(obs.Count > 0)
            {
                var processing = obs
                    .Merge()
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Concat()
                    .ToTask(ct);

                SignalStartupReady();
                await processing;
            }

            else
                SignalStartupReady();
        }

        catch(Exception ex)
        {
            SignalStartupFailure(ex);
            throw;
        }
    }
}
