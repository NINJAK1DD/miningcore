using Microsoft.Extensions.Hosting;
using Miningcore.Blockchain;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using NLog;

namespace Miningcore.Mining;

public interface IShareRecoveryFailureHandler
{
    void StopCluster(IReadOnlyCollection<Share> shares, string recoveryFilename,
        Exception databaseError, Exception journalError);
}

public sealed class ShareRecoveryFailureHandler : IShareRecoveryFailureHandler
{
    public ShareRecoveryFailureHandler(IProcessStatus processStatus,
        IHostApplicationLifetime applicationLifetime, IMessageBus messageBus)
    {
        ArgumentNullException.ThrowIfNull(processStatus);
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(messageBus);

        this.processStatus = processStatus;
        this.applicationLifetime = applicationLifetime;
        this.messageBus = messageBus;
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IProcessStatus processStatus;
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly IMessageBus messageBus;
    private int failureSignalled;

    public void StopCluster(IReadOnlyCollection<Share> shares,
        string recoveryFilename, Exception databaseError, Exception journalError)
    {
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilename);

        var pools = shares
            .Select(share => share.PoolId)
            .Where(poolId => !string.IsNullOrWhiteSpace(poolId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(poolId => poolId, StringComparer.Ordinal)
            .ToArray();
        var poolSummary = pools.Length > 0 ? string.Join(", ", pools) : "(unknown)";
        var failure = journalError ?? databaseError ??
            new IOException("Unknown share-recovery durability failure");

        logger.Fatal(failure,
            "Stopping cluster because neither PostgreSQL nor the recovery journal stored {0} share(s). Pools: {1}. Recovery file: {2}. Database error: {3}. Journal error: {4}",
            shares.Count, poolSummary, recoveryFilename,
            databaseError?.Message ?? "(none)", journalError?.Message ?? "(none)");

        if(Interlocked.Exchange(ref failureSignalled, 1) != 0)
            return;

        processStatus.MarkFailed();

        try
        {
            messageBus.SendMessage(new AdminNotification(
                "Fatal share-recovery fallback failure",
                $"Miningcore is stopping with exit status 1 because neither PostgreSQL nor " +
                $"the recovery journal durably stored {shares.Count} share(s) for pool(s) " +
                $"{poolSummary}. Recovery file: {recoveryFilename}. Preserve any existing " +
                "journal and investigate database and storage health before restarting."));
        }
        catch(Exception ex)
        {
            logger.Error(ex, "Unable to emit fatal share-recovery notification");
        }

        applicationLifetime.StopApplication();
    }
}

internal sealed class NullShareRecoveryFailureHandler :
    IShareRecoveryFailureHandler
{
    public static readonly NullShareRecoveryFailureHandler Instance = new();

    public void StopCluster(IReadOnlyCollection<Share> shares,
        string recoveryFilename, Exception databaseError, Exception journalError)
    {
    }
}
