namespace Miningcore.Blockchain.BitcoinBlake2b;

internal static class BitcoinBlake2bReconciliationPolicy
{
    internal const ulong RecoveryDepth = 2UL * 25920;
    internal const ulong HorizonWarningDepth = RecoveryDepth / 20;
}
