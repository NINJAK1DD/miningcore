using System.Globalization;
using System.Text;

namespace Miningcore.Mining;

/// <summary>
/// Independently records the last force-flushed recovery-journal frame. The chained journal
/// detects modification inside retained data; this anchor also detects deletion of complete
/// terminal frames, which would otherwise leave a valid shorter prefix.
/// </summary>
internal sealed class ShareRecoveryTerminalState
{
    public ShareRecoveryTerminalState(string recoveryFilename, string stateDirectory)
    {
        RecoveryFilename = Path.GetFullPath(recoveryFilename);
        var pathHash = ShareRecoveryFatalState.ComputeRecoveryPathHash(RecoveryFilename);
        Filename = Path.Combine(Path.GetFullPath(stateDirectory),
            "share-recovery-terminal", pathHash + ".tail");
    }

    public string RecoveryFilename { get; }
    public string Filename { get; }

    public void Write(long sequence, string digest)
    {
        if(sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);

        var directory = Path.GetDirectoryName(Filename)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(Filename)}.{Guid.NewGuid():N}.tmp");
        var content = string.Join('\n',
            "miningcore-share-recovery-terminal-v1",
            $"recoveryPathSha256={ShareRecoveryFatalState.ComputeRecoveryPathHash(RecoveryFilename)}",
            $"sequence={sequence.ToString(CultureInfo.InvariantCulture)}",
            $"digest={digest.ToUpperInvariant()}", string.Empty);

        try
        {
            using(var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temporary, Filename, true);
            ShareRecoveryFatalState.SyncDirectoryWhereSupported(directory);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // The published anchor or original failure remains authoritative.
            }
        }
    }

    public void EnsureConsistent(long sequence, string digest)
    {
        Terminal expected;

        try
        {
            expected = Read();
        }
        catch(FileNotFoundException)
        {
            // Backward-compatible adoption: an existing pre-anchor journal is validated in full
            // and gains an anchor on its next Miningcore append.
            return;
        }

        if(expected.Sequence != sequence ||
           !string.Equals(expected.Digest, digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Recovery journal {RecoveryFilename} ends at frame {sequence} [{digest}], " +
                $"but its independent terminal anchor expects frame {expected.Sequence} " +
                $"[{expected.Digest}]. Preserve both files for reconciliation; refusing a " +
                "shortened, replaced, or incompletely committed journal.");
    }

    public void EnsureJournalMayBeMissing()
    {
        if(File.Exists(Filename))
            throw new InvalidDataException(
                $"Recovery journal {RecoveryFilename} is missing while its independent " +
                $"terminal anchor {Filename} remains. Preserve the anchor and surrounding " +
                "storage for reconciliation.");
    }

    public void RemoveAfterArchive()
    {
        try
        {
            File.Delete(Filename);
            var directory = Path.GetDirectoryName(Filename)!;
            if(Directory.Exists(directory))
                ShareRecoveryFatalState.SyncDirectoryWhereSupported(directory);
        }
        catch(FileNotFoundException)
        {
        }
    }

    private Terminal Read()
    {
        var lines = File.ReadAllLines(Filename, new UTF8Encoding(false, true));

        if(lines.Length != 4 ||
           lines[0] != "miningcore-share-recovery-terminal-v1" ||
           !lines[1].StartsWith("recoveryPathSha256=", StringComparison.Ordinal) ||
           !lines[2].StartsWith("sequence=", StringComparison.Ordinal) ||
           !lines[3].StartsWith("digest=", StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Recovery-journal terminal anchor {Filename} is malformed");

        var expectedPath = ShareRecoveryFatalState.ComputeRecoveryPathHash(
            RecoveryFilename);
        var actualPath = lines[1]["recoveryPathSha256=".Length..];
        var sequenceText = lines[2]["sequence=".Length..];
        var digest = lines[3]["digest=".Length..];

        if(!string.Equals(actualPath, expectedPath, StringComparison.Ordinal) ||
           !long.TryParse(sequenceText, NumberStyles.None,
               CultureInfo.InvariantCulture, out var sequence) ||
           digest.Length != 64 || !digest.All(Uri.IsHexDigit))
            throw new InvalidDataException(
                $"Recovery-journal terminal anchor {Filename} contains invalid identity or tail data");

        return new Terminal(sequence, digest);
    }

    private readonly record struct Terminal(long Sequence, string Digest);
}
