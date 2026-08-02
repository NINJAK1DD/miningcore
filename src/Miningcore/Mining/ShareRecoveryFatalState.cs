using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Miningcore.Configuration;
using Newtonsoft.Json;
using Share = Miningcore.Blockchain.Share;

namespace Miningcore.Mining;

public interface IShareRecoveryFatalState
{
    string RecoveryFilename { get; }
    string FatalStateFilename { get; }
    void EnsureStartupAllowed();
    void MarkFatal(int shareCount, IReadOnlyCollection<string> pools,
        Exception databaseError, Exception journalError);
    void MarkFatalShares(IReadOnlyCollection<Share> shares,
        Exception databaseError, Exception journalError,
        string failureCategory = "database-and-journal-unavailable");
    bool Acknowledge(TextWriter output);
}

public sealed class ShareRecoveryFatalState : IShareRecoveryFatalState
{
    internal const string VerifyOption = "--verify-share-recovery-state";
    internal const string AcknowledgeOption =
        "--acknowledge-share-recovery-state";
    internal const string OperatorAcknowledgementInstruction =
        "Reconcile every incident against PostgreSQL and the recovery journal, then run " +
        "Miningcore -c <configuration> --verify-share-recovery-state followed by " +
        "Miningcore -c <configuration> --acknowledge-share-recovery-state. Do not manually " +
        "delete the active latch, incident metadata or exact-share sidecars.";

    public ShareRecoveryFatalState(ClusterConfig clusterConfig,
        IProcessStatus processStatus) :
        this(clusterConfig, processStatus, ResolveStateDirectory(clusterConfig))
    {
    }

