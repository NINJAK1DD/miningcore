using System.Security.Cryptography;
using System.Text;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Newtonsoft.Json;

namespace Miningcore.Mining;

internal sealed record ShareRecoveryVerificationSummary(int IncidentCount,
    int CompleteCount, int IncompleteCount, int InvalidCount, bool LatchPresent)
{
    public bool IsSuccessful => InvalidCount == 0 && IncompleteCount == 0 &&
        (!LatchPresent || IncidentCount > 0);
}

internal static class ShareRecoveryIncidentVerifier
{
    private const long MaximumMetadataBytes = 64 * 1024;
    private const int MaximumSidecarLineCharacters = 1_048_576;

    public static ShareRecoveryVerificationSummary Verify(ClusterConfig config,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(output);

        var recoveryFilename = ShareRecoveryFatalState.ResolveRecoveryFilename(config);
        var stateDirectory = ShareRecoveryFatalState.ResolveStateDirectory(config);
        var recoveryPathHash = ShareRecoveryFatalState.ComputeRecoveryPathHash(
            recoveryFilename);
        var fatalDirectory = Path.Combine(stateDirectory, "share-recovery-fatal");
        var stem = recoveryPathHash;
        var latchFilename = Path.Combine(fatalDirectory, stem + ".fatal");

        output.WriteLine("Miningcore share-recovery incident verification");
        output.WriteLine($"recoveryFile={recoveryFilename}");
        output.WriteLine($"stateDirectory={stateDirectory}");
        output.WriteLine($"fatalLatch={latchFilename}");

        string[] stateEntries;

        try
        {
            stateEntries = Directory.GetFileSystemEntries(fatalDirectory);
        }
        catch(DirectoryNotFoundException)
        {
            output.WriteLine("No share-recovery fatal-state directory exists for this configuration.");
            output.WriteLine("RESULT: no incidents found.");
            return new ShareRecoveryVerificationSummary(0, 0, 0, 0, false);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"ERROR: Unable to enumerate fatal-state evidence: {ex.Message}");
            output.WriteLine("RESULT: incident evidence cannot be verified; preserve all files and do not clear the fatal latch.");
            return new ShareRecoveryVerificationSummary(0, 0, 0, 1, false);
        }

        var latchPresent = stateEntries.Any(entry =>
            PathsEqual(entry, latchFilename));
        Dictionary<string, string> latch = null;
        var globalErrors = new List<string>();
        var latchIncomplete = false;

        if(latchPresent)
        {
            latch = ReadMetadata(latchFilename, globalErrors);
            RequireEqual(latch, "formatVersion", "2", globalErrors);
            RequireEqual(latch, "recoveryFile", recoveryFilename, globalErrors);
            RequireEqual(latch, "recoveryPathSha256", recoveryPathHash,
                globalErrors);
            var latchDetailState = GetValue(latch, "detailState");
            latchIncomplete = latchDetailState is "hash-pending" or "incomplete";

            if(latchDetailState is not ("complete" or "not-required" or
               "hash-pending" or "incomplete"))
                globalErrors.Add(
                    $"The fatal latch has an unsupported detailState: {latchDetailState ?? "(missing)"}.");

            output.WriteLine($"fatalLatchPresent=true");
            output.WriteLine($"fatalLatchIncident={GetValue(latch, "incidentId") ?? "(unreadable)"}");
            output.WriteLine($"fatalLatchDetailState={GetValue(latch, "detailState") ?? "(unreadable)"}");
        }
        else
            output.WriteLine("fatalLatchPresent=false");

        string[] incidentFiles;

        try
        {
            incidentFiles = stateEntries
                .Where(path => Path.GetFileName(path).StartsWith(stem + ".",
                    StringComparison.Ordinal) &&
                    path.EndsWith(".incident", StringComparison.Ordinal))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            globalErrors.Add($"Unable to enumerate incident metadata: {ex.Message}");
            incidentFiles = Array.Empty<string>();
        }

        var complete = 0;
        var incomplete = 0;
        var invalid = 0;
        var incidentIds = new HashSet<string>(StringComparer.Ordinal);
        var incompleteIncidentIds = new HashSet<string>(StringComparer.Ordinal);

