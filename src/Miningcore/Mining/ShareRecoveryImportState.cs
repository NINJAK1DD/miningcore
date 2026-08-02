using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Miningcore.Mining;

/// <summary>
/// Durable, path-scoped state for the interval between recovery-source validation and source
/// retirement. Its presence blocks normal mining/appends so an imported journal cannot grow and
/// acquire a new whole-file hash before the committed source is archived.
/// </summary>
internal sealed class ShareRecoveryImportState
{
    public ShareRecoveryImportState(string recoveryFilename, string stateDirectory)
    {
        RecoveryFilename = Path.GetFullPath(recoveryFilename);
        var pathHash = ShareRecoveryFatalState.ComputeRecoveryPathHash(RecoveryFilename);
        Filename = Path.Combine(Path.GetFullPath(stateDirectory),
            "share-recovery-import", pathHash + ".import");
    }

    public string RecoveryFilename { get; }
    public string Filename { get; }
    internal Action<string> DirectorySync { get; set; } =
        ShareRecoveryFatalState.SyncDirectoryWhereSupported;
    internal Func<string, IEnumerable<string>> EnumerateEntries { get; set; } =
        Directory.EnumerateFileSystemEntries;
    internal Action<ImportPhase> WriteCheckpoint { get; set; } = _ => { };
    internal Action RemoveCheckpoint { get; set; } = () => { };
    internal Action RemoveDirectorySyncCheckpoint { get; set; } = () => { };

    internal void EnsureDirectoryDurable()
    {
        DurableDirectory.EnsureCreated(Path.GetDirectoryName(Filename)!,
            DirectorySync);
    }

    public ImportMarker TryRead()
    {
        EnsureDirectoryDurable();
        using var stream = RecoveryStateFile.TryOpenExactEntry(Filename,
            EnumerateEntries);
        return stream == null ? null : Read(stream);
    }

    public void EnsureNoOutstandingImport()
    {
        var marker = TryRead();
        if(marker == null)
            return;

        throw new InvalidDataException(
            $"Recovery source {RecoveryFilename} has an unfinished {marker.Phase.ToString().ToLowerInvariant()} " +
            $"import-retirement operation recorded at {Filename}. Resume recovery with the same " +
            "source and configuration; refusing normal startup or journal append until the " +
            "committed source is durably retired.");
    }

    public ImportMarker Begin(string fileHash, int recordCount,
        string archiveFilename, long? terminalSequence = null,
        string terminalDigest = null, bool terminalAnchorRequired = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveFilename);
        if(recordCount < 0)
            throw new ArgumentOutOfRangeException(nameof(recordCount));

        var existing = TryRead();
        if(existing != null)
        {
            if(!string.Equals(existing.FileHash, fileHash,
                   StringComparison.OrdinalIgnoreCase) ||
               existing.RecordCount != recordCount)
                throw new InvalidDataException(
                    $"Recovery import marker {Filename} identifies different source content. " +
                    "Preserve the source and marker for reconciliation.");

            return existing;
        }

