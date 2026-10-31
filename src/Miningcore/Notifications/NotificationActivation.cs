using Miningcore.Configuration;
using Miningcore.Mining;

namespace Miningcore.Notifications;

// The legacy notifications.enabled value is deliberately ignored. Admin.Enabled is the
// activation switch; a nonblank recipient opts into email and Pushover.Enabled opts into push.
// Keep this policy shared by validation, the hosted instance and the unhosted critical sender.
internal sealed class NotificationActivation
{
    public NotificationActivation(NotificationsConfig config)
    {
        this.config = config;
        AdminEnabled = config?.Admin?.Enabled == true;
        EmailEnabled = AdminEnabled &&
            !string.IsNullOrWhiteSpace(config.Admin.EmailAddress);
        PushoverEnabled = AdminEnabled && config.Pushover?.Enabled == true;
    }

    private readonly NotificationsConfig config;

    public bool AdminEnabled { get; }
    public bool EmailEnabled { get; }
    public bool PushoverEnabled { get; }
    public string EmailRecipient => config?.Admin?.EmailAddress;

    public void ValidateForNormalHost()
    {
        if(!AdminEnabled)
            return;

        if(!EmailEnabled && !PushoverEnabled)
            throw new PoolStartupException(
                "notifications.admin.enabled requires a nonblank notifications.admin.emailAddress or enabled notifications.pushover");

        if(EmailEnabled)
            ValidateEmailProvider();

        if(PushoverEnabled)
            ValidatePushoverProvider();
    }

    public EmailSenderConfig ValidateEmailProvider()
    {
        // Use one snapshot for policy validation and the eventual SMTP send.
        var email = config?.Email?.Snapshot();
        if(email == null)
            throw new PoolStartupException(
                "notifications.admin.emailAddress requires notifications.email configuration");
        try
        {
            SmtpTransportPolicy.Resolve(email);
        }
        catch(InvalidOperationException ex)
        {
            throw new PoolStartupException(ex.Message, ex);
        }
        Require(email.FromName, "notifications.email.fromName");
        Require(email.FromAddress, "notifications.email.fromAddress");
        return email;
    }

    public void ValidatePushoverProvider()
    {
        Require(config?.Pushover?.User, "notifications.pushover.user");
        Require(config?.Pushover?.Token, "notifications.pushover.token");
    }

    private static void Require(string value, string path)
    {
        if(string.IsNullOrWhiteSpace(value))
            throw new PoolStartupException($"{path} must be nonblank when its notification transport is enabled");
    }
}