        foreach(var incidentFile in incidentFiles)
        {
            var result = VerifyIncident(incidentFile, fatalDirectory, stem,
                recoveryFilename, recoveryPathHash);
            var status = result.Errors.Count > 0
                ? "INVALID"
                : result.Incomplete
                    ? "INCOMPLETE"
                    : "COMPLETE";

            output.WriteLine();
            output.WriteLine($"incident={result.IncidentId ?? Path.GetFileName(incidentFile)} status={status}");
            output.WriteLine($"metadata={incidentFile}");
            output.WriteLine($"detailState={result.DetailState ?? "(missing)"}");

            if(result.DetailFilename != null)
                output.WriteLine($"detailFile={result.DetailFilename}");

            if(result.ExpectedHash != null)
                output.WriteLine($"expectedSha256={result.ExpectedHash}");

            if(result.ActualHash != null)
                output.WriteLine($"actualSha256={result.ActualHash}");

            if(result.DecodedRecords.HasValue)
                output.WriteLine($"decodedRecords={result.DecodedRecords}");

            foreach(var error in result.Errors)
                output.WriteLine($"ERROR: {error}");

            if(result.Errors.Count > 0)
                invalid++;
            else if(result.Incomplete)
            {
                incomplete++;
                if(result.IncidentId != null)
                    incompleteIncidentIds.Add(result.IncidentId);
            }
            else
                complete++;

            if(result.IncidentId != null)
                incidentIds.Add(result.IncidentId);
        }

        var latchIncidentId = GetValue(latch, "incidentId");

        if(latchPresent && !string.IsNullOrWhiteSpace(latchIncidentId) &&
           !incidentIds.Contains(latchIncidentId))
            globalErrors.Add(
                $"The fatal latch references incident {latchIncidentId}, but its .incident metadata file is missing.");

        if(latchPresent && !latchIncomplete &&
           !string.IsNullOrWhiteSpace(latchIncidentId))
        {
            var matchingIncident = incidentFiles.SingleOrDefault(path =>
                string.Equals(Path.GetFileName(path),
                    $"{stem}.{latchIncidentId}.incident",
                    StringComparison.Ordinal));

            if(matchingIncident != null)
            {
                var readErrors = new List<string>();
                var incidentMetadata = ReadMetadata(matchingIncident, readErrors);

                if(readErrors.Count == 0)
                {
                    foreach(var key in new[]
                            {
                                "formatVersion", "incidentId", "createdUtc",
                                "failureCategory", "recoveryFile",
                                "recoveryPathSha256", "shareCount", "pools",
                                "detailFile", "detailSha256", "detailState",
                            })
                    {
                        if(!string.Equals(GetValue(latch, key),
                               GetValue(incidentMetadata, key),
                               StringComparison.Ordinal))
                            globalErrors.Add(
                                $"The fatal latch does not match its incident metadata for key '{key}'.");
                    }
                }
            }
        }

        if(latchPresent && string.IsNullOrWhiteSpace(latchIncidentId))
            globalErrors.Add("The fatal latch has no readable incidentId.");

        foreach(var error in globalErrors)
            output.WriteLine($"ERROR: {error}");

        invalid += globalErrors.Count > 0 ? 1 : 0;
        var unmatchedIncompleteLatch = latchIncomplete &&
            (latchIncidentId == null ||
             !incompleteIncidentIds.Contains(latchIncidentId));
        var summary = new ShareRecoveryVerificationSummary(incidentFiles.Length,
            complete, incomplete + (unmatchedIncompleteLatch ? 1 : 0), invalid,
            latchPresent);

        output.WriteLine();
        output.WriteLine($"SUMMARY: incidents={summary.IncidentCount} complete={summary.CompleteCount} incomplete={summary.IncompleteCount} invalid={summary.InvalidCount}");

        if(summary.IsSuccessful)
        {
            output.WriteLine("RESULT: recorded evidence is structurally complete and its hashes match.");
            output.WriteLine("This does not prove PostgreSQL reconciliation. Compare every incident with PostgreSQL before removing an active fatal latch.");
        }
        else
            output.WriteLine("RESULT: incident evidence is incomplete or invalid; preserve all files and do not clear the fatal latch.");