        ValidateTerminalState(terminalSequence, terminalDigest,
            terminalAnchorRequired);
        var marker = new ImportMarker(ImportPhase.Pending,
            fileHash.ToUpperInvariant(), recordCount,
            Path.GetFullPath(archiveFilename), terminalSequence,
            terminalDigest?.ToUpperInvariant(), terminalAnchorRequired);
        try
        {
            Write(marker, false);
            return marker;
        }
        catch(IOException writeError)
        {
            existing = TryRead();
            if(existing == null)
                ExceptionDispatchInfo.Capture(writeError).Throw();

            if(!string.Equals(existing.FileHash, fileHash,
                   StringComparison.OrdinalIgnoreCase) ||
               existing.RecordCount != recordCount)
                throw new InvalidDataException(
                    $"Recovery import marker {Filename} was concurrently created for different " +
                    "source content. Preserve the source and marker for reconciliation.");

            return existing;
        }
    }

    public ImportMarker MarkCommitted(ImportMarker marker)
    {
        return Advance(marker, ImportPhase.Committed);
    }

    public ImportMarker RecordTerminalState(ImportMarker marker,
        long sequence, string digest, bool anchorRequired)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ValidateTerminalState(sequence, digest, anchorRequired);

        if(marker.TerminalSequence.HasValue)
        {
            EnsureTerminalStateMatches(marker, sequence, digest,
                anchorRequired);
            return marker;
        }

        var updated = marker with
        {
            TerminalSequence = sequence,
            TerminalDigest = digest.ToUpperInvariant(),
            TerminalAnchorRequired = anchorRequired,
        };
        Write(updated, true);
        return updated;
    }

    public ImportMarker MarkArchiveDurable(ImportMarker marker) =>
        Advance(marker, ImportPhase.ArchiveDurable);

    public ImportMarker AuthoriseAnchorRetirement(ImportMarker marker) =>
        Advance(marker, ImportPhase.AnchorRetirementAuthorised);

    public ImportMarker MarkAnchorRetired(ImportMarker marker) =>
        Advance(marker, ImportPhase.AnchorRetired);

    private ImportMarker Advance(ImportMarker marker, ImportPhase phase)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if(phase < marker.Phase)
            throw new InvalidOperationException(
                $"Recovery import marker cannot move backwards from {marker.Phase} to {phase}");

        if(phase == marker.Phase)
            return marker;

        var updated = marker with { Phase = phase };
        Write(updated, true);
        return updated;
    }

    public void RemoveAfterRetirement()
    {
        var directory = Path.GetDirectoryName(Filename)!;
        RemoveCheckpoint();
        File.Delete(Filename);
        RemoveDirectorySyncCheckpoint();
        DirectorySync(directory);
    }

    private void Write(ImportMarker marker, bool replace)
    {
        var directory = Path.GetDirectoryName(Filename)!;
        EnsureDirectoryDurable();
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(Filename)}.{Guid.NewGuid():N}.tmp");
        var archiveBytes = Encoding.UTF8.GetBytes(marker.ArchiveFilename);
        var content = string.Join('\n',
            "miningcore-share-recovery-import-v2",
            $"recoveryPathSha256={ShareRecoveryFatalState.ComputeRecoveryPathHash(RecoveryFilename)}",
            $"phase={marker.Phase.ToString().ToLowerInvariant()}",
            $"fileHash={marker.FileHash.ToUpperInvariant()}",
            $"recordCount={marker.RecordCount.ToString(CultureInfo.InvariantCulture)}",
            $"archivePathBase64={Convert.ToBase64String(archiveBytes)}",
            $"terminalAnchorRequired={marker.TerminalAnchorRequired.ToString().ToLowerInvariant()}",
            $"terminalSequence={marker.TerminalSequence?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}",
            $"terminalDigest={marker.TerminalDigest ?? "(none)"}", string.Empty);

        try
        {
            using(var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(true);
            }

            WriteCheckpoint(marker.Phase);
            File.Move(temporary, Filename, replace);
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
                // The published marker or original failure remains authoritative.
            }
        }
    }

    private ImportMarker Read(FileStream stream)
    {
        var lines = RecoveryStateFile.ReadAllLinesStable(stream, Filename,
            EnumerateEntries);
        var legacy = lines.Length == 6 &&
            lines[0] == "miningcore-share-recovery-import-v1";
        var current = lines.Length == 9 &&
            lines[0] == "miningcore-share-recovery-import-v2";
        if(!legacy && !current ||
           !lines[1].StartsWith("recoveryPathSha256=", StringComparison.Ordinal) ||
           !lines[2].StartsWith("phase=", StringComparison.Ordinal) ||
           !lines[3].StartsWith("fileHash=", StringComparison.Ordinal) ||
           !lines[4].StartsWith("recordCount=", StringComparison.Ordinal) ||
           !lines[5].StartsWith("archivePathBase64=", StringComparison.Ordinal) ||
           current &&
           (!lines[6].StartsWith("terminalAnchorRequired=", StringComparison.Ordinal) ||
            !lines[7].StartsWith("terminalSequence=", StringComparison.Ordinal) ||
            !lines[8].StartsWith("terminalDigest=", StringComparison.Ordinal)))
            throw new InvalidDataException(
                $"Recovery-import marker {Filename} is malformed");

        var actualPath = lines[1]["recoveryPathSha256=".Length..];
        var phaseText = lines[2]["phase=".Length..];
        var fileHash = lines[3]["fileHash=".Length..];
        var recordCountText = lines[4]["recordCount=".Length..];
        var archiveText = lines[5]["archivePathBase64=".Length..];
        var expectedPath = ShareRecoveryFatalState.ComputeRecoveryPathHash(
            RecoveryFilename);

        if(!string.Equals(actualPath, expectedPath, StringComparison.Ordinal) ||
           !Enum.TryParse<ImportPhase>(phaseText, true, out var phase) ||
           legacy && phase is not (ImportPhase.Pending or ImportPhase.Committed) ||
           fileHash.Length != 64 || !fileHash.All(Uri.IsHexDigit) ||
           !int.TryParse(recordCountText, NumberStyles.None,
               CultureInfo.InvariantCulture, out var recordCount) || recordCount < 0)
            throw new InvalidDataException(
                $"Recovery-import marker {Filename} contains invalid identity or import data");

        string archiveFilename;
        try
        {
            archiveFilename = Path.GetFullPath(Encoding.UTF8.GetString(
                Convert.FromBase64String(archiveText)));
        }
        catch(Exception ex) when(ex is FormatException or ArgumentException or
                                  NotSupportedException)
        {
            throw new InvalidDataException(
                $"Recovery-import marker {Filename} contains an invalid archive path", ex);
        }

        var expectedArchivePrefix = RecoveryFilename + ".imported-";
        if(!archiveFilename.StartsWith(expectedArchivePrefix,
               OperatingSystem.IsWindows()
                   ? StringComparison.OrdinalIgnoreCase
                   : StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Recovery-import marker {Filename} names an archive outside the expected " +
                "recovery-source namespace");

        long? terminalSequence = null;
        string terminalDigest = null;
        var terminalAnchorRequired = false;

        if(current)
        {
            var requiredText = lines[6]["terminalAnchorRequired=".Length..];
            var sequenceText = lines[7]["terminalSequence=".Length..];
            terminalDigest = lines[8]["terminalDigest=".Length..];
            if(!bool.TryParse(requiredText, out terminalAnchorRequired))
                throw new InvalidDataException(
                    $"Recovery-import marker {Filename} contains an invalid terminal-anchor requirement");

            if(sequenceText != "(none)")
            {
                if(!long.TryParse(sequenceText, NumberStyles.None,
                       CultureInfo.InvariantCulture, out var parsedSequence) ||
                   parsedSequence < 0)
                    throw new InvalidDataException(
                        $"Recovery-import marker {Filename} contains an invalid terminal sequence");

                terminalSequence = parsedSequence;
            }

            if(terminalDigest == "(none)")
                terminalDigest = null;

            ValidateTerminalState(terminalSequence, terminalDigest,
                terminalAnchorRequired);
        }

        return new ImportMarker(phase, fileHash, recordCount, archiveFilename,
            terminalSequence, terminalDigest, terminalAnchorRequired);
    }

    public static void EnsureTerminalStateMatches(ImportMarker marker,
        long sequence, string digest, bool anchorRequired)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if(marker.TerminalSequence != sequence ||
           !string.Equals(marker.TerminalDigest, digest,
               StringComparison.OrdinalIgnoreCase) ||
           marker.TerminalAnchorRequired != anchorRequired)
            throw new InvalidDataException(
                "Committed recovery source terminal state no longer matches its durable import marker");
    }

    private static void ValidateTerminalState(long? sequence, string digest,
        bool anchorRequired)
    {
        if(sequence.HasValue != (digest != null) ||
           sequence < 0 ||
           digest != null && (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) ||
           anchorRequired && !sequence.HasValue)
            throw new InvalidDataException(
                "Recovery import terminal state is incomplete or malformed");
    }

    internal enum ImportPhase
    {
        Pending,
        Committed,
        ArchiveDurable,
        AnchorRetirementAuthorised,
        AnchorRetired,
    }

    internal sealed record ImportMarker(ImportPhase Phase, string FileHash,
        int RecordCount, string ArchiveFilename, long? TerminalSequence = null,
        string TerminalDigest = null, bool TerminalAnchorRequired = false);
}
