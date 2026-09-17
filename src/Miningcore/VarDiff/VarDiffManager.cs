using CircularBuffer;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Time;

namespace Miningcore.VarDiff;

public static class VarDiffManager
{
    private const int BufferSize = 10;  // Last 10 shares should be enough
    private const double SafetyMargin = 1;    // ensure we don't miss a cycle due a sub-second fraction delta;

    public static double? Update(WorkerContextBase context, VarDiffConfig options, IMasterClock clock,
        double protocolMaximum = double.MaxValue)
    {
        var ctx = context.VarDiff;
        if(ctx == null)
            return null;

        try
        {
            Monitor.Enter(ctx);
            // Sample time under the same lock as LastTs. Otherwise an idle
            // update can advance LastTs past a regular update's captured time.
            var now = clock.Now;
            var ts = now.ToUnixSeconds();
            var difficulty = context.Difficulty;

            if(ctx.LastTs.HasValue)
            {
                if(RebaseInvalidTiming(ctx, ts, ctx.LastTs.Value))
                    return null;
                var minDiff = options.MinDiff;
                var maxDiff = Math.Min(options.MaxDiff ?? protocolMaximum, protocolMaximum);
                var timeDelta = ts - ctx.LastTs.Value;

                // make sure buffer exists as this point
                ctx.TimeBuffer ??= new CircularBuffer<double>(BufferSize);

                // Always calculate the time until now even there is no share submitted.
                var timeTotal = ctx.TimeBuffer.Sum() + timeDelta;
                var sampleCount = ctx.TimeBuffer.Size + 1;
                var avg = timeTotal / sampleCount;

                // Once there is a share submitted, store the time into the buffer and update the last time.
                ctx.TimeBuffer.PushBack(timeDelta);
                ctx.LastTs = ts;

                // Check if we need to change the difficulty
                var variance = options.TargetTime * (options.VariancePercent / 100.0);
                var tMin = options.TargetTime - variance;
                var tMax = options.TargetTime + variance;

                if(ts - ctx.LastRetarget < options.RetargetTime || avg >= tMin && avg <= tMax)
                    return null;

                // Possible New Diff
                if(TryCalculateDifficulty(difficulty, options.TargetTime, avg, sampleCount, maxDiff, out var newDiff) &&
                   TryApplyNewDiff(ref newDiff, difficulty, minDiff, maxDiff, ts, ctx, options, now))
                    return newDiff;
            }

            else
            {
                // init
                ctx.LastRetarget = ts;
                ctx.LastTs = ts;
            }
        }

        finally
        {
            Monitor.Exit(ctx);
        }

        return null;
    }

    public static double? IdleUpdate(WorkerContextBase context, VarDiffConfig options, IMasterClock clock,
        double protocolMaximum = double.MaxValue)
    {
        var ctx = context.VarDiff;
        if(ctx == null)
            return null;

        // abort if a regular update is just happening
        if(!Monitor.TryEnter(ctx))
            return null;

        try
        {
            var now = clock.Now;
            var ts = now.ToUnixSeconds();
            var difficulty = context.Difficulty;
            var previousTs = ctx.LastTs ?? ctx.Created.ToUnixSeconds();
            if(RebaseInvalidTiming(ctx, ts, previousTs))
                return null;
            var timeDelta = ts - previousTs;

            timeDelta += SafetyMargin;

            // we only get involved if there was never an update or the last update happened longer than retargetTime ago
            if(timeDelta < options.RetargetTime)
                return null;

            var minDiff = options.MinDiff;
            var maxDiff = Math.Min(options.MaxDiff ?? protocolMaximum, protocolMaximum);

            // Always calculate the time until now even there is no share submitted.
            var timeTotal = (ctx.TimeBuffer?.Sum() ?? 0) + (timeDelta - SafetyMargin);
            var sampleCount = (ctx.TimeBuffer?.Size ?? 0) + 1;
            var avg = timeTotal / sampleCount;

            // Possible New Diff
            if(TryCalculateDifficulty(difficulty, options.TargetTime, avg, sampleCount, maxDiff, out var newDiff) &&
               TryApplyNewDiff(ref newDiff, difficulty, minDiff, maxDiff, ts, ctx, options, now))
            {
                // A no-op sweep is not a share. Preserve the next real share's
                // elapsed interval unless a new assignment starts a fresh window.
                ctx.LastTs = ts;
                return newDiff;
            }
        }

        finally
        {
            Monitor.Exit(ctx);
        }

        return null;
    }

    private static bool RebaseInvalidTiming(VarDiffContext ctx, double ts, double previousTs)
    {
        if(double.IsFinite(previousTs) && double.IsFinite(ctx.LastRetarget) &&
           ts >= previousTs && ts >= ctx.LastRetarget &&
           ctx.TimeBuffer?.Any(x => !double.IsFinite(x) || x < 0) != true)
            return false;

        // Backward time or poisoned history is not evidence of a faster miner.
        // Start a fresh measurement window; retain LastUpdate because no actual
        // assignment changed (other families use it for previous-difficulty work).
        ctx.LastTs = ts;
        ctx.LastRetarget = ts;
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

        // A zero window only says samples fit inside the one-second resolution.
        // Scale the estimate to the available intervals (at most ten), so a
        // sparse window cannot claim the same rate as a full zero window.
        if(average == 0)
        {
            // A full buffer plus the current interval gives eleven samples.
            // Cap at ten to retain the conservative 0.1-second full-window
            // estimate instead of increasing the retarget another ten percent.
            var zeroWindowAverage = 1d / Math.Min(sampleCount, BufferSize);
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
    private static bool TryApplyNewDiff(ref double newDiff, double oldDiff, double minDiff, double maxDiff, double ts,
        VarDiffContext ctx, VarDiffConfig options, DateTime now)
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

        ctx.LastRetarget = ts;
        ctx.LastUpdate = now;

        // Due to change of diff, Buffer needs to be cleared
        if(ctx.TimeBuffer != null)
            ctx.TimeBuffer = new CircularBuffer<double>(BufferSize);

        return true;
    }
}
