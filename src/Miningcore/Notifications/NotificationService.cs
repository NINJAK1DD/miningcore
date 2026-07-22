using System.Globalization;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using MailKit.Net.Smtp;
using MimeKit;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Pushover;
using NLog;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Notifications;

public class NotificationService : StartupGatedBackgroundService
{
    public NotificationService(
        ClusterConfig clusterConfig,
        PushoverClient pushoverClient,
        IMessageBus messageBus)
    {
        Contract.RequiresNonNull(clusterConfig);
        Contract.RequiresNonNull(messageBus);

        this.clusterConfig = clusterConfig;
        emailSenderConfig = clusterConfig.Notifications.Email;
        this.messageBus = messageBus;
        this.pushoverClient = pushoverClient;

        poolConfigs = clusterConfig.Pools.ToDictionary(x => x.Id, x => x);

        adminEmail = clusterConfig.Notifications?.Admin?.EmailAddress;
    }

    private readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly ClusterConfig clusterConfig;
    private readonly Dictionary<string, PoolConfig> poolConfigs;
    private readonly string adminEmail;
    private readonly IMessageBus messageBus;
    private readonly EmailSenderConfig emailSenderConfig;
    private readonly PushoverClient pushoverClient;

    public string FormatAmount(decimal amount, string poolId)
    {
        return $"{amount:0.#####} {poolConfigs[poolId].Template.Symbol}";
    }

    private async Task OnAdminNotificationAsync(AdminNotification notification, CancellationToken ct)
    {
        if(!string.IsNullOrEmpty(adminEmail))
            await Guard(()=> SendEmailAsync(adminEmail, notification.Subject, notification.Message, ct), LogGuarded);

        if(clusterConfig.Notifications?.Pushover?.Enabled == true)
            await Guard(()=> pushoverClient.PushMessage(notification.Subject, notification.Message, PushoverMessagePriority.None, ct), LogGuarded);
    }

    private async Task OnBlockFoundNotificationAsync(BlockFoundNotification notification, CancellationToken ct)
    {
        const string subject = "Block Notification";
        var message = $"Pool {notification.PoolId} found block candidate {notification.BlockHeight}";

        if(clusterConfig.Notifications?.Admin?.NotifyBlockFound == true)
        {
            await Guard(() => SendEmailAsync(adminEmail, subject, message, ct), LogGuarded);

            if(clusterConfig.Notifications?.Pushover?.Enabled == true)
                await Guard(() => pushoverClient.PushMessage(subject, message, PushoverMessagePriority.None, ct), LogGuarded);
        }
    }

    private async Task OnPaymentNotificationAsync(PaymentNotification notification, CancellationToken ct)
    {
        var coin = poolConfigs[notification.PoolId].Template;
        var (subject, emailMessage, pushoverMessage, isSuccess) =
            FormatPaymentNotification(notification,
            coin.Symbol, coin.ExplorerTxLink);

        if(isSuccess && clusterConfig.Notifications?.Admin?.NotifyPaymentSuccess != true)
            return;

        await Guard(()=> SendEmailAsync(adminEmail, subject, emailMessage, ct), LogGuarded);

        if(clusterConfig.Notifications?.Pushover?.Enabled == true)
            await Guard(()=> pushoverClient.PushMessage(subject, pushoverMessage,
                PushoverMessagePriority.None, ct), LogGuarded);
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
            var message = $"Paid {FormatCoinAmount(notification.Amount, symbol)} from pool " +
                $"{notification.PoolId} to {notification.RecipientsCount} recipients in " +
                $"transaction(s) {string.Join(", ", txLinks)}";

            return (subject, message, TruncateForPushover(message), true);
        }

        if(outcome == PaymentNotificationOutcome.Uncertain)
        {
            const string subject = "Payout Outcome Uncertain Notification";
            var sections = new List<string>
            {
                $"Payout batch totalling {FormatExactAmount(notification.Amount, symbol)} from " +
                $"pool {notification.PoolId} has an uncertain outcome and requires " +
                "reconciliation.",
            };

            AppendReconciliationSection(sections, "Accepted and persisted",
                notification.Reconciliation?.Accepted, symbol);
            AppendReconciliationSection(sections, "Conclusively failed",
                notification.Reconciliation?.Failed, symbol);
            AppendReconciliationSection(sections, "Uncertain",
                notification.Reconciliation?.Uncertain, symbol);
            AppendReconciliationSection(sections, "Not attempted",
                notification.Reconciliation?.NotAttempted, symbol);

            if(!string.IsNullOrWhiteSpace(notification.Error))
                sections.Add($"Reason: {notification.Error}");

            var pushoverSections = new List<string>
            {
                $"Payout batch {FormatExactAmount(notification.Amount, symbol)} from pool " +
                $"{notification.PoolId} is uncertain; reconcile before releasing ownership.",
            };
            AppendPushoverReconciliationSummary(pushoverSections,
                "Accepted/persisted", notification.Reconciliation?.Accepted, symbol);
            AppendPushoverReconciliationSummary(pushoverSections,
                "Failed", notification.Reconciliation?.Failed, symbol);
            AppendPushoverReconciliationSummary(pushoverSections,
                "Uncertain", notification.Reconciliation?.Uncertain, symbol);
            AppendPushoverReconciliationSummary(pushoverSections,
                "Not attempted", notification.Reconciliation?.NotAttempted, symbol);
            pushoverSections.Add("See email and logs for recipient, transaction, and error details.");

            return (subject, string.Join("<br/>", sections),
                TruncateForPushover(string.Join("\n", pushoverSections)), false);
        }

        var failureMessage = $"Failed to pay out {notification.Amount} {symbol} from pool " +
            $"{notification.PoolId}: {notification.Error}";
        return ("Payout Failure Notification", failureMessage,
            TruncateForPushover(failureMessage), false);
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
                $"{FormatExactAmount(x.Amount, symbol)} to {x.Address}",
            };

            if(!string.IsNullOrWhiteSpace(x.TransactionId))
                parts.Add($"transaction {x.TransactionId}");

            if(!string.IsNullOrWhiteSpace(x.Detail))
                parts.Add(x.Detail);

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

    private static string FormatCoinAmount(decimal amount, string symbol)
    {
        return $"{amount:0.#####} {symbol}";
    }

    private static string FormatExactAmount(decimal amount, string symbol)
    {
        return $"{amount.ToString(CultureInfo.InvariantCulture)} {symbol}";
    }

    internal static string TruncateForPushover(string message)
    {
        const int maxCharacters = 1024;
        var characters = message.EnumerateRunes().ToArray();

        if(characters.Length <= maxCharacters)
            return message;

        return string.Concat(characters.Take(maxCharacters - 1)
            .Select(x => x.ToString())) + "…";
    }

    public async Task SendEmailAsync(string recipient, string subject, string body, CancellationToken ct)
    {
        logger.Info(() => $"Sending '{subject.ToLower()}' email to {recipient}");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(emailSenderConfig.FromName, emailSenderConfig.FromAddress));
        message.To.Add(new MailboxAddress("", recipient));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = body };

        using(var client = new SmtpClient())
        {
            await client.ConnectAsync(emailSenderConfig.Host, emailSenderConfig.Port, cancellationToken: ct);
            await client.AuthenticateAsync(emailSenderConfig.User, emailSenderConfig.Password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }

        logger.Info(() => $"Sent '{subject.ToLower()}' email to {recipient}");
    }

    private void LogGuarded(Exception ex)
    {
        logger.Error(ex);
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

            if(clusterConfig.Notifications?.Admin?.Enabled == true)
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
