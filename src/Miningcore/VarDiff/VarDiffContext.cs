using CircularBuffer;
using Miningcore.Configuration;

namespace Miningcore.VarDiff;

public class VarDiffContext
{
    public VarDiffContext(TimeProvider timeProvider = null)
    {
        TimeProvider = timeProvider ?? System.TimeProvider.System;
        CreatedTimestamp = TimeProvider.GetTimestamp();
        LastRetargetTimestamp = CreatedTimestamp;
    }

    // All timestamps belong to this immutable provider's counter domain.
    public TimeProvider TimeProvider { get; }
    public long CreatedTimestamp { get; }
    public long? LastShareTimestamp { get; internal set; }
    public long LastRetargetTimestamp { get; internal set; }
    // Measured elapsed seconds, never absolute wall-clock timestamps.
    public CircularBuffer<double> TimeBuffer { get; set; }
    // UTC assignment metadata retained for previous-difficulty share validation.
    public DateTime? LastUpdate { get; set; }
    public VarDiffConfig Config { get; set; }
}
