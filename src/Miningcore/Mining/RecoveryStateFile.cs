using System.Text;

namespace Miningcore.Mining;

internal static class RecoveryStateFile
{
    internal const int MaximumBytes = 64 * 1024;
    internal const int MaximumLineCharacters = 16 * 1024;

    public static FileStream TryOpenExactEntry(string filename,
        Func<string, IEnumerable<string>> enumerateEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ArgumentNullException.ThrowIfNull(enumerateEntries);

        filename = Path.GetFullPath(filename);
        var directory = Path.GetDirectoryName(filename)!;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string exactEntry = null;

        // Materialise enumeration so failures while advancing a lazy iterator are not mistaken
        // for a successfully proven absence.
        foreach(var entry in enumerateEntries(directory).ToArray())
        {
            if(string.Equals(Path.GetFullPath(entry), filename, comparison))
            {
                exactEntry = entry;
                break;
            }
        }

        if(exactEntry == null)
            return null;

        try
        {
            var info = new FileInfo(exactEntry);
            if(info.LinkTarget != null)
                throw new InvalidDataException(
                    $"Recovery state entry {filename} is a symbolic link");

            var attributes = File.GetAttributes(exactEntry);
            if((attributes & FileAttributes.Directory) != 0)
                throw new InvalidDataException(
                    $"Recovery state entry {filename} is a directory, not a regular file");
            if((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    $"Recovery state entry {filename} is an unsupported reparse-point file");

            return new FileStream(exactEntry, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.SequentialScan);
        }
        catch(Exception ex) when(ex is FileNotFoundException or
                                  DirectoryNotFoundException)
        {
            throw new IOException(
                $"Recovery state entry {filename} disappeared after directory enumeration; " +
                "its absence cannot be proven", ex);
        }
    }

    public static string[] ReadAllLinesStable(FileStream stream,
        string filename, Func<string, IEnumerable<string>> enumerateEntries,
        Action<string> readCheckpoint = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        filename = Path.GetFullPath(filename);

        var identityBefore = RecoveryJournalFileIdentity.Read(stream);
        var lengthBefore = stream.Length;
        if(lengthBefore > MaximumBytes)
            throw new InvalidDataException(
                $"Recovery state file {filename} exceeds {MaximumBytes} bytes");

        var result = new List<string>();
        using(var reader = new StreamReader(stream,
                  new UTF8Encoding(false, true), false, 4096, leaveOpen: true))
        using(var lines = new BoundedLineReader(reader,
                  MaximumLineCharacters, $"Recovery state file {filename}"))
        {
            string line;
            while((line = lines.ReadLine()) != null)
                result.Add(line);
        }

        readCheckpoint?.Invoke(filename);

        var identityAfter = RecoveryJournalFileIdentity.Read(stream);
        var lengthAfter = stream.Length;
        if(identityAfter != identityBefore || lengthAfter != lengthBefore)
            throw new InvalidDataException(
                $"Recovery state file {filename} changed while it was being read");

        using var pathStream = TryOpenExactEntry(filename, enumerateEntries);
        if(pathStream == null ||
           RecoveryJournalFileIdentity.Read(pathStream) != identityAfter ||
           pathStream.Length != lengthAfter)
            throw new InvalidDataException(
                $"Recovery state path {filename} was replaced while it was being read");

        return result.ToArray();
    }
}
