using System.Globalization;
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

    internal void EnsureDirectoryDurable()
    {
        DurableDirectory.EnsureCreated(Path.GetDirectoryName(Filename)!,
            DirectorySync);
    }

    public ImportMarker TryRead()
    {
        try
        {
            return Read();
        }
        catch(FileNotFoundException)
        {
            return null;
        }
        catch(DirectoryNotFoundException)
        {
            return null;
        }
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
        string archiveFilename)
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

        var marker = new ImportMarker(ImportPhase.Pending,
            fileHash.ToUpperInvariant(), recordCount,
            Path.GetFullPath(archiveFilename));
        try
        {
            Write(marker, false);
            return marker;
        }
        catch(IOException) when(File.Exists(Filename))
        {
            existing = Read();
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
        ArgumentNullException.ThrowIfNull(marker);
        var committed = marker with { Phase = ImportPhase.Committed };
        Write(committed, true);
        return committed;
    }

    public void RemoveAfterRetirement()
    {
        var directory = Path.GetDirectoryName(Filename)!;
        File.Delete(Filename);
        if(Directory.Exists(directory))
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
            "miningcore-share-recovery-import-v1",
            $"recoveryPathSha256={ShareRecoveryFatalState.ComputeRecoveryPathHash(RecoveryFilename)}",
            $"phase={marker.Phase.ToString().ToLowerInvariant()}",
            $"fileHash={marker.FileHash.ToUpperInvariant()}",
            $"recordCount={marker.RecordCount.ToString(CultureInfo.InvariantCulture)}",
            $"archivePathBase64={Convert.ToBase64String(archiveBytes)}", string.Empty);

        try
        {
            using(var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(true);
            }

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

    private ImportMarker Read()
    {
        var lines = File.ReadAllLines(Filename, new UTF8Encoding(false, true));
        if(lines.Length != 6 ||
           lines[0] != "miningcore-share-recovery-import-v1" ||
           !lines[1].StartsWith("recoveryPathSha256=", StringComparison.Ordinal) ||
           !lines[2].StartsWith("phase=", StringComparison.Ordinal) ||
           !lines[3].StartsWith("fileHash=", StringComparison.Ordinal) ||
           !lines[4].StartsWith("recordCount=", StringComparison.Ordinal) ||
           !lines[5].StartsWith("archivePathBase64=", StringComparison.Ordinal))
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

        return new ImportMarker(phase, fileHash, recordCount, archiveFilename);
    }

    internal enum ImportPhase
    {
        Pending,
        Committed,
    }

    internal sealed record ImportMarker(ImportPhase Phase, string FileHash,
        int RecordCount, string ArchiveFilename);
}
