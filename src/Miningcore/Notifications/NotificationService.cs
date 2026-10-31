using Miningcore.Rpc;
using System.Net;
using System.Net.Security;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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

namespace Miningcore.Notifications;

public class NotificationService : StartupGatedBackgroundService,
    ICriticalNotificationSender
{
    internal TimeSpan OrdinaryTransportTimeout { get; set; } =
        TimeSpan.FromSeconds(15);

    public NotificationService(
        ClusterConfig clusterConfig,
        PushoverClient pushoverClient,
        IMessageBus messageBus) : this(clusterConfig, pushoverClient, messageBus,
        LogManager.GetCurrentClassLogger())
    {
    }

    internal NotificationService(ClusterConfig clusterConfig,
        PushoverClient pushoverClient, IMessageBus messageBus, ILogger logger)
    {
        Contract.RequiresNonNull(clusterConfig);
        Contract.RequiresNonNull(messageBus);
        Contract.RequiresNonNull(logger);

        this.clusterConfig = clusterConfig;
        this.messageBus = messageBus;
        this.pushoverClient = pushoverClient;
        this.logger = logger;
        activation = new NotificationActivation(clusterConfig.Notifications);

        poolConfigs = clusterConfig.Pools.ToDictionary(x => x.Id, x => x);
    }

    private readonly ILogger logger;
    private readonly ClusterConfig clusterConfig;
    private readonly Dictionary<string, PoolConfig> poolConfigs;
    private readonly NotificationActivation activation;
    private readonly IMessageBus messageBus;
    private readonly PushoverClient pushoverClient;
    internal Action<PaymentNotification> BeforePaymentFormat { get; set; }

    public string FormatAmount(decimal amount, string poolId)
    {
        return $"{amount:0.#####} {poolConfigs[poolId].Template.Symbol}";
    }

    private sealed record OrdinaryNotification(string Subject, string EmailMessage,
        string PushoverMessage, bool FinancialIncident = false);
    private sealed record OrdinaryEnvelope(Func<OrdinaryNotification> Format,
        bool FinancialIncident);

    // Cap both the scheduler handoff and each transport's serial queue.
    internal int MaxPendingOrdinaryNotifications { get; set; } = 256;
    private int pendingFormatting;
    private int pendingEmail;
    private int pendingPushover;
    private long droppedFormatting;
    private long droppedEmail;
    private long droppedPushover;
    internal int PendingFormatting => Volatile.Read(ref pendingFormatting);
    internal int PendingEmail => Volatile.Read(ref pendingEmail);
    internal int PendingPushover => Volatile.Read(ref pendingPushover);
    internal long DroppedFormatting => Volatile.Read(ref droppedFormatting);
    internal long DroppedEmail => Volatile.Read(ref droppedEmail);
    internal long DroppedPushover => Volatile.Read(ref droppedPushover);

    private static OrdinaryNotification FormatAdminNotification(
        AdminNotification notification) =>
        new(notification.Subject, notification.Message, notification.Message);

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
                    notification.Message, PushoverMessagePriority.None, true, ct);
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

    private async Task AttemptOrdinaryTransportAsync(string transport,
        Func<CancellationToken, Task> send, CancellationToken ct)
    {
        await Task.Yield();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            timeout.CancelAfter(OrdinaryTransportTimeout);
            await send(timeout.Token).WaitAsync(timeout.Token);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            // Host shutdown is expected and must not produce a delivery error.
        }
        catch(OperationCanceledException ex) when(timeout.IsCancellationRequested)
        {
            var failure = new TimeoutException(
                $"Ordinary {transport} delivery exceeded {OrdinaryTransportTimeout.TotalSeconds:g} s", ex);
            logger.Error("Ordinary {0} delivery exceeded {1:g} s ({2})",
                transport, OrdinaryTransportTimeout.TotalSeconds,
                RpcConsumerDiagnostics.Failure(failure));
        }
        catch(Exception ex)
        {
            logger.Error("Ordinary {0} delivery failed ({1})", transport,
                RpcConsumerDiagnostics.Failure(ex));
        }
    }

    private OrdinaryNotification FormatBlockFoundNotification(
        BlockFoundNotification notification)
    {
        if(clusterConfig.Notifications?.Admin?.NotifyBlockFound != true)
            return null;

        const string subject = "Block Notification";
        var message = $"Pool {notification.PoolId} found block candidate {notification.BlockHeight}";
        return new OrdinaryNotification(subject, message, message);
    }

    private OrdinaryNotification PreparePaymentNotification(
        PaymentNotification notification)
    {
        BeforePaymentFormat?.Invoke(notification);
        var coin = poolConfigs[notification.PoolId].Template;
        var (subject, emailMessage, pushoverMessage, isSuccess) =
            FormatPaymentNotification(notification,
            coin.Symbol, coin.ExplorerTxLink, activation.EmailEnabled);

        if(isSuccess && clusterConfig.Notifications?.Admin?.NotifyPaymentSuccess != true)
            return null;

        return new OrdinaryNotification(subject, emailMessage, pushoverMessage,
            !isSuccess);
    }

    internal static (string Subject, string EmailMessage, string PushoverMessage,
        bool IsSuccess)
        FormatPaymentNotification(PaymentNotification notification, string symbol,
            string explorerTxLink, bool emailSelected = false)
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

            return (subject, emailMessage, pushoverMessage, true);
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
            if(notification.FailureDiagnostic != null)
                pushoverSections.Add(notification.FailureDiagnostic.Summary);
            pushoverSections.Add("Consult retained private payout reconciliation evidence and wallet history for per-recipient details and withheld error information.");
            if(emailSelected)
                pushoverSections.Add("If the separate email alert was delivered, it includes the per-recipient summary.");

            return (subject, string.Join("<br/>", sections),
                string.Join("\n", pushoverSections), false);
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
            pushoverFailureMessage, false);
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

    private bool TryReserve(ref int pending, ref long dropped,
        string queue, bool financialIncident)
    {
        while(true)
        {
            var current = Volatile.Read(ref pending);
            if(current >= Math.Max(1, MaxPendingOrdinaryNotifications))
            {
                var total = Interlocked.Increment(ref dropped);
                if(financialIncident)
                    logger.Error("Ordinary {0} queue full; payout failure or uncertain outcome not queued; dropped count {1}",
                        queue, total);
                else if(total == 1 || total % 100 == 0)
                    logger.Warn("Ordinary {0} queue full; nonfinancial alert omitted; dropped count {1}",
                        queue, total);
                return false;
            }

            if(Interlocked.CompareExchange(ref pending, current + 1, current) == current)
                return true;
        }
    }

    private IObservable<OrdinaryEnvelope> ListenForFormatting<T>(
        Func<T, OrdinaryNotification> format)
    {
        return messageBus.Listen<T>()
            // Only capture the event on the publisher's scheduler. Formatting
            // runs after ObserveOn in ExecuteAsync.
            .Select(msg => new OrdinaryEnvelope(() => format(msg),
                msg is PaymentNotification payout &&
                (payout.Outcome != PaymentNotificationOutcome.Success ||
                    !string.IsNullOrEmpty(payout.Error))))
            .Where(envelope => TryReserve(ref pendingFormatting,
                ref droppedFormatting, "formatting", envelope.FinancialIncident));
    }

    private OrdinaryNotification FormatSafely(
        OrdinaryEnvelope envelope, CancellationToken ct)
    {
        try
        {
            return envelope.Format();
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            return null;
        }
        catch(Exception ex)
        {
            LogGuarded(ex);
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref pendingFormatting);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
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
                // Both channels see the same ordered events, but each drains its
                // own serial queue. A stalled email cannot hold later pushes.
                var events = new[]
                    {
                        ListenForFormatting<AdminNotification>(FormatAdminNotification),
                        ListenForFormatting<BlockFoundNotification>(FormatBlockFoundNotification),
                        ListenForFormatting<PaymentNotification>(PreparePaymentNotification),
                    }
                    .Merge()
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Select(envelope => FormatSafely(envelope, ct))
                    .Where(notification => notification != null)
                    .Publish();

                var channels = new List<IObservable<Unit>>();
                if(activation.EmailEnabled)
                    channels.Add(events
                        .Where(notification => TryReserve(ref pendingEmail,
                            ref droppedEmail, "email", notification.FinancialIncident))
                        .Select(notification => Observable.FromAsync(async () =>
                        {
                            try
                            {
                                await AttemptOrdinaryTransportAsync("email", token =>
                                    SendEmailAsync(activation.EmailRecipient,
                                        notification.Subject, notification.EmailMessage, token), ct);
                            }
                            finally
                            {
                                Interlocked.Decrement(ref pendingEmail);
                            }
                        }))
                        .Concat());
                if(activation.PushoverEnabled)
                    channels.Add(events
                        .Where(notification => TryReserve(ref pendingPushover,
                            ref droppedPushover, "Pushover", notification.FinancialIncident))
                        .Select(notification => Observable.FromAsync(async () =>
                        {
                            try
                            {
                                await AttemptOrdinaryTransportAsync("Pushover", token =>
                                    pushoverClient.PushMessage(notification.Subject,
                                        notification.PushoverMessage,
                                        PushoverMessagePriority.None, token), ct);
                            }
                            finally
                            {
                                Interlocked.Decrement(ref pendingPushover);
                            }
                        }))
                        .Concat());

                var processing = channels.Merge().ToTask(ct);
                using var connection = events.Connect();

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
