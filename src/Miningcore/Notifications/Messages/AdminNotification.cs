namespace Miningcore.Notifications.Messages;

public enum AdminNotificationImportance
{
    Routine,
    FinancialOrSecurity,
}

public record AdminNotification
{
    public AdminNotification(string subject, string message) : this(subject,
        message, AdminNotificationImportance.Routine)
    {
    }

    public AdminNotification(string subject, string message,
        AdminNotificationImportance importance,
        string poolId = null)
    {
        Subject = subject;
        Message = message;
        Importance = importance;
        PoolId = poolId;
    }

    public string Subject { get; }
    public string Message { get; }
    public AdminNotificationImportance Importance { get; }
    public string PoolId { get; }
}