    internal ShareRecoveryFatalState(ClusterConfig clusterConfig,
        IProcessStatus processStatus, string stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(clusterConfig);
        ArgumentNullException.ThrowIfNull(processStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        this.processStatus = processStatus;
        this.clusterConfig = clusterConfig;
        RecoveryFilename = ResolveRecoveryFilename(clusterConfig);
        StateDirectory = Path.GetFullPath(stateDirectory);
        var pathHash = ComputeRecoveryPathHash(RecoveryFilename);
        FatalStateFilename = Path.Combine(StateDirectory,
            "share-recovery-fatal", pathHash + ".fatal");
        MutationLockFilename = Path.Combine(StateDirectory,
            "share-recovery-fatal", pathHash + ".mutation.lock");
        terminalState = new ShareRecoveryTerminalState(RecoveryFilename,
            StateDirectory);
        importState = new ShareRecoveryImportState(RecoveryFilename,
            StateDirectory);
    }

    private readonly IProcessStatus processStatus;
    private readonly ClusterConfig clusterConfig;
    private readonly object fatalStateGate = new();
    private readonly ShareRecoveryTerminalState terminalState;
    private readonly ShareRecoveryImportState importState;
    internal Action<string> DirectorySync { get; set; } =
        SyncDirectoryWhereSupported;
    internal Action<int> ExactShareWriteCheckpoint { get; set; } = _ => { };
    internal Action CompletedIncidentPublishedCheckpoint { get; set; } =
        () => { };
    internal Action AcknowledgementPublishedCheckpoint { get; set; } = () => { };
    internal Action ActiveLatchRemovedCheckpoint { get; set; } = () => { };

    public string RecoveryFilename { get; }
    public string StateDirectory { get; }
    public string FatalStateFilename { get; }
    internal string MutationLockFilename { get; }
    internal static readonly string[] CompletionInvariantKeys =
    {
        "formatVersion", "incidentId", "incidentSequence",
        "previousIncidentDigest", "legacyIncidentCount",
        "legacyIncidentSetSha256", "createdUtc", "failureCategory",
        "recoveryFile", "recoveryPathSha256", "shareCount", "detailFile",
        "databaseError", "journalError",
    };

    public void EnsureStartupAllowed()
    {
        try
        {
            using var mutationLock = AcquireMutationLock();
            var markerEntryWasPresent = EnsureFatalStateDirectoryAccessible();
            terminalState.DirectorySync = DirectorySync;
            importState.DirectorySync = DirectorySync;
            terminalState.EnsureDirectoryDurable();
            importState.EnsureDirectoryDurable();
            ResumeCompletedIncidentPublication();

            try
            {
                using var marker = new FileStream(FatalStateFilename, FileMode.Open,
                    FileAccess.Read, FileShare.Read);
                _ = marker.ReadByte();

                throw new PoolStartupException(
                    $"Share-accounting durability remains unreconciled. Fatal state: " +
                    $"{FatalStateFilename}. Recovery journal: {RecoveryFilename}. Preserve both " +
                    "files and reconcile every recorded incident against PostgreSQL and the journal. " +
                    "Then run --acknowledge-share-recovery-state with the same configuration; " +
                    "do not manually delete the active latch or retained evidence.");
            }
            catch(FileNotFoundException) when(!markerEntryWasPresent)
            {
                // The independently owned state directory is accessible and this exact marker
                // does not exist. Other metadata and I/O failures are deliberately fail-closed.
            }

            // A removed latch must not hide retained incident evidence. A clean state has neither
            // object; any orphaned, shortened or substituted chain blocks startup with status 74.
            var incidentTip = ShareRecoveryIncidentChain.ReadTip(
                Path.GetDirectoryName(FatalStateFilename)!,
                Path.GetFileNameWithoutExtension(FatalStateFilename),
                FatalStateFilename);

            if(incidentTip.ExistingCount > 0)
            {
                var verification = ShareRecoveryIncidentVerifier.Verify(
                    clusterConfig, TextWriter.Null);
                if(!verification.IsSuccessful)
                    throw new PoolStartupException(
                        "Acknowledged share-recovery evidence is incomplete or corrupt. " +
                        $"Run {VerifyOption} with the service configuration, preserve all " +
                        "incident metadata and exact-share sidecars, and reconcile the damage " +
                        "before restarting Miningcore.");
            }

            try
            {
                importState.EnsureNoOutstandingImport();
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or
                                      UnauthorizedAccessException)
            {
                throw new PoolStartupException(
                    $"Recovery-import retirement validation failed for {RecoveryFilename}: " +
                    $"{ex.Message}");
            }

            try
            {
                using var stream = new FileStream(RecoveryFilename, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite);
                ShareRecorder.EnsureRecoveryJournalAppendBoundary(stream,
                    RecoveryFilename);
                stream.Seek(0, SeekOrigin.Begin);
                var tail = ShareRecorder.ValidateRecoveryJournalDetailed(stream,
                    RecoveryFilename);
                terminalState.EnsureConsistent(tail.Sequence, tail.FrameDigest,
                    tail.IsChainedFormat);
            }
            catch(FileNotFoundException)
            {
                // A journal is created only when PostgreSQL first needs the fallback.
                terminalState.EnsureJournalMayBeMissing();
            }
            catch(DirectoryNotFoundException)
            {
                // A not-yet-created configured journal parent is equivalent to no journal.
                terminalState.EnsureJournalMayBeMissing();
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or
                UnauthorizedAccessException)
            {
                throw new PoolStartupException(
                    $"Recovery journal startup validation failed for {RecoveryFilename}: " +
                    $"{ex.Message} Preserve it for reconciliation before restarting Miningcore.");
            }
        }
        catch(PoolStartupException)
        {
            processStatus.MarkFailed(ProcessExitCodes.UnreconciledShareDurabilityLoss);
            throw;
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            processStatus.MarkFailed(ProcessExitCodes.UnreconciledShareDurabilityLoss);
            throw new PoolStartupException(
                $"Unable to determine the share-recovery fatal state at " +
                $"{FatalStateFilename}: {ex.Message} Startup is blocked because fatal-state " +
                "uncertainty must be reconciled before mining can resume.");
        }
    }

    public void MarkFatal(int shareCount, IReadOnlyCollection<string> pools,
        Exception databaseError, Exception journalError)
    {
        lock(fatalStateGate)
        {
            using var mutationLock = AcquireMutationLock();
            MarkFatalCore(shareCount, pools, null, databaseError, journalError,
                "database-and-journal-unavailable");
        }
    }

    public void MarkFatalShares(IReadOnlyCollection<Share> shares,
        Exception databaseError, Exception journalError,
        string failureCategory = "database-and-journal-unavailable")
    {
        ArgumentNullException.ThrowIfNull(shares);

        lock(fatalStateGate)
        {
            using var mutationLock = AcquireMutationLock();
            MarkFatalCore(shares.Count, null, shares, databaseError,
                journalError, failureCategory);
        }
    }

    public bool Acknowledge(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        lock(fatalStateGate)
        {
            using var mutationLock = AcquireMutationLock();
            ResumeCompletedIncidentPublication();
            var verification = ShareRecoveryIncidentVerifier.Verify(
                clusterConfig, output);
            if(!verification.IsSuccessful)
            {
                output.WriteLine("ACKNOWLEDGEMENT REFUSED: fatal share-recovery evidence is not structurally complete.");
                return false;
            }

            var directory = Path.GetDirectoryName(FatalStateFilename)!;
            var stem = Path.GetFileNameWithoutExtension(FatalStateFilename);
            var tip = ShareRecoveryIncidentChain.ReadTip(directory, stem,
                FatalStateFilename);
            if(tip.ExistingCount == 0)
                throw new InvalidOperationException(
                    "No fatal share-recovery incident exists to acknowledge");

            string[] latchLines;
            using(var latch = RecoveryStateFile.TryOpenExactEntry(
                      FatalStateFilename, Directory.EnumerateFileSystemEntries))
            {
                if(latch == null)
                {
                    output.WriteLine(
                        "Share-recovery state is already durably acknowledged; the active blocking latch is absent.");
                    return true;
                }

                latchLines = RecoveryStateFile.ReadAllLinesStable(latch,
                    FatalStateFilename, Directory.EnumerateFileSystemEntries);
            }
            var legacyOnly = tip.Sequence == 0 && tip.LegacyCount > 0;
            var acknowledgementFilename = legacyOnly
                ? ShareRecoveryIncidentChain.BuildLegacyAcknowledgementFilename(
                    directory, stem, tip)
                : ShareRecoveryIncidentChain.BuildAcknowledgementFilename(
                    directory, stem, tip);
            var acknowledgementLines = legacyOnly
                ? BuildLegacyAcknowledgement(latchLines, tip)
                : latchLines;
            using(var existingAcknowledgement =
                  RecoveryStateFile.TryOpenExactEntry(acknowledgementFilename,
                      Directory.EnumerateFileSystemEntries))
            {
                if(existingAcknowledgement == null)
                    WriteSmallStateFile(acknowledgementFilename,
                        string.Join('\n', acknowledgementLines) + '\n', false);
                else
                {
                    var existingLines = RecoveryStateFile.ReadAllLinesStable(
                        existingAcknowledgement, acknowledgementFilename,
                        Directory.EnumerateFileSystemEntries);
                    if(!acknowledgementLines.SequenceEqual(existingLines,
                           StringComparer.Ordinal))
                        throw new InvalidDataException(
                            $"Existing acknowledgement {acknowledgementFilename} does not match the active fatal latch");
                }
            }

            AcknowledgementPublishedCheckpoint();
            File.Delete(FatalStateFilename);
            DirectorySync(directory);
            ActiveLatchRemovedCheckpoint();

            // Re-open and validate the acknowledged anchor after removing the blocking latch.
            // Returning success is itself contingent on the preserved evidence remaining anchored.
            ShareRecoveryIncidentChain.ReadTip(directory, stem,
                FatalStateFilename);
            output.WriteLine($"ACKNOWLEDGED: {acknowledgementFilename}");
            output.WriteLine(
                "The immutable incident metadata and exact-share sidecars remain preserved for audit.");
            return true;
        }
    }

    private FileStream AcquireMutationLock()
    {
        _ = EnsureFatalStateDirectoryAccessible();
        FileStream stream = null;

        try
        {
            stream = new FileStream(MutationLockFilename, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1,
                FileOptions.WriteThrough);
            if(stream.Length == 0)
            {
                stream.WriteByte(0);
                stream.Flush(true);
                DirectorySync(Path.GetDirectoryName(MutationLockFilename)!);
            }

            return stream;
        }
        catch(IOException ex)
        {
            stream?.Dispose();
            throw new InvalidOperationException(
                $"Another Miningcore service or share-recovery operation still owns the " +
                $"state mutation lock {MutationLockFilename}. Wait for it to exit before " +
                "acknowledging or publishing fatal evidence.", ex);
        }
    }

    private static string[] BuildLegacyAcknowledgement(
        IEnumerable<string> latchLines,
        ShareRecoveryIncidentChain.ChainTip tip)
    {
        var result = new List<string>();

        foreach(var line in latchLines)
        {
            if(line.StartsWith("formatVersion=", StringComparison.Ordinal))
                result.Add("formatVersion=4");
            else if(!string.IsNullOrEmpty(line) &&
                    !line.StartsWith("Reconcile every ",
                        StringComparison.Ordinal))
                result.Add(line);
        }

        result.Add("acknowledgementKind=legacy-v2-set");
        result.Add($"legacyIncidentCount={tip.LegacyCount.ToString(CultureInfo.InvariantCulture)}");
        result.Add($"legacyIncidentSetSha256={tip.LegacyDigest}");
        result.Add($"incidentChainDigest={tip.LegacyDigest}");
        result.Add($"expectedIncidentCount={tip.ExistingCount.ToString(CultureInfo.InvariantCulture)}");
        result.Add(OperatorAcknowledgementInstruction);
        return result.ToArray();
    }

    private void MarkFatalCore(int shareCount, IReadOnlyCollection<string> pools,
        IReadOnlyCollection<Share> shares, Exception databaseError,
        Exception journalError, string failureCategory)
    {
        _ = EnsureFatalStateDirectoryAccessible();
        var recoveryPathHash = ComputeRecoveryPathHash(RecoveryFilename);
        var incidentId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}";
        var directory = Path.GetDirectoryName(FatalStateFilename)!;
        var stem = Path.GetFileNameWithoutExtension(FatalStateFilename);
        ResumeCompletedIncidentPublication();
        var chain = ShareRecoveryIncidentChain.ReadTip(directory, stem,
            FatalStateFilename);
        var incidentSequence = chain.Sequence + 1;
        var detailFilename = shares != null
            ? Path.Combine(directory, $"{stem}.{incidentId}.shares")
            : null;
        string detailHash = null;

        var created = DateTimeOffset.UtcNow;
        var initialIncident = BuildIncidentState(incidentId, created,
            recoveryPathHash, failureCategory, shareCount, pools, databaseError,
            journalError, detailFilename, detailHash,
            shares == null ? "not-required" : "hash-pending",
            incidentSequence, chain.Digest, chain.LegacyCount,
            chain.LegacyDigest, null, chain.ExistingCount + 1,
            shares != null);
        var initialDigest = ShareRecoveryIncidentChain.ComputeDigest(initialIncident);
        var initialLatch = BuildIncidentState(incidentId, created,
            recoveryPathHash, failureCategory, shareCount, pools, databaseError,
            journalError, detailFilename, detailHash,
            shares == null ? "not-required" : "hash-pending",
            incidentSequence, chain.Digest, chain.LegacyCount,
            chain.LegacyDigest, initialDigest, chain.ExistingCount + 1,
            shares != null);
        var incidentFilename = Path.Combine(directory,
            $"{stem}.{incidentId}.incident");

        // Publish the small authoritative startup barrier before enumerating or serializing any
        // exact shares. A serialization, memory-pressure or sidecar write failure must never
        // leave a later restart unblocked.
        WriteSmallStateFile(FatalStateFilename, initialLatch, true);
        WriteSmallStateFile(incidentFilename, initialIncident, false);

        if(shares != null)
        {
            var detail = WriteExactShareSidecar(detailFilename, shares);
            detailHash = detail.Hash;
            pools = detail.Pools;
        }

        var completedIncident = BuildIncidentState(incidentId, created,
            recoveryPathHash, failureCategory, shareCount, pools, databaseError,
            journalError, detailFilename, detailHash,
            shares == null ? "not-required" : "complete",
            incidentSequence, chain.Digest, chain.LegacyCount,
            chain.LegacyDigest, null, chain.ExistingCount + 1);
        var completedDigest = ShareRecoveryIncidentChain.ComputeDigest(
            completedIncident);
        var completedLatch = BuildIncidentState(incidentId, created,
            recoveryPathHash, failureCategory, shareCount, pools, databaseError,
            journalError, detailFilename, detailHash,
            shares == null ? "not-required" : "complete",
            incidentSequence, chain.Digest, chain.LegacyCount,
            chain.LegacyDigest, completedDigest, chain.ExistingCount + 1);
        // Each incident record is advanced once from hash-pending to complete, then remains
        // immutable evidence. The fixed-name latch remains a small current startup barrier.
        WriteSmallStateFile(incidentFilename, completedIncident, true);
        CompletedIncidentPublishedCheckpoint();
        WriteSmallStateFile(FatalStateFilename, completedLatch, true);
    }

