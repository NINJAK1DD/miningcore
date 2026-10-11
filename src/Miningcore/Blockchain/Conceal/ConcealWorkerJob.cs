using System.Collections.Concurrent;

namespace Miningcore.Blockchain.Conceal;

public class ConcealWorkerJob
{
    public ConcealWorkerJob(string jobId, double difficulty)
    {
        Id = jobId;
        Difficulty = difficulty;
    }

    internal object DifficultyTemplate { get; set; }
    public string Id { get; }
    public uint Height { get; set; }
    public uint ExtraNonce { get; set; }
    public double Difficulty { get; set; }

    public readonly ConcurrentDictionary<string, bool> Submissions = new(StringComparer.OrdinalIgnoreCase);
}