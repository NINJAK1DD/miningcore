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

internal enum DifficultyOrigin { Automatic, Static, Device }

public class WorkerContextBase
{
    internal DifficultyOrigin PendingDifficultyOrigin { get; private set; } = DifficultyOrigin.Automatic;
    internal double? ExplicitDifficultyFloor { get; private set; }
    internal double MinimumVarDiffDifficulty => Math.Max(MinimumDifficulty, ExplicitDifficultyFloor ?? 0);

    private double? pendingDifficulty;
    private string userAgent;
    private VarDiffContext varDiff;
    private double difficulty;
    private double? previousDifficulty;
    private TimeProvider assignmentTimeProvider = TimeProvider.System;
    private IssuedDifficulty assignment;
    private object assignmentSession = new();
    private long assignmentGeneration;
    private readonly object assignmentSync = new();
    private readonly AsyncLocal<ShareReceipt?> shareReceipt = new();
    private TimeSpan difficultyGrace = IssuedDifficulty.Grace;
    private bool assignmentsClosed;
    internal Miningcore.Blockchain.BitcoinBlake2b.DifficultyRequestBudget DifficultyRequests { get; private set; }
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
    /// Legacy diagnostic metadata. Never used to authorize or credit a share.
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

    public virtual void Init(double difficulty, VarDiffConfig varDiffConfig, IMasterClock clock, TimeProvider timeProvider = null,
        int difficultyGraceSeconds = 30)
    {
        if(difficultyGraceSeconds is < 0 or > 300)
            throw new ArgumentOutOfRangeException(nameof(difficultyGraceSeconds));
        difficulty = ValidateAssignment(difficulty);
        assignmentTimeProvider = timeProvider ?? TimeProvider.System;
        difficultyGrace = TimeSpan.FromSeconds(difficultyGraceSeconds);
        assignmentsClosed = false;
        DifficultyRequests = new(assignmentTimeProvider);
        assignmentSession = new();
        assignment = new IssuedDifficulty(difficulty, assignmentTimeProvider, assignmentSession, difficultyGrace);
        assignmentGeneration = 0;
        previousDifficulty = null;
        jobSnapshots = new();
        blobDifficulties.Clear();
        pendingDifficulty = null;
        this.difficulty = difficulty;
        ExplicitDifficultyFloor = null;
        PendingDifficultyOrigin = DifficultyOrigin.Automatic;
        LastActivity = clock.Now;
        Created = clock.Now;
        Stats = new ShareStats();

        VarDiff = varDiffConfig == null ? null : new VarDiffContext(timeProvider)
        {
            Config = varDiffConfig
        };
    }

    public void EnqueueNewDifficulty(double difficulty) => EnqueueNewDifficulty(difficulty, DifficultyOrigin.Automatic);

    internal void EnqueueNewDifficulty(double difficulty, DifficultyOrigin origin)
    {
        difficulty = ValidateAssignment(difficulty);
        pendingDifficulty = ValidateAssignment(origin == DifficultyOrigin.Automatic
            ? Math.Max(difficulty, ExplicitDifficultyFloor ?? 0) : difficulty);
        PendingDifficultyOrigin = origin;
    }

    internal void DiscardPendingDifficulty() => pendingDifficulty = null;

    public bool HasPendingDifficulty => pendingDifficulty.HasValue;
    // Read only while owning the assignment gate, like ApplyPendingDifficulty.
    internal double? PendingDifficulty => pendingDifficulty;

    internal Action BeforeApplyPendingDifficulty { get; set; }
    internal Action AfterApplyPendingDifficulty { get; set; }

    public virtual bool ApplyPendingDifficulty()
    {
        BeforeApplyPendingDifficulty?.Invoke();
        if(pendingDifficulty.HasValue)
        {
            SetDifficulty(pendingDifficulty.Value, PendingDifficultyOrigin);
            pendingDifficulty = null;
            AfterApplyPendingDifficulty?.Invoke();

            return true;
        }

        AfterApplyPendingDifficulty?.Invoke();
        return false;
    }

    public void SetDifficulty(double difficulty) => SetDifficulty(difficulty, DifficultyOrigin.Automatic);

    internal void SetDifficulty(double difficulty, DifficultyOrigin origin)
    {
        difficulty = ValidateAssignment(difficulty);
        difficulty = ValidateAssignment(origin == DifficultyOrigin.Automatic
            ? Math.Max(difficulty, ExplicitDifficultyFloor ?? 0) : difficulty);
        lock(assignmentSync)
        {
            // Record provenance even for a no-op: accepting a device floor can
            // constrain future automatic assignments without retiring current work.
            pendingDifficulty = null;
            if(origin is DifficultyOrigin.Static or DifficultyOrigin.Device)
                ExplicitDifficultyFloor = difficulty;
            if(difficulty == Difficulty)
                return;
            var nextGeneration = checked(assignmentGeneration + 1);
            assignment?.Retire();
            assignment = new IssuedDifficulty(difficulty, assignmentTimeProvider, assignmentSession, difficultyGrace);
            assignmentGeneration = nextGeneration;
            previousDifficulty = Difficulty;
            this.difficulty = difficulty;
        }
    }

