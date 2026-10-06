using CircularBuffer;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Time;

namespace Miningcore.VarDiff;

public static class VarDiffManager
{
    private const int BufferSize = 10;  // Last 10 shares should be enough
    private const double SafetyMargin = 1;    // ensure we don't miss a cycle due a sub-second fraction delta;
    private const double ZeroWindowEstimate = 0.001; // Conservative policy for unresolved zero intervals.

    public static double? Update(WorkerContextBase context, VarDiffConfig options, IMasterClock clock,
        double protocolMaximum = double.MaxValue, CancellationToken ct = default)
    {
        var ctx = context.VarDiff;
        if(ctx == null || options == null)
            return null;

        try
        {
            Monitor.Enter(ctx);
            // Sample time under the same lock as LastShareTimestamp. Otherwise an idle
            // update can advance LastShareTimestamp past a regular update's captured time.
            ct.ThrowIfCancellationRequested();
            if(!ReferenceEquals(ctx, context.VarDiff))
                return null;
            var ts = ctx.TimeProvider.GetTimestamp();
            var difficulty = context.Difficulty;

            if(ctx.LastShareTimestamp.HasValue)
            {
                if(RebaseInvalidTiming(ctx, ts, ctx.LastShareTimestamp.Value))
                    return null;
                var minDiff = options.MinDiff;
                var maxDiff = Math.Min(options.MaxDiff ?? protocolMaximum, protocolMaximum);
                var timeDelta = ElapsedSeconds(ctx, ctx.LastShareTimestamp.Value, ts);

                // make sure buffer exists as this point
                ctx.TimeBuffer ??= new CircularBuffer<double>(BufferSize);

                // Always calculate the time until now even there is no share submitted.
                var timeTotal = ctx.TimeBuffer.Sum() + timeDelta;
                var sampleCount = ctx.TimeBuffer.Size + 1;
                var avg = timeTotal / sampleCount;

                // Once there is a share submitted, store the time into the buffer and update the last time.
                ctx.TimeBuffer.PushBack(timeDelta);
                ctx.LastShareTimestamp = ts;

                // Check if we need to change the difficulty
                var variance = options.TargetTime * (options.VariancePercent / 100.0);
                var tMin = options.TargetTime - variance;
                var tMax = options.TargetTime + variance;

                if(ElapsedSeconds(ctx, ctx.LastRetargetTimestamp, ts) < options.RetargetTime || avg >= tMin && avg <= tMax)
                    return null;

                // Possible New Diff
                if(TryCalculateDifficulty(difficulty, options.TargetTime, avg, sampleCount, maxDiff, out var newDiff) &&
                   TryApplyNewDiff(ref newDiff, difficulty, minDiff, maxDiff, ts, ctx, options, clock))
                    return newDiff;
            }

            else
            {
                // init
                ctx.LastRetargetTimestamp = ts;
                ctx.LastShareTimestamp = ts;
            }
        }

        finally
        {
            Monitor.Exit(ctx);
        }

        return null;
    }

    public static double? IdleUpdate(WorkerContextBase context, VarDiffConfig options, IMasterClock clock,
        double protocolMaximum = double.MaxValue, CancellationToken ct = default)
    {
        var ctx = context.VarDiff;
        if(ctx == null || options == null)
            return null;

        // abort if a regular update is just happening
        if(!Monitor.TryEnter(ctx))
            return null;

        try
        {
            ct.ThrowIfCancellationRequested();
            if(!ReferenceEquals(ctx, context.VarDiff))
                return null;
            var ts = ctx.TimeProvider.GetTimestamp();
            var difficulty = context.Difficulty;
            var previousTs = ctx.LastShareTimestamp ?? ctx.CreatedTimestamp;
            if(RebaseInvalidTiming(ctx, ts, previousTs))
                return null;
            var timeDelta = ElapsedSeconds(ctx, previousTs, ts);

            timeDelta += SafetyMargin;

            // Check inactivity as well as the actual retarget cooldown. The
            // scheduler margin must never shorten the assignment cooldown.
            if(timeDelta < options.RetargetTime ||
               ElapsedSeconds(ctx, ctx.LastRetargetTimestamp, ts) < options.RetargetTime)
                return null;

            var minDiff = options.MinDiff;
            var maxDiff = Math.Min(options.MaxDiff ?? protocolMaximum, protocolMaximum);

            // Always calculate the time until now even there is no share submitted.
            var timeTotal = (ctx.TimeBuffer?.Sum() ?? 0) + (timeDelta - SafetyMargin);
            var sampleCount = (ctx.TimeBuffer?.Size ?? 0) + 1;
            var avg = timeTotal / sampleCount;

            // Possible New Diff
            if(TryCalculateDifficulty(difficulty, options.TargetTime, avg, sampleCount, maxDiff, out var newDiff) &&
               TryApplyNewDiff(ref newDiff, difficulty, minDiff, maxDiff, ts, ctx, options, clock))
            {
                // A no-op sweep is not a share. Preserve the next real share's
                // elapsed interval unless a new assignment starts a fresh window.
                ctx.LastShareTimestamp = ts;
                return newDiff;
            }
        }

        finally
        {
            Monitor.Exit(ctx);
        }

        return null;
    }

