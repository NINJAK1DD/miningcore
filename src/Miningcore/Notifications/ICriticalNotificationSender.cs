using Miningcore.Notifications.Messages;

namespace Miningcore.Notifications;

public interface ICriticalNotificationSender
{
    Task SendCriticalAdminNotificationAsync(AdminNotification notification,
        CancellationToken ct);
}
