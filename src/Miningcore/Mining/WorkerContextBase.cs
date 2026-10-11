using Miningcore.Configuration;
using Miningcore.Nicehash.API;
using Miningcore.Time;
using Miningcore.VarDiff;
using System.Runtime.CompilerServices;
using Miningcore.Stratum;

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
    private TimeProvider assignmentTimeProvider = TimeProvider.System;
    private IssuedDifficulty assignment;
    private object assignmentSession = new();
    private long assignmentGeneration;
    private readonly ConditionalWeakTable<object, IssuedDifficulty> issuedDifficulties = new();
    private ConditionalWeakTable<object, JobSnapshot> jobSnapshots = new();
    private readonly Dictionary<(object Template, uint ExtraNonce), IssuedDifficulty> blobDifficulties = new();
    private sealed record JobSnapshot(IssuedDifficulty Assignment, object Job);
    internal virtual double MinimumDifficulty => 0;
    internal virtual double MaximumDifficulty => double.MaxValue;
    internal virtual bool RequiresIntegerDifficulty => false;
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
    public double Difficulty { get => difficulty; set => SetDifficulty(value); }

    private double ValidateAssignment(double value)
    {
        if(!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Difficulty must be finite and positive");
        return ValidateDifficulty(value);
    }

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
        difficulty = ValidateAssignment(difficulty);
        assignmentTimeProvider = timeProvider ?? TimeProvider.System;
        assignmentSession = new();
        assignment = new IssuedDifficulty(difficulty, assignmentTimeProvider, assignmentSession);
        assignmentGeneration = 0;
        previousDifficulty = null;
        jobSnapshots = new();
        blobDifficulties.Clear();
        pendingDifficulty = null;
        this.difficulty = difficulty;
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
        pendingDifficulty = ValidateAssignment(difficulty);
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
        difficulty = ValidateAssignment(difficulty);
        lock(this)
        {
            // An explicit assignment supersedes any deferred dynamic assignment.
            pendingDifficulty = null;
            if(difficulty == Difficulty)
                return;
            var nextGeneration = checked(assignmentGeneration + 1);
            assignment?.Retire();
            assignment = new IssuedDifficulty(difficulty, assignmentTimeProvider, assignmentSession);
            assignmentGeneration = nextGeneration;
            previousDifficulty = Difficulty;
            this.difficulty = difficulty;
        }
    }

    // Call under the worker assignment gate, before registry insertion/publication.
    // Shallow job copies deliberately retain the template's duplicate-proof set.
    internal T JobForDifficulty<T>(T template, Func<T, string, T> copy, string jobId) where T : class
    {
        ArgumentNullException.ThrowIfNull(template);
        lock(this)
        {
            assignment ??= new IssuedDifficulty(ValidateAssignment(Difficulty), assignmentTimeProvider, assignmentSession);
            if(jobSnapshots.TryGetValue(template, out var cached) && ReferenceEquals(cached.Assignment, assignment))
                return (T) cached.Job;
            var id = assignmentGeneration == 0 ? jobId : $"{jobId}-d{assignmentGeneration:x}";
            var job = copy(template, id);
            issuedDifficulties.Add(job, assignment);
            jobSnapshots.Remove(template);
            jobSnapshots.Add(template, new JobSnapshot(assignment, job));
            return job;
        }
    }

    internal void RegisterBlobDifficulty(object template, uint extraNonce)
    {
        lock(this)
        {
            assignment ??= new IssuedDifficulty(ValidateAssignment(Difficulty), assignmentTimeProvider, assignmentSession);
            blobDifficulties.TryAdd((template, extraNonce), assignment);
        }
    }

    internal void ForgetBlobDifficulty(object template, uint extraNonce)
    {
        lock(this)
            blobDifficulties.Remove((template, extraNonce));
    }

    internal bool TryValidateShareDifficulty(double proofDifficulty, bool blockCandidate, object job, out double credit)
    {
        IssuedDifficulty issued;
        lock(this)
        {
            if(job is ValueTuple<object, uint> blob)
                blobDifficulties.TryGetValue(blob, out issued);
            else
                issuedDifficulties.TryGetValue(job, out issued);
        }
        // Compatibility for direct job validators/custom callers without issuance
        // metadata: current target only. PreviousDifficulty/UTC never authorize work.
        credit = Difficulty;
        if(issued != null && !ReferenceEquals(issued.Session, assignmentSession))
            return false;
        issued ??= new IssuedDifficulty(Difficulty, assignmentTimeProvider, assignmentSession);
        return issued.TryValidate(proofDifficulty, blockCandidate, out credit);
    }

    internal double ValidateShareDifficulty(double proofDifficulty, bool blockCandidate, object job)
    {
        if(!TryValidateShareDifficulty(proofDifficulty, blockCandidate, job, out var credit))
            throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty or expired assignment ({proofDifficulty})");
        return credit;
    }

}