    private static double ElapsedSeconds(VarDiffContext ctx, long start, long end) =>
        ctx.TimeProvider.GetElapsedTime(start, end).TotalSeconds;

    private static bool RebaseInvalidTiming(VarDiffContext ctx, long ts, long previousTs)
    {
        if(ts >= previousTs && ts >= ctx.LastRetargetTimestamp &&
           ctx.TimeBuffer?.Any(x => !double.IsFinite(x) || x < 0) != true)
            return false;

        // A broken provider or poisoned history is not evidence of a faster miner.
        // Start a fresh measurement window; retain LastUpdate because no actual
        // assignment changed (other families use it for previous-difficulty work).
        ctx.LastShareTimestamp = ts;
        ctx.LastRetargetTimestamp = ts;
        ctx.TimeBuffer = null;
        return true;
    }

    private static bool TryCalculateDifficulty(double difficulty, double targetTime, double average, int sampleCount,
        double maximum, out double result)
    {
        result = 0;
        if(!double.IsFinite(difficulty) || difficulty <= 0 ||
           !double.IsFinite(targetTime) || targetTime <= 0 ||
           !double.IsFinite(average) || average < 0 ||
           !double.IsFinite(maximum) || maximum <= 0)
            return false;

        // Keep the conservative one-millisecond policy floor for unresolved zeros.
        // Scale the estimate to the available intervals (at most ten), so a
        // sparse window cannot claim the same rate as a full zero window.
        if(average == 0)
        {
            // A full buffer plus the current interval gives eleven samples.
            // Cap at ten to retain the conservative 0.0001-second full-window
            // estimate instead of increasing the retarget another ten percent.
            var zeroWindowAverage = ZeroWindowEstimate / Math.Min(sampleCount, BufferSize);
            // Coarse zero samples cannot justify a downward adjustment when
            // the configured target interval is already at/below this estimate.
            if(targetTime <= zeroWindowAverage)
            {
                result = Math.Min(difficulty, maximum);
                return true;
            }
            average = zeroWindowAverage;
        }

        var product = difficulty * targetTime;
        var candidate = product / average;
        if(!double.IsFinite(candidate) || product == 0 || double.IsSubnormal(product))
        {
            // Preserve ordinary arithmetic, but avoid intermediate overflow or
            // underflow when the final proportional result is representable.
            var d = Math.ILogB(difficulty);
            var t = Math.ILogB(targetTime);
            var a = Math.ILogB(average);
            candidate = Math.ScaleB(Math.ScaleB(difficulty, -d) * Math.ScaleB(targetTime, -t) /
                Math.ScaleB(average, -a), d + t - a);
        }
        result = Math.Min(candidate, maximum);
        return double.IsFinite(result);
    }

    /// <summary>
    /// Assumes to be called with lock held
    /// </summary>
    private static bool TryApplyNewDiff(ref double newDiff, double oldDiff, double minDiff, double maxDiff, long ts,
        VarDiffContext ctx, VarDiffConfig options, IMasterClock clock)
    {
        // Max delta
        if(options.MaxDelta is > 0)
        {
            var delta = Math.Abs(newDiff - oldDiff);

            if(delta > options.MaxDelta)
            {
                if(newDiff > oldDiff)
                    newDiff = oldDiff + options.MaxDelta.Value;
                else if(newDiff < oldDiff)
                    newDiff = oldDiff - options.MaxDelta.Value;
            }
        }

        // Clamp to valid range
        if(newDiff < minDiff)
            newDiff = minDiff;
        if(newDiff > maxDiff)
            newDiff = maxDiff;

        // RTC if the Diff is changed
        if(!(newDiff < oldDiff) && !(newDiff > oldDiff))
            return false;

        ctx.LastRetargetTimestamp = ts;
        ctx.LastUpdate = clock.Now;

        // Due to change of diff, Buffer needs to be cleared
        if(ctx.TimeBuffer != null)
            ctx.TimeBuffer = new CircularBuffer<double>(BufferSize);

        return true;
    }
}