    private bool ResumeCompletedIncidentPublication()
    {
        var directory = Path.GetDirectoryName(FatalStateFilename)!;
        var stem = Path.GetFileNameWithoutExtension(FatalStateFilename);
        StateEntry latchEntry;
        using(var latch = RecoveryStateFile.TryOpenExactEntry(FatalStateFilename,
                  Directory.EnumerateFileSystemEntries))
        {
            if(latch == null)
                return false;

            latchEntry = ReadStateEntry(latch, FatalStateFilename);
        }
        if(!string.Equals(GetStateValue(latchEntry.Metadata, "detailState"),
               "hash-pending", StringComparison.Ordinal))
            return false;

        var incidentId = GetStateValue(latchEntry.Metadata, "incidentId");
        if(string.IsNullOrWhiteSpace(incidentId))
            throw new InvalidDataException(
                "The hash-pending fatal latch has no incident identity");

        var incidentFilename = Path.Combine(directory,
            $"{stem}.{incidentId}.incident");
        StateEntry incidentEntry;
        using(var incident = RecoveryStateFile.TryOpenExactEntry(incidentFilename,
                  Directory.EnumerateFileSystemEntries))
        {
            if(incident == null)
                return false;

            incidentEntry = ReadStateEntry(incident, incidentFilename);
        }
        if(!string.Equals(GetStateValue(incidentEntry.Metadata, "detailState"),
               "complete", StringComparison.Ordinal))
            return false;

        foreach(var key in CompletionInvariantKeys)
        {
            if(!string.Equals(GetStateValue(latchEntry.Metadata, key),
                   GetStateValue(incidentEntry.Metadata, key),
                   StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"The completed fatal incident cannot resume latch publication because key '{key}' changed");
        }

        if(!string.Equals(GetStateValue(latchEntry.Metadata, "pools"),
               "(pending)", StringComparison.Ordinal) ||
           !string.Equals(GetStateValue(latchEntry.Metadata, "detailSha256"),
               "(none)", StringComparison.Ordinal))
            throw new InvalidDataException(
                "The hash-pending fatal latch is not a valid predecessor of the completed incident");

        var initialIncidentLines = latchEntry.Lines.Where(line =>
            !line.StartsWith("incidentChainDigest=", StringComparison.Ordinal) &&
            !line.StartsWith("expectedIncidentCount=", StringComparison.Ordinal));
        var initialDigest = ShareRecoveryIncidentChain.ComputeDigest(
            ComposeStateContent(initialIncidentLines));
        if(!string.Equals(initialDigest,
               GetStateValue(latchEntry.Metadata, "incidentChainDigest"),
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The hash-pending fatal latch does not authenticate its original incident metadata");

        var verification = ShareRecoveryIncidentVerifier.Verify(clusterConfig,
            TextWriter.Null);
        if(verification.InvalidCount != 0 ||
           verification.CompleteCount != verification.IncidentCount)
            throw new InvalidDataException(
                "The completed fatal incident or its exact-share sidecar cannot be verified for resumable publication");

        var tip = ShareRecoveryIncidentChain.ReadTip(directory, stem,
            FatalStateFilename);
        if(!string.Equals(tip.Digest, incidentEntry.Digest,
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The completed fatal incident is not the validated incident-chain tip");
        if(GetStateValue(incidentEntry.Metadata, "incidentChainDigest") != null ||
           GetStateValue(incidentEntry.Metadata, "expectedIncidentCount") != null)
            throw new InvalidDataException(
                "Completed incident metadata unexpectedly contains active-latch chain fields");

        var completedLatchLines = new List<string>(incidentEntry.Lines.Length + 2);
        var anchorInserted = false;
        foreach(var line in incidentEntry.Lines)
        {
            if(string.Equals(line, OperatorAcknowledgementInstruction,
                   StringComparison.Ordinal))
            {
                if(anchorInserted)
                    throw new InvalidDataException(
                        "Completed incident metadata repeats the operator instruction boundary");
                completedLatchLines.Add($"incidentChainDigest={incidentEntry.Digest}");
                completedLatchLines.Add(
                    $"expectedIncidentCount={tip.ExistingCount.ToString(CultureInfo.InvariantCulture)}");
                anchorInserted = true;
            }

            completedLatchLines.Add(line);
        }

        if(!anchorInserted)
            throw new InvalidDataException(
                "Completed incident metadata has no operator instruction boundary");

        WriteSmallStateFile(FatalStateFilename,
            ComposeStateContent(completedLatchLines), true);
        return true;
    }

    private static StateEntry ReadStateEntry(FileStream stream,
        string filename)
    {
        stream.Position = 0;
        var digest = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        var lines = RecoveryStateFile.ReadAllLinesStable(stream, filename,
            Directory.EnumerateFileSystemEntries);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach(var line in lines)
        {
            var separator = line.IndexOf('=');
            if(separator <= 0)
                continue;
            if(!metadata.TryAdd(line[..separator], line[(separator + 1)..]))
                throw new InvalidDataException(
                    $"Fatal state metadata contains duplicate key '{line[..separator]}': {filename}");
        }

        return new StateEntry(lines, metadata, digest);
    }

    private static string ComposeStateContent(IEnumerable<string> lines) =>
        string.Join('\n', lines) + '\n';

    private static string GetStateValue(
        IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) ? value : null;

    private sealed record StateEntry(string[] Lines,
        IReadOnlyDictionary<string, string> Metadata, string Digest);

    private string BuildIncidentState(string incidentId, DateTimeOffset created,
        string recoveryPathHash, string failureCategory, int shareCount,
        IReadOnlyCollection<string> pools, Exception databaseError,
        Exception journalError, string detailFilename, string detailHash,
        string detailState, long incidentSequence, string previousIncidentDigest,
        int legacyIncidentCount, string legacyIncidentSetDigest,
        string incidentChainDigest, int expectedIncidentCount,
        bool poolsPending = false)
    {
        var lines = new List<string>
        {
            "Miningcore share-accounting durability failure",
            "formatVersion=3",
            $"incidentId={incidentId}",
            $"incidentSequence={incidentSequence.ToString(CultureInfo.InvariantCulture)}",
            $"previousIncidentDigest={previousIncidentDigest}",
            $"legacyIncidentCount={legacyIncidentCount.ToString(CultureInfo.InvariantCulture)}",
            $"legacyIncidentSetSha256={legacyIncidentSetDigest}",
            $"createdUtc={created:O}",
            $"failureCategory={SanitizeStateValue(failureCategory, 256)}",
            $"recoveryFile={RecoveryFilename}",
            $"recoveryPathSha256={recoveryPathHash}",
            $"shareCount={shareCount}",
            $"pools={(poolsPending ? "(pending)" : SanitizeStateValue(
                string.Join(',', pools ?? Array.Empty<string>()), 4096))}",
            $"detailFile={detailFilename ?? "(none)"}",
            $"detailSha256={detailHash ?? "(none)"}",
            $"detailState={detailState}",
            $"databaseError={FormatStateException(databaseError)}",
            $"journalError={FormatStateException(journalError)}",
        };

        if(incidentChainDigest != null)
        {
            lines.Add($"incidentChainDigest={incidentChainDigest}");
            lines.Add($"expectedIncidentCount={expectedIncidentCount.ToString(CultureInfo.InvariantCulture)}");
        }

        lines.Add(OperatorAcknowledgementInstruction);
        lines.Add(string.Empty);
        return string.Join('\n', lines);
    }

    private static string FormatStateException(Exception error) => error == null
        ? "(none)"
        : SanitizeStateValue($"{error.GetType().FullName}: {error.Message}", 2048);

    private static string SanitizeStateValue(string value, int maxLength)
    {
        value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private ExactShareDetail WriteExactShareSidecar(string filename,
        IReadOnlyCollection<Share> shares)
    {
        var directory = Path.GetDirectoryName(filename)!;
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(filename)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var pools = new HashSet<string>(StringComparer.Ordinal);

            using(var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var index = 0;

                foreach(var share in shares)
                {
                    ExactShareWriteCheckpoint(index++);
                    var record = Encoding.UTF8.GetBytes(SerializeExactShareRecord(share));
                    stream.Write(record);
                    hash.AppendData(record);

                    if(!string.IsNullOrWhiteSpace(share.PoolId))
                        pools.Add(share.PoolId);
                }

                stream.Flush(true);
            }

            File.Move(temporary, filename);
            DirectorySync(directory);

            return new ExactShareDetail(Convert.ToHexString(hash.GetHashAndReset()),
                pools.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // The startup latch is already durable and records an incomplete sidecar. A
                // stray temporary remains incident evidence rather than an acknowledgement.
            }
        }
    }

    private readonly record struct ExactShareDetail(string Hash, string[] Pools);

    private static string SerializeExactShareRecord(Share share)
    {
        var json = JsonConvert.SerializeObject(share, Formatting.None);
        return "shareJsonBase64=" + Convert.ToBase64String(
            Encoding.UTF8.GetBytes(json)) + "\n";
    }

    private void WriteSmallStateFile(string filename, string content,
        bool replace)
    {
        var directory = Path.GetDirectoryName(filename)!;
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(filename)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using(var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using(var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024,
                leaveOpen: true))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(temporary, filename, replace);
            DirectorySync(directory);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // The destination is authoritative once renamed; otherwise the original write
                // error must escape while any temporary remains incident evidence.
            }
        }
    }

