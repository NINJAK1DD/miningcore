using MailKit.Security;
using MimeKit;

namespace Miningcore.Configuration;

// The legacy notifications.enabled value is deliberately ignored. Admin.Enabled is the
// activation switch; a nonblank recipient opts into email and Pushover.Enabled opts into push.
// Keep this policy shared by validation, the hosted instance and the unhosted critical sender.
internal sealed class NotificationActivation
{
    public NotificationActivation(NotificationsConfig config)
    {
        this.config = config;
        AdminEnabled = config?.Admin?.Enabled == true;
        EmailRecipient = config?.Admin?.EmailAddress;
        EmailEnabled = AdminEnabled && !string.IsNullOrWhiteSpace(EmailRecipient);
        PushoverEnabled = AdminEnabled && config.Pushover?.Enabled == true;
    }

    private readonly NotificationsConfig config;

    public bool AdminEnabled { get; }
    public bool EmailEnabled { get; }
    public bool PushoverEnabled { get; }
    public string EmailRecipient { get; }

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if(!AdminEnabled)
            return errors;

        if(!EmailEnabled && !PushoverEnabled)
            errors.Add("notifications.admin.enabled requires a nonblank notifications.admin.emailAddress or enabled notifications.pushover");

        if(EmailEnabled)
        {
            try
            {
                RequireAddress(EmailRecipient, "notifications.admin.emailAddress");
            }
            catch(InvalidOperationException ex)
            {
                errors.Add(ex.Message);
            }
            try
            {
                ValidateEmailProvider();
            }
            catch(InvalidOperationException ex)
            {
                errors.Add(ex.Message);
            }
        }

        if(PushoverEnabled)
        {
            if(string.IsNullOrWhiteSpace(config.Pushover.User))
                errors.Add("notifications.pushover.user must be nonblank when Pushover is enabled");
            if(string.IsNullOrWhiteSpace(config.Pushover.Token))
                errors.Add("notifications.pushover.token must be nonblank when Pushover is enabled");
        }

        return errors;
    }

    public (EmailSenderConfig Config, SecureSocketOptions SocketOptions)
        ValidateEmailProvider()
    {
        // Validate and return one snapshot for the eventual SMTP send.
        var email = config?.Email?.Snapshot() ?? throw new InvalidOperationException(
            "notifications.admin.emailAddress requires notifications.email configuration");
        var socketOptions = SmtpTransportPolicy.Resolve(email);
        Require(email.FromName, "notifications.email.fromName");
        RequireAddress(email.FromAddress, "notifications.email.fromAddress");
        return (email, socketOptions);
    }

    public void ValidatePushoverProvider()
    {
        Require(config?.Pushover?.User, "notifications.pushover.user");
        Require(config?.Pushover?.Token, "notifications.pushover.token");
    }

    public static void RequireAddress(string value, string path)
    {
        Require(value, path);
        if(value.IndexOf('@') <= 0 || value.LastIndexOf('@') == value.Length - 1 ||
            !MailboxAddress.TryParse(value, out var mailbox) ||
            !string.Equals(mailbox.Address, value, StringComparison.Ordinal))
            throw new InvalidOperationException($"{path} must be a valid plain email address");
    }

    private static void Require(string value, string path)
    {
        if(string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{path} must be nonblank when its notification transport is enabled");
    }
}