        return summary;
    }

    private static IncidentVerification VerifyIncident(string metadataFilename,
        string fatalDirectory, string stem, string recoveryFilename,
        string recoveryPathHash)
    {
        var errors = new List<string>();
        var metadata = ReadMetadata(metadataFilename, errors);
        var incidentId = GetValue(metadata, "incidentId");
        var detailState = GetValue(metadata, "detailState");
        var detailFilename = NormalizeOptionalValue(GetValue(metadata, "detailFile"));
        var expectedHash = NormalizeOptionalValue(GetValue(metadata, "detailSha256"));
        string actualHash = null;
        int? decodedRecords = null;
        var incomplete = false;

        RequireEqual(metadata, "formatVersion", "2", errors);
        RequireEqual(metadata, "recoveryFile", recoveryFilename, errors);
        RequireEqual(metadata, "recoveryPathSha256", recoveryPathHash, errors);

        if(string.IsNullOrWhiteSpace(incidentId))
            errors.Add("incidentId is missing.");
        else
        {
            var expectedMetadata = Path.Combine(fatalDirectory,
                $"{stem}.{incidentId}.incident");

            if(!PathsEqual(expectedMetadata, metadataFilename))
                errors.Add("incidentId does not match the metadata filename.");
        }

        if(!int.TryParse(GetValue(metadata, "shareCount"), out var expectedCount) ||
           expectedCount < 0)
        {
            errors.Add("shareCount is missing or invalid.");
            expectedCount = -1;
        }

        switch(detailState)
        {
            case "not-required":
                if(detailFilename != null || expectedHash != null)
                    errors.Add("not-required evidence unexpectedly references a detail file or hash.");
                break;

            case "complete":
                VerifyCompleteSidecar(fatalDirectory, stem, incidentId,
                    detailFilename, expectedHash, expectedCount, errors,
                    out actualHash, out decodedRecords);
                break;

            case "hash-pending":
            case "incomplete": // accepted for incidents produced by the earlier v2 writer
                incomplete = true;

                if(detailState == "hash-pending" && expectedHash != null)
                    errors.Add("Hash-pending evidence unexpectedly declares detailSha256.");

                if(detailFilename != null)
                {
                    var expectedDetailFilename = string.IsNullOrWhiteSpace(incidentId)
                        ? null
                        : Path.Combine(fatalDirectory,
                            $"{stem}.{incidentId}.shares");

                    if(expectedDetailFilename == null ||
                       !PathsEqual(expectedDetailFilename, detailFilename))
                        errors.Add("detailFile does not match the incident identity or state directory.");
                    else if(File.Exists(detailFilename))
                    {
                        InspectSidecar(detailFilename, errors, out actualHash,
                            out decodedRecords);

                        if(expectedHash?.Length == 64 && actualHash != null &&
                           !string.Equals(expectedHash, actualHash,
                               StringComparison.OrdinalIgnoreCase))
                            errors.Add("The exact-share sidecar SHA-256 does not match its metadata.");
                    }
                }
                break;

            default:
                errors.Add($"detailState is missing or unsupported: {detailState ?? "(missing)"}.");
                break;
        }

        return new IncidentVerification(incidentId, detailState, detailFilename,
            expectedHash, actualHash, decodedRecords, incomplete, errors);
    }

    private static void VerifyCompleteSidecar(string fatalDirectory, string stem,
        string incidentId, string detailFilename, string expectedHash,
        int expectedCount, ICollection<string> errors, out string actualHash,
        out int? decodedRecords)
    {
        actualHash = null;
        decodedRecords = null;

        if(string.IsNullOrWhiteSpace(incidentId) ||
           string.IsNullOrWhiteSpace(detailFilename))
        {
            errors.Add("Complete evidence has no usable detailFile.");
            return;
        }

        var expectedDetailFilename = Path.Combine(fatalDirectory,
            $"{stem}.{incidentId}.shares");

        if(!PathsEqual(expectedDetailFilename, detailFilename))
        {
            errors.Add("detailFile does not match the incident identity or state directory.");
            return;
        }

        if(expectedHash?.Length != 64 ||
           !expectedHash.All(Uri.IsHexDigit))
            errors.Add("Complete evidence has no valid detailSha256.");

        if(!File.Exists(detailFilename))
        {
            errors.Add("The referenced exact-share sidecar is missing.");
            return;
        }

        InspectSidecar(detailFilename, errors, out actualHash,
            out decodedRecords);

        if(expectedHash?.Length == 64 && actualHash != null &&
           !string.Equals(expectedHash, actualHash,
               StringComparison.OrdinalIgnoreCase))
            errors.Add("The exact-share sidecar SHA-256 does not match its metadata.");

        if(expectedCount >= 0 && decodedRecords.HasValue &&
           decodedRecords.Value != expectedCount)
            errors.Add(
                $"The sidecar contains {decodedRecords.Value} records, but metadata declares {expectedCount}.");
    }

    private static void InspectSidecar(string filename,
        ICollection<string> errors, out string actualHash,
        out int? decodedRecords)
    {
        actualHash = null;
        decodedRecords = null;

        try
        {
            using(var stream = File.OpenRead(filename))
                actualHash = Convert.ToHexString(SHA256.HashData(stream));

            var count = 0;
            using var reader = new StreamReader(filename, Encoding.UTF8, true,
                4096);
            string line;

            while((line = reader.ReadLine()) != null)
            {
                if(line.Length > MaximumSidecarLineCharacters)
                {
                    errors.Add("The sidecar contains an oversized record line.");
                    return;
                }

                const string prefix = "shareJsonBase64=";

                if(!line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    errors.Add($"Sidecar record {count + 1} has an invalid prefix.");
                    return;
                }

                try
                {
                    var json = Encoding.UTF8.GetString(
                        Convert.FromBase64String(line[prefix.Length..]));
                    _ = JsonConvert.DeserializeObject<Share>(json) ??
                        throw new JsonSerializationException(
                            "Decoded share is null");
                }
                catch(Exception ex) when(ex is FormatException or JsonException)
                {
                    errors.Add($"Sidecar record {count + 1} cannot be decoded: {ex.Message}");
                    return;
                }

                count++;
            }

            decodedRecords = count;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Unable to read exact-share sidecar: {ex.Message}");
        }
    }

    private static Dictionary<string, string> ReadMetadata(string filename,
        ICollection<string> errors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var info = new FileInfo(filename);

            if(info.Length > MaximumMetadataBytes)
            {
                errors.Add($"Metadata exceeds {MaximumMetadataBytes} bytes: {filename}");
                return result;
            }

            foreach(var line in File.ReadLines(filename))
            {
                var separator = line.IndexOf('=');

                if(separator <= 0)
                    continue;

                var key = line[..separator];
                var value = line[(separator + 1)..];

                if(!result.TryAdd(key, value))
                    errors.Add($"Metadata contains duplicate key '{key}': {filename}");
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Unable to read metadata {filename}: {ex.Message}");
        }

        return result;
    }

    private static void RequireEqual(IReadOnlyDictionary<string, string> values,
        string key, string expected, ICollection<string> errors)
    {
        if(!values.TryGetValue(key, out var actual) ||
           !string.Equals(actual, expected, StringComparison.Ordinal))
            errors.Add($"Metadata {key} does not match the configured recovery state.");
    }

    private static string GetValue(IReadOnlyDictionary<string, string> values,
        string key) => values != null && values.TryGetValue(key, out var value)
        ? value
        : null;

    private static string NormalizeOptionalValue(string value) =>
        string.IsNullOrWhiteSpace(value) || value == "(none)" ? null : value;

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch(Exception ex) when(ex is ArgumentException or NotSupportedException or
                                  PathTooLongException)
        {
            return false;
        }
    }

    private sealed record IncidentVerification(string IncidentId,
        string DetailState, string DetailFilename, string ExpectedHash,
        string ActualHash, int? DecodedRecords, bool Incomplete,
        IReadOnlyCollection<string> Errors);
}
