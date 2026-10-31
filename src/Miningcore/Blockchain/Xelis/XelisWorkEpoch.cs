namespace Miningcore.Blockchain.Xelis;

// Shared by every job and worker snapshot issued during a compatible chain era.
// Retired epochs never reopen, including after a supported daemon recovers.
internal sealed class XelisWorkEpoch
{
    private int retired;
    internal bool IsRetired => Volatile.Read(ref retired) != 0;
    internal bool Retire() => Interlocked.Exchange(ref retired, 1) == 0;
    internal void EnsureActive()
    {
        if(IsRetired) throw new XelisWorkUnavailableException();
    }
}
