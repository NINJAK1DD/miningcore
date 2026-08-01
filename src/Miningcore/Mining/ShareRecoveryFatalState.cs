using System.Text;
using Miningcore.Configuration;

namespace Miningcore.Mining;

public interface IShareRecoveryFatalState
{
    string RecoveryFilename { get; }
    string FatalStateFilename { get; }
    void EnsureStartupAllowed();
    void MarkFatal(int shareCount, IReadOnlyCollection<string> pools,
        Exception databaseError, Exception journalError);
}

public sealed class ShareRecoveryFatalState : IShareRecoveryFatalState
{
    public ShareRecoveryFatalState(ClusterConfig clusterConfig)
    {
        ArgumentNullException.ThrowIfNull(clusterConfig);

        RecoveryFilename = ResolveRecoveryFilename(clusterConfig);
        FatalStateFilename = RecoveryFilename + ".fatal";
    }

    public string RecoveryFilename { get; }
    public string FatalStateFilename { get; }

    public void EnsureStartupAllowed()
    {
        if(File.Exists(FatalStateFilename))
        {
            throw new PoolStartupException(
                $"Share-accounting durability remains unreconciled. Fatal state: " +
                $"{FatalStateFilename}. Recovery journal: {RecoveryFilename}. Preserve both files, " +
                "reconcile PostgreSQL and the journal, then remove only the .fatal file as the " +
                "explicit operator acknowledgement before restarting Miningcore.");
        }

        if(!File.Exists(RecoveryFilename))
            return;

        try
        {
            using var stream = new FileStream(RecoveryFilename, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            ShareRecorder.EnsureRecoveryJournalAppendBoundary(stream,
                RecoveryFilename);
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or
            UnauthorizedAccessException)
        {
            throw new PoolStartupException(
                $"Recovery journal startup validation failed for {RecoveryFilename}: " +
                $"{ex.Message} Preserve it for reconciliation before restarting Miningcore.");
        }
    }

    public void MarkFatal(int shareCount, IReadOnlyCollection<string> pools,
        Exception databaseError, Exception journalError)
    {
        var directory = Path.GetDirectoryName(FatalStateFilename);

        if(!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var content = new StringBuilder()
            .AppendLine("Miningcore share-accounting durability failure")
            .AppendLine($"createdUtc={DateTimeOffset.UtcNow:O}")
            .AppendLine($"recoveryFile={RecoveryFilename}")
            .AppendLine($"shareCount={shareCount}")
            .AppendLine($"pools={string.Join(",", pools)}")
            .AppendLine($"databaseError={databaseError?.GetType().FullName}: {databaseError?.Message}")
            .AppendLine($"journalError={journalError?.GetType().FullName}: {journalError?.Message}")
            .AppendLine("Reconcile before deleting this marker and restarting Miningcore.")
            .ToString();

        using var stream = new FileStream(FatalStateFilename, FileMode.Create,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024,
            leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(true);
    }

    internal static string ResolveRecoveryFilename(ClusterConfig clusterConfig)
    {
        var configured = !string.IsNullOrWhiteSpace(clusterConfig.ShareRecoveryFile)
            ? clusterConfig.ShareRecoveryFile
            : "recovered-shares.txt";

        return Path.GetFullPath(configured);
    }
}
