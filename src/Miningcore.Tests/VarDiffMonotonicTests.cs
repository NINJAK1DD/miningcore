using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Tests.Util;
using Miningcore.VarDiff;
using Xunit;

namespace Miningcore.Tests;

public class VarDiffMonotonicTests
{
    // Discover every concrete family context, including merged mining. A newly
    // added family must inherit the same initialization and timing contract.
    public static IEnumerable<object[]> Families => typeof(WorkerContextBase).Assembly.GetTypes()
        .Where(x => typeof(WorkerContextBase).IsAssignableFrom(x) && !x.IsAbstract)
        .SelectMany(x => new[] { new object[] { x.FullName, false }, new object[] { x.FullName, true } });

    private static (WorkerContextBase Worker, VarDiffConfig Options, MockMasterClock Wall, ManualTimeProvider Time)
        Fixture(string family = null)
    {
        var worker = family == null ? new WorkerContextBase() :
            (WorkerContextBase) Activator.CreateInstance(typeof(WorkerContextBase).Assembly.GetType(family));
        var options = new VarDiffConfig { MinDiff = 1, TargetTime = 10, RetargetTime = 5, VariancePercent = 1 };
        var wall = new MockMasterClock { CurrentTime = DateTime.UnixEpoch.AddDays(20000) };
        var time = new ManualTimeProvider();
        worker.Init(10, options, wall, time);
        return (worker, options, wall, time);
    }

    private static double? Update(WorkerContextBase worker, VarDiffConfig options, MockMasterClock wall, bool idle) =>
        idle ? VarDiffManager.IdleUpdate(worker, options, wall) : VarDiffManager.Update(worker, options, wall);