    // Call under the worker assignment gate, before registry insertion/publication.
    // Shallow job copies deliberately retain the template's duplicate-proof set.
    internal T JobForDifficulty<T>(T template, Func<T, string, T> copy, string jobId,
        Func<string> nextJobId = null, bool preserveId = false) where T : class
    {
        ArgumentNullException.ThrowIfNull(template);
        CandidateShareCredit.Prepare(template);
        lock(assignmentSync)
        {
            assignment ??= new IssuedDifficulty(ValidateAssignment(Difficulty), assignmentTimeProvider, assignmentSession, difficultyGrace);
            if(jobSnapshots.TryGetValue(template, out var cached) && ReferenceEquals(cached.Assignment, assignment))
                return (T) cached.Job;
            // Fresh templates retain their native ID. Only reissues need another
            // ID, allocated from the same manager counter as template IDs.
            var id = cached == null || preserveId ? jobId :
                (nextJobId?.Invoke() ?? $"{jobId}-d{assignmentGeneration:x}");
            var job = cached == null && !issuedDifficulties.TryGetValue(template, out _) ? template : copy(template, id);
            CandidateShareCredit.Copy(template, job);
            issuedDifficulties.Add(job, assignment);
            jobSnapshots.Remove(template);
            jobSnapshots.Add(template, new JobSnapshot(assignment, job));
            return job;
        }
    }

    internal void RegisterBlobDifficulty(object template, uint extraNonce)
    {
        CandidateShareCredit.Prepare(template);
        lock(assignmentSync)
        {
            assignment ??= new IssuedDifficulty(ValidateAssignment(Difficulty), assignmentTimeProvider, assignmentSession, difficultyGrace);
            blobDifficulties.TryAdd((template, extraNonce), assignment);
        }
    }

    internal void ForgetBlobDifficulty(object template, uint extraNonce)
    {
        lock(assignmentSync)
            blobDifficulties.Remove((template, extraNonce));
    }

    internal bool TryValidateShareDifficulty(double proofDifficulty, bool blockCandidate, object job, out double credit)
    {
        IssuedDifficulty issued;
        lock(assignmentSync)
        {
            credit = Difficulty;
            if(assignmentsClosed)
                return false;
            if(job is ValueTuple<object, uint> blob)
                blobDifficulties.TryGetValue(blob, out issued);
            else
                issuedDifficulties.TryGetValue(job, out issued);
            if(shareReceipt.Value is { } receipt && (!ReferenceEquals(receipt.Clock, assignmentTimeProvider) ||
                receipt.Timestamp > assignmentTimeProvider.GetTimestamp()))
                return false;
            // Compatibility for direct job validators/custom callers without issuance
            // metadata: current target only. PreviousDifficulty/UTC never authorize work.
            if(issued != null && !ReferenceEquals(issued.Session, assignmentSession))
                return false;
            issued ??= assignment ?? new IssuedDifficulty(Difficulty, assignmentTimeProvider, assignmentSession, difficultyGrace);
            var accepted = issued.TryValidate(proofDifficulty, blockCandidate, out credit, shareReceipt.Value);
            // Retired work may still satisfy the currently issued target. This never
            // grants old easier credit after expiry, or revives another session.
            accepted = accepted || assignment != null && assignment.TryValidate(proofDifficulty, blockCandidate, out credit);
            if(accepted && blockCandidate)
                credit = CandidateShareCredit.Cap(credit, CandidateShareCredit.Limit(job));
            return accepted;
        }
    }

    internal ShareReceipt CaptureShareReceipt() => new(assignmentTimeProvider, assignmentTimeProvider.GetTimestamp());

    internal IDisposable UseShareReceipt(ShareReceipt receipt)
    {
        var previous = shareReceipt.Value;
        shareReceipt.Value = receipt;
        return System.Reactive.Disposables.Disposable.Create(() => shareReceipt.Value = previous);
    }

    internal void CloseDifficultyAssignments()
    {
        lock(assignmentSync)
        {
            assignmentsClosed = true;
            IsAuthorized = false;
            pendingDifficulty = null;
            jobSnapshots = new();
            blobDifficulties.Clear();
        }
    }

    internal double ValidateShareDifficulty(double proofDifficulty, bool blockCandidate, object job)
    {
        if(!TryValidateShareDifficulty(proofDifficulty, blockCandidate, job, out var credit))
            throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty or expired assignment ({proofDifficulty})");
        return credit;
    }

}
