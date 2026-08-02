using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Miningcore.Mining;

internal static class ShareRecoveryIncidentChain
{
    public const string EmptyPreviousDigest =
        "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly string EmptyLegacySetDigest = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(
            "Miningcore fatal incident legacy set v1\n")));

    public static ChainTip ReadTip(string directory, string stem,
        string latchFilename)
    {
        var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        var incidentPaths = entries
            .Where(path => Path.GetFileName(path).StartsWith(stem + ".",
                StringComparison.Ordinal) &&
                path.EndsWith(".incident", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var incidents = incidentPaths.Select(ReadEntry).ToArray();
        var legacy = incidents.Where(x => x.FormatVersion == 2)
            .OrderBy(x => Path.GetFileName(x.Filename), StringComparer.Ordinal)
            .ToArray();
        var current = incidents.Where(x => x.FormatVersion == 3)
            .OrderBy(x => x.Sequence)
            .ToArray();

        if(incidents.Any(x => x.FormatVersion is not (2 or 3)))
            throw new InvalidDataException(
                "Fatal incident collection contains an unsupported format version");

        var legacyDigest = ComputeLegacySetDigest(legacy);
        var previous = EmptyPreviousDigest;
        long expectedSequence = 1;

        foreach(var incident in current)
        {
            if(incident.Sequence != expectedSequence++)
                throw new InvalidDataException(
                    "Fatal incident chain contains a missing, duplicate or reordered sequence");
            if(!string.Equals(incident.PreviousDigest, previous,
                   StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Fatal incident {incident.IncidentId} does not link to the previous incident digest");
            if(incident.LegacyCount != legacy.Length ||
               !string.Equals(incident.LegacyDigest, legacyDigest,
                   StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Fatal incident {incident.IncidentId} does not anchor the complete legacy incident set");

            previous = incident.FileDigest;
        }

        using var latchStream = RecoveryStateFile.TryOpenExactEntry(latchFilename,
            Directory.EnumerateFileSystemEntries);
        if(latchStream == null)
        {
            if(incidents.Length != 0)
                throw new InvalidDataException(
                    "Fatal incident metadata exists without its authoritative fixed-name latch");

            return new ChainTip(0, EmptyPreviousDigest, 0,
                EmptyLegacySetDigest, 0);
        }

        var latch = ReadEntry(latchFilename, latchStream);
        if(latch.FormatVersion == 2)
        {
            if(current.Length != 0)
                throw new InvalidDataException(
                    "A legacy fatal latch cannot authorise chained incident metadata");
            if(!legacy.Any(x => string.Equals(x.IncidentId, latch.IncidentId,
                   StringComparison.Ordinal)))
                throw new InvalidDataException(
                    "The fatal latch references missing legacy incident metadata");

            return new ChainTip(0, EmptyPreviousDigest, legacy.Length,
                legacyDigest, legacy.Length);
        }

        if(latch.FormatVersion != 3 || current.Length == 0)
            throw new InvalidDataException(
                "The fatal latch has no valid chained incident tip");

        var latest = current[^1];
        if(latch.Sequence != latest.Sequence ||
           !string.Equals(latch.IncidentId, latest.IncidentId,
               StringComparison.Ordinal) ||
           !string.Equals(latch.ChainDigest, latest.FileDigest,
               StringComparison.OrdinalIgnoreCase) ||
           latch.ExpectedCount != incidents.Length ||
           latch.LegacyCount != legacy.Length ||
           !string.Equals(latch.LegacyDigest, legacyDigest,
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The fatal latch does not anchor the complete current incident collection");

        return new ChainTip(latest.Sequence, latest.FileDigest,
            legacy.Length, legacyDigest, incidents.Length);
    }

    public static string ComputeDigest(string content) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static IncidentEntry ReadEntry(string filename)
    {
        using var stream = RecoveryStateFile.TryOpenExactEntry(filename,
            Directory.EnumerateFileSystemEntries);
        if(stream == null)
            throw new IOException(
                $"Fatal incident entry disappeared during chain validation: {filename}");

        return ReadEntry(filename, stream);
    }

    private static IncidentEntry ReadEntry(string filename, FileStream stream)
    {
        stream.Position = 0;
        var fileDigest = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        var lines = RecoveryStateFile.ReadAllLinesStable(stream, filename,
            Directory.EnumerateFileSystemEntries);
        var metadata = ParseMetadata(lines, filename);
        if(!int.TryParse(Get(metadata, "formatVersion"), NumberStyles.None,
               CultureInfo.InvariantCulture, out var formatVersion))
            throw new InvalidDataException(
                $"Fatal incident metadata has no format version: {filename}");

        var incidentId = Get(metadata, "incidentId");
        if(string.IsNullOrWhiteSpace(incidentId))
            throw new InvalidDataException(
                $"Fatal incident metadata has no incident ID: {filename}");

        if(formatVersion == 2)
            return new IncidentEntry(filename, formatVersion, incidentId,
                0, null, 0, null, fileDigest, null, 0);

        if(formatVersion != 3 ||
           !long.TryParse(Get(metadata, "incidentSequence"), NumberStyles.None,
               CultureInfo.InvariantCulture, out var sequence) || sequence <= 0 ||
           !int.TryParse(Get(metadata, "legacyIncidentCount"), NumberStyles.None,
               CultureInfo.InvariantCulture, out var legacyCount) || legacyCount < 0)
            throw new InvalidDataException(
                $"Fatal incident chain metadata is malformed: {filename}");

        var previousDigest = Get(metadata, "previousIncidentDigest");
        var legacyDigest = Get(metadata, "legacyIncidentSetSha256");
        EnsureDigest(previousDigest, filename);
        EnsureDigest(legacyDigest, filename);

        var chainDigest = Get(metadata, "incidentChainDigest");
        if(chainDigest != null)
            EnsureDigest(chainDigest, filename);
        var expectedCount = 0;
        var expectedText = Get(metadata, "expectedIncidentCount");
        if(expectedText != null &&
           (!int.TryParse(expectedText, NumberStyles.None,
                CultureInfo.InvariantCulture, out expectedCount) || expectedCount < 1))
            throw new InvalidDataException(
                $"Fatal incident count anchor is malformed: {filename}");

        return new IncidentEntry(filename, formatVersion, incidentId,
            sequence, previousDigest, legacyCount, legacyDigest, fileDigest,
            chainDigest, expectedCount);
    }

    private static Dictionary<string, string> ParseMetadata(IEnumerable<string> lines,
        string filename)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach(var line in lines)
        {
            var separator = line.IndexOf('=');
            if(separator <= 0)
                continue;

            if(!result.TryAdd(line[..separator], line[(separator + 1)..]))
                throw new InvalidDataException(
                    $"Fatal incident metadata contains a duplicate key: {filename}");
        }

        return result;
    }

    private static string ComputeLegacySetDigest(IEnumerable<IncidentEntry> legacy)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(
            "Miningcore fatal incident legacy set v1\n"));
        foreach(var incident in legacy)
            hash.AppendData(Encoding.UTF8.GetBytes(
                $"{Path.GetFileName(incident.Filename)}\0{incident.FileDigest}\n"));

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Get(IReadOnlyDictionary<string, string> metadata,
        string key) => metadata.TryGetValue(key, out var value) ? value : null;

    private static void EnsureDigest(string digest, string filename)
    {
        if(digest?.Length != 64 || !digest.All(Uri.IsHexDigit))
            throw new InvalidDataException(
                $"Fatal incident chain digest is malformed: {filename}");
    }

    internal readonly record struct ChainTip(long Sequence, string Digest,
        int LegacyCount, string LegacyDigest, int ExistingCount);

    private sealed record IncidentEntry(string Filename, int FormatVersion,
        string IncidentId, long Sequence, string PreviousDigest,
        int LegacyCount, string LegacyDigest, string FileDigest,
        string ChainDigest, int ExpectedCount);
}