    private bool EnsureFatalStateDirectoryAccessible()
    {
        var directory = Path.GetDirectoryName(FatalStateFilename)!;
        DurableDirectory.EnsureCreated(directory, DirectorySync);

        // Opening the directory for enumeration catches access and metadata failures that
        // File.Exists would incorrectly collapse into "marker missing".
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return Directory.EnumerateFileSystemEntries(directory)
            .Any(entry => string.Equals(Path.GetFullPath(entry),
                FatalStateFilename, comparison));
    }

    internal static string ResolveRecoveryFilename(ClusterConfig clusterConfig)
    {
        var configured = !string.IsNullOrWhiteSpace(clusterConfig.ShareRecoveryFile)
            ? clusterConfig.ShareRecoveryFile
            : "recovered-shares.txt";

        return Path.GetFullPath(configured);
    }

    internal static string ResolveStateDirectory(ClusterConfig clusterConfig)
    {
        if(!string.IsNullOrWhiteSpace(clusterConfig.ShareRecoveryStateDirectory))
            return Path.GetFullPath(clusterConfig.ShareRecoveryStateDirectory);

        var systemdStateDirectory = Environment.GetEnvironmentVariable("STATE_DIRECTORY");

        if(!string.IsNullOrWhiteSpace(systemdStateDirectory))
        {
            var first = OperatingSystem.IsWindows()
                ? systemdStateDirectory
                : systemdStateDirectory.Split(':', 2)[0];
            return Path.GetFullPath(first);
        }

        var applicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        if(!string.IsNullOrWhiteSpace(applicationData))
            return Path.Combine(applicationData, "Miningcore");

        return Path.Combine(AppContext.BaseDirectory, "state");
    }

    internal static string ComputeRecoveryPathHash(string recoveryFilename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilename);
        var identity = OperatingSystem.IsWindows()
            ? recoveryFilename.ToUpperInvariant()
            : recoveryFilename;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    internal static void SyncDirectoryWhereSupported(string directory)
    {
        if(!OperatingSystem.IsLinux())
            return;

        const int oDirectory = 0x10000;
        var descriptor = open(directory, oDirectory);

        if(descriptor < 0)
            throw new IOException(
                $"Unable to open directory for durable sync (errno {Marshal.GetLastPInvokeError()})");

        try
        {
            if(fsync(descriptor) != 0)
                throw new IOException(
                    $"Unable to durably sync directory (errno {Marshal.GetLastPInvokeError()})");
        }
        finally
        {
            _ = close(descriptor);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);
}
