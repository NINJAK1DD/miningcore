using Miningcore.Configuration;
using Miningcore.Nicehash.API;
using Miningcore.Time;
using Miningcore.VarDiff;

namespace Miningcore.Mining;

public class ShareStats
{
    public int ValidShares { get; set; }
    public int InvalidShares { get; set; }
}

public class WorkerContextBase
{
    private double? pendingDifficulty;
    private string userAgent;
    private VarDiffContext varDiff;
    private double difficulty;
    private double? previousDifficulty;
    internal virtual double MinimumDifficulty => 0;
    internal virtual double MaximumDifficulty => double.MaxValue;
    protected virtual double ValidateDifficulty(double value) => value;
    // One gate for the worker lifetime, including replacement/disabled contexts.
    // Never dispose while asynchronous assignment producers may still be waiting.
    internal SemaphoreSlim AssignmentGate { get; } = new(1, 1);

    public ShareStats Stats { get; set; }
    public VarDiffContext VarDiff
    {
        get => varDiff;
        internal set
        {
            if(!ReferenceEquals(varDiff, value))
                pendingDifficulty = null;
            varDiff = value;
        }
    }
    public DateTime Created { get; set; }
    public DateTime LastActivity { get; set; }
    public bool IsAuthorized { get; set; }
    public bool IsSubscribed { get; set; }

    /// <summary>
    /// Difficulty assigned to this worker, either static or updated through VarDiffManager
    /// </summary>
    public double Difficulty { get => difficulty; set => difficulty = ValidateDifficulty(value); }

    /// <summary>
    /// Previous difficulty assigned to this worker
    /// </summary>
    public double? PreviousDifficulty
    {
        get => previousDifficulty;
        set => previousDifficulty = value.HasValue ? ValidateDifficulty(value.Value) : null;
    }

    /// <summary>
    /// Usually a wallet address
    /// </summary>
    public virtual string Miner { get; set; }

    /// <summary>
    /// Arbitrary worker identififer for miners using multiple rigs
    /// </summary>
    public virtual string Worker { get; set; }

    /// <summary>
    /// UserAgent reported by Stratum
    /// </summary>
    public string UserAgent
    {
        get => userAgent;
        set
        {
            userAgent = value;

            IsNicehash = userAgent?.Contains(NicehashConstants.NicehashUA, StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    public bool IsNicehash { get; private set; }

    public void Init(double difficulty, VarDiffConfig varDiffConfig, IMasterClock clock, TimeProvider timeProvider = null)
    {
        difficulty = ValidateDifficulty(difficulty);
        pendingDifficulty = null;
        Difficulty = difficulty;
        LastActivity = clock.Now;
        Created = clock.Now;
        Stats = new ShareStats();

        VarDiff = varDiffConfig == null ? null : new VarDiffContext(timeProvider)
        {
            Config = varDiffConfig
        };
    }

    public void EnqueueNewDifficulty(double difficulty)
    {
        pendingDifficulty = ValidateDifficulty(difficulty);
    }

    public bool HasPendingDifficulty => pendingDifficulty.HasValue;

    public bool ApplyPendingDifficulty()
    {
        if(pendingDifficulty.HasValue)
        {
            SetDifficulty(pendingDifficulty.Value);
            pendingDifficulty = null;

            return true;
        }

        return false;
    }

    public void SetDifficulty(double difficulty)
    {
        difficulty = ValidateDifficulty(difficulty);
        // An explicit assignment supersedes any deferred dynamic assignment.
        pendingDifficulty = null;
        previousDifficulty = Difficulty;
        Difficulty = difficulty;
    }

}
