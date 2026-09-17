using System.Numerics;

namespace Miningcore.Blockchain.BitcoinBlake2b;

// An immutable admission value: callers cannot independently supply the
// credited difficulty and the proof target, or construct an invalid default.
internal sealed class BitcoinBlake2bDifficulty
{
    // Diff1 is exactly representable as a double and produces target 1.
    internal static double Maximum => (double) Bitcoin.BitcoinConstants.Diff1;

    private BitcoinBlake2bDifficulty(double difficulty)
    {
        Target = BitcoinBlake2bHeader.TargetForDifficulty(difficulty);
        Difficulty = difficulty;
        Bits = BitcoinBlake2bHeader.EncodeCompactTarget(Target);
    }

    internal double Difficulty { get; }
    internal BigInteger Target { get; }
    internal uint Bits { get; }

    internal static BitcoinBlake2bDifficulty Create(double difficulty) => new(difficulty);
}
