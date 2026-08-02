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
        var acknowledgementPaths = entries
            .Where(path => Path.GetFileName(path).StartsWith(stem + ".",
                StringComparison.Ordinal) &&
                path.EndsWith(".acknowledged", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var incidents = incidentPaths.Select(ReadEntry).ToArray();
        var acknowledgements = acknowledgementPaths.Select(ReadEntry).ToArray();
        if(acknowledgements.GroupBy(x => x.Sequence).Any(group =>
               group.Count() != 1))
            throw new InvalidDataException(
                "Fatal acknowledgement collection contains a duplicate incident sequence");
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

        foreach(var acknowledgement in acknowledgements)
            ValidateAcknowledgement(acknowledgement, current, legacy,
                legacyDigest);

        using var latchStream = RecoveryStateFile.TryOpenExactEntry(latchFilename,
            Directory.EnumerateFileSystemEntries);
        if(latchStream == null)
        {
            if(incidents.Length == 0 && acknowledgements.Length == 0)
                return new ChainTip(0, EmptyPreviousDigest, 0,
                    EmptyLegacySetDigest, 0);

            if(incidents.Length == 0 || acknowledgements.Length == 0)
                throw new InvalidDataException(
                    "Fatal incident evidence has neither an active latch nor a complete acknowledged anchor");

            var latestAcknowledgement = acknowledgements
                .OrderBy(x => x.Sequence)
                .Last();
            var latestIncident = current.LastOrDefault();
            if(latestIncident == null)
            {
                if(acknowledgements.Length != 1 ||
                   latestAcknowledgement.FormatVersion != 4 ||
                   latestAcknowledgement.ExpectedCount != incidents.Length ||
                   latestAcknowledgement.LegacyCount != legacy.Length ||
                   !string.Equals(latestAcknowledgement.ChainDigest,
                       legacyDigest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "The acknowledged legacy fatal anchor does not cover the complete retained incident collection");

                return new ChainTip(0, EmptyPreviousDigest, legacy.Length,
                    legacyDigest, incidents.Length);
            }

            if(latestAcknowledgement.Sequence != latestIncident.Sequence ||
               latestAcknowledgement.ExpectedCount != incidents.Length ||
               !string.Equals(latestAcknowledgement.ChainDigest,
                   latestIncident.FileDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The latest acknowledged fatal anchor does not cover the complete retained incident collection");

            return new ChainTip(latestIncident.Sequence,
                latestIncident.FileDigest, legacy.Length, legacyDigest,
                incidents.Length);
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
        var commonTipMatches = latch.Sequence == latest.Sequence &&
            string.Equals(latch.IncidentId, latest.IncidentId,
                StringComparison.Ordinal) &&
            latch.ExpectedCount == incidents.Length &&
            latch.LegacyCount == legacy.Length &&
            string.Equals(latch.LegacyDigest, legacyDigest,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(latch.PreviousDigest, latest.PreviousDigest,
                StringComparison.OrdinalIgnoreCase);
        var completed = commonTipMatches &&
            string.Equals(latch.ChainDigest, latest.FileDigest,
                StringComparison.OrdinalIgnoreCase);
        var completionCanBeResumed = commonTipMatches &&
            string.Equals(latch.DetailState, "hash-pending",
                StringComparison.Ordinal) &&
            string.Equals(latest.DetailState, "complete",
                StringComparison.Ordinal);

        if(!completed && !completionCanBeResumed)
            throw new InvalidDataException(
                "The fatal latch does not anchor the complete current incident collection");

        return new ChainTip(latest.Sequence, latest.FileDigest,
            legacy.Length, legacyDigest, incidents.Length);
    }

    public static string BuildAcknowledgementFilename(string directory,
        string stem, ChainTip tip) => Path.Combine(directory,
        $"{stem}.{tip.Sequence.ToString(CultureInfo.InvariantCulture)}-" +
        $"{tip.Digest.ToLowerInvariant()}.acknowledged");

    public static string BuildLegacyAcknowledgementFilename(string directory,
        string stem, ChainTip tip) => Path.Combine(directory,
        $"{stem}.legacy-{tip.LegacyDigest.ToLowerInvariant()}.acknowledged");

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
                0, null, 0, null, fileDigest, null, 0, null);

        if(formatVersion == 4)
        {
            if(!int.TryParse(Get(metadata, "legacyIncidentCount"),
                   NumberStyles.None, CultureInfo.InvariantCulture,
                   out var acknowledgedLegacyCount) || acknowledgedLegacyCount < 1 ||
               !int.TryParse(Get(metadata, "expectedIncidentCount"),
                   NumberStyles.None, CultureInfo.InvariantCulture,
                   out var acknowledgedExpectedCount) || acknowledgedExpectedCount < 1 ||
               !string.Equals(Get(metadata, "acknowledgementKind"),
                   "legacy-v2-set", StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Legacy fatal acknowledgement is malformed: {filename}");

            var acknowledgedLegacyDigest = Get(metadata,
                "legacyIncidentSetSha256");
            var acknowledgedChainDigest = Get(metadata,
                "incidentChainDigest");
            EnsureDigest(acknowledgedLegacyDigest, filename);
            EnsureDigest(acknowledgedChainDigest, filename);
            return new IncidentEntry(filename, formatVersion, incidentId,
                0, null, acknowledgedLegacyCount, acknowledgedLegacyDigest,
                fileDigest, acknowledgedChainDigest,
                acknowledgedExpectedCount, null);
        }

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
            chainDigest, expectedCount, Get(metadata, "detailState"));
    }

    private static void ValidateAcknowledgement(IncidentEntry acknowledgement,
        IReadOnlyCollection<IncidentEntry> current,
        IReadOnlyCollection<IncidentEntry> legacy,
        string legacyDigest)
    {
        if(acknowledgement.FormatVersion == 4)
        {
            if(!legacy.Any(x => string.Equals(x.IncidentId,
                   acknowledgement.IncidentId, StringComparison.Ordinal)) ||
               acknowledgement.LegacyCount != legacy.Count ||
               acknowledgement.ExpectedCount != legacy.Count ||
               !string.Equals(acknowledgement.LegacyDigest, legacyDigest,
                   StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(acknowledgement.ChainDigest, legacyDigest,
                   StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Legacy fatal acknowledgement does not anchor its exact incident set: " +
                    acknowledgement.Filename);

            return;
        }

        if(acknowledgement.FormatVersion != 3 ||
           acknowledgement.ChainDigest == null ||
           acknowledgement.ExpectedCount < 1)
            throw new InvalidDataException(
                $"Fatal acknowledgement is malformed: {acknowledgement.Filename}");

        var incident = current.SingleOrDefault(x =>
            x.Sequence == acknowledgement.Sequence);
        if(incident == null ||
           !string.Equals(incident.IncidentId, acknowledgement.IncidentId,
               StringComparison.Ordinal) ||
           !string.Equals(incident.FileDigest, acknowledgement.ChainDigest,
               StringComparison.OrdinalIgnoreCase) ||
           acknowledgement.ExpectedCount != legacy.Count + acknowledgement.Sequence ||
           acknowledgement.LegacyCount != legacy.Count ||
           !string.Equals(acknowledgement.LegacyDigest, legacyDigest,
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Fatal acknowledgement does not anchor its exact incident-chain prefix: " +
                acknowledgement.Filename);
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
        string ChainDigest, int ExpectedCount, string DetailState);
}
