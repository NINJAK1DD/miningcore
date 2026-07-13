using Miningcore.Blockchain;

namespace Miningcore.Mining;

/// <summary>
/// Persists financially significant block candidates before the submitting worker is acknowledged.
/// </summary>
public interface IBlockCandidateRecorder
{
    Task PersistBlockCandidateAsync(Share share);
}