    [Fact]
    public void ProductionInitialization_OwnsASystemCounterIndependentOfUtcCreation()
    {
        var (_, options, wall, _) = Fixture();
        var worker = new WorkerContextBase();
        var before = TimeProvider.System.GetTimestamp();
        worker.Init(10, options, wall);
        var after = TimeProvider.System.GetTimestamp();
        Assert.Same(TimeProvider.System, worker.VarDiff.TimeProvider);
        Assert.InRange(worker.VarDiff.CreatedTimestamp, before, after);
        Assert.Equal(worker.VarDiff.CreatedTimestamp, worker.VarDiff.LastRetargetTimestamp);
        Assert.Null(worker.VarDiff.LastShareTimestamp);
        Assert.Equal(wall.Now, worker.Created);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void AllFamilies_WallCorrectionsCannotChangeAnIdenticalMonotonicSequence(string family, bool idle)
    {
        var reference = Fixture(family);
        var corrected = Fixture(family);
        Assert.Null(VarDiffManager.Update(reference.Worker, reference.Options, reference.Wall));
        Assert.Null(VarDiffManager.Update(corrected.Worker, corrected.Options, corrected.Wall));
        var changes = new[] { TimeSpan.FromDays(1000), TimeSpan.FromDays(-2000), TimeSpan.FromHours(1),
            TimeSpan.FromHours(-1), TimeSpan.FromSeconds(123) };
        var intervals = new[] { 2, 3, 20, 1, 5 };
        for(var i = 0; i < intervals.Length; i++)
        {
            var interval = TimeSpan.FromSeconds(intervals[i]);
            reference.Time.AdvanceMonotonic(interval);
            corrected.Time.AdvanceMonotonic(interval);
            corrected.Wall.CurrentTime += changes[i];
            var expected = Update(reference.Worker, reference.Options, reference.Wall, idle);
            var actual = Update(corrected.Worker, corrected.Options, corrected.Wall, idle);
            Assert.Equal(expected, actual);
            if(actual.HasValue)
            {
                Assert.Equal(corrected.Wall.Now, corrected.Worker.VarDiff.LastUpdate);
                reference.Worker.SetDifficulty(expected.Value);
                corrected.Worker.SetDifficulty(actual.Value);
            }
            Assert.Equal(reference.Worker.VarDiff.LastShareTimestamp, corrected.Worker.VarDiff.LastShareTimestamp);
            Assert.Equal(reference.Worker.VarDiff.LastRetargetTimestamp, corrected.Worker.VarDiff.LastRetargetTimestamp);
            Assert.Equal(reference.Worker.VarDiff.TimeBuffer?.ToArray(), corrected.Worker.VarDiff.TimeBuffer?.ToArray());
            Assert.Equal(reference.Worker.Difficulty, corrected.Worker.Difficulty);
        }
        Assert.NotNull(corrected.Worker.VarDiff.LastUpdate); // The sequence actually retargeted.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WallClockAlone_CannotExpireCooldownOrCreateAnIdleInterval(bool idle)
    {
        var (worker, options, wall, time) = Fixture();
        Assert.Null(VarDiffManager.Update(worker, options, wall));
        var assignment = worker.VarDiff.LastUpdate = wall.Now;
        foreach(var jump in new[] { 1000000, -2000000, 1000000 })
        {
            wall.CurrentTime = wall.Now.AddSeconds(jump);
            Assert.Null(Update(worker, options, wall, idle));
            Assert.Equal(0, worker.VarDiff.LastShareTimestamp);
            Assert.Equal(0, worker.VarDiff.LastRetargetTimestamp);
            Assert.Equal(assignment, worker.VarDiff.LastUpdate);
            Assert.Equal(10, worker.Difficulty);
        }
    }

    [Fact]
    public void FirstShare_InitializesAtTheCounterSampleEvenAfterLongWallAndMonotonicIdle()
    {
        var (worker, options, wall, time) = Fixture();
        time.AdvanceMonotonic(TimeSpan.FromDays(1));
        wall.CurrentTime = wall.Now.AddYears(-1);
        Assert.Null(VarDiffManager.Update(worker, options, wall));
        Assert.Equal(time.GetTimestamp(), worker.VarDiff.LastShareTimestamp);
        Assert.Equal(time.GetTimestamp(), worker.VarDiff.LastRetargetTimestamp);
        Assert.Null(worker.VarDiff.TimeBuffer);
        Assert.Null(worker.VarDiff.LastUpdate);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(5));
        Assert.Equal(20, VarDiffManager.Update(worker, options, wall));
    }

    [Fact]
    public void IdleBeforeFirstShare_UsesContextCreationCounterAndStrictRetargetCooldown()
    {
        var (worker, options, wall, time) = Fixture();
        options.RetargetTime = 20;
        wall.CurrentTime = wall.Now.AddYears(1);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(19));
        Assert.Null(VarDiffManager.IdleUpdate(worker, options, wall));
        Assert.Null(worker.VarDiff.LastShareTimestamp);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        Assert.Equal(5, VarDiffManager.IdleUpdate(worker, options, wall));
        Assert.Equal(time.GetTimestamp(), worker.VarDiff.LastShareTimestamp);
        Assert.Equal(wall.Now, worker.VarDiff.LastUpdate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retarget_ResetStartsANewCooldownAndClearsTheMeasurementBuffer(bool idle)
    {
        var (worker, options, wall, time) = Fixture();
        Assert.Null(VarDiffManager.Update(worker, options, wall));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(5));
        Assert.Equal(20, Update(worker, options, wall, idle));
        worker.SetDifficulty(20);
        var timestamp = worker.VarDiff.LastRetargetTimestamp;
        var assignment = worker.VarDiff.LastUpdate;
        Assert.True(worker.VarDiff.TimeBuffer == null || worker.VarDiff.TimeBuffer.Size == 0);
        // Idle intervals are also subject to cooldown, even if the scheduler's
        // one-second inactivity margin would otherwise allow a sweep early.
        time.AdvanceMonotonic(TimeSpan.FromSeconds(4));
        wall.CurrentTime = wall.Now.AddDays(100);
        Assert.Null(Update(worker, options, wall, idle));
        Assert.Equal(timestamp, worker.VarDiff.LastRetargetTimestamp);
        Assert.Equal(assignment, worker.VarDiff.LastUpdate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementContext_UsesItsOwnCounterDomainAndDisabledReinitializationClearsIt(bool idle)
    {
        var (worker, options, wall, oldTime) = Fixture();
        Assert.Null(VarDiffManager.Update(worker, options, wall));
        oldTime.AdvanceMonotonic(TimeSpan.FromDays(30));
        var old = worker.VarDiff;
        var replacementTime = new ManualTimeProvider();
        worker.VarDiff = new VarDiffContext(replacementTime) { Config = options };
        wall.CurrentTime = wall.Now.AddYears(-1);
        Assert.Null(Update(worker, options, wall, idle));
        Assert.Null(old.LastUpdate);
        Assert.Equal(0, old.LastShareTimestamp);
        replacementTime.AdvanceMonotonic(TimeSpan.FromSeconds(5));
        Assert.Equal(20, Update(worker, options, wall, idle));
        worker.Init(10, null, wall);
        Assert.Null(worker.VarDiff);
        Assert.Null(Update(worker, options, wall, idle));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanceledCalculation_DoesNotTouchTimingOrAssignment(bool idle)
    {
        var (worker, options, wall, time) = Fixture();
        time.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => idle ?
            VarDiffManager.IdleUpdate(worker, options, wall, ct: cancel.Token) :
            VarDiffManager.Update(worker, options, wall, ct: cancel.Token));
        Assert.Null(worker.VarDiff.LastShareTimestamp);
        Assert.Equal(0, worker.VarDiff.LastRetargetTimestamp);
        Assert.Null(worker.VarDiff.LastUpdate);
        Assert.Null(worker.VarDiff.TimeBuffer);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BlockedShareCalculation_RechecksContextAndCancellationAfterAcquiringTheStateLock(
        bool canceled, bool disabled)
    {
        var (worker, options, wall, time) = Fixture();
        var old = worker.VarDiff;
        using var cancel = new CancellationTokenSource();
        var completed = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new Thread(() =>
        {
            try
            {
                completed.TrySetResult(VarDiffManager.Update(worker, options, wall, ct: cancel.Token));
            }
            catch(Exception ex) { completed.TrySetException(ex); }
        }) { IsBackground = true };
        lock(old)
        {
            producer.Start();
            // This dedicated thread can only block on Monitor.Enter(old).
            // Wait for actual contention rather than relying on a scheduler delay.
            Assert.True(SpinWait.SpinUntil(() =>
                (producer.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
            // Idle producers never wait behind a share producer.
            var idle = Task.Run(() => VarDiffManager.IdleUpdate(worker, options, wall));
            Assert.True(idle.Wait(TimeSpan.FromSeconds(10)));
            Assert.Null(idle.Result);
            if(canceled)
                cancel.Cancel();
            else
                worker.VarDiff = disabled ? null : new VarDiffContext(new ManualTimeProvider()) { Config = options };
        }
        if(canceled)
            await Assert.ThrowsAsync<OperationCanceledException>(() => completed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        else
            Assert.Null(await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(producer.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(old.LastShareTimestamp);
        Assert.Null(old.LastUpdate);
    }

    private sealed class CheckingTimeProvider : TimeProvider
    {
        public Action OnSample { get; set; }
        public long Timestamp { get; set; }
        public override long TimestampFrequency => 1000; // Exercise provider-specific units.
        public override long GetTimestamp()
        {
            OnSample?.Invoke();
            return Timestamp;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CounterSamples_AreReadUnderTheSharedStateLockAndUseProviderFrequency(bool idle)
    {
        var (worker, options, wall, _) = Fixture();
        var time = new CheckingTimeProvider();
        worker.VarDiff = new VarDiffContext(time) { Config = options, LastShareTimestamp = 0 };
        time.OnSample = () => Assert.True(Monitor.IsEntered(worker.VarDiff));
        time.Timestamp = 5000;
        Assert.Equal(20, Update(worker, options, wall, idle));
        Assert.Equal(5000, worker.VarDiff.LastRetargetTimestamp);
    }
}
