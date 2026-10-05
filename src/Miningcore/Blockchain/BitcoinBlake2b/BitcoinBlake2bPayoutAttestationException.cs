namespace Miningcore.Blockchain.BitcoinBlake2b;

// Closed source-owned diagnostic vocabulary; daemon payloads never become labels.
internal enum BitcoinBlake2bPayoutAttestationReason
{
    RpcUnavailable = 701,
    Syncing = 702,
    ContractDrift = 703,
    BindingMismatch = 704,
}

internal sealed class BitcoinBlake2bPayoutAttestationException(
    BitcoinBlake2bPayoutAttestationReason reason) : InvalidOperationException(
        $"Bitcoin BLAKE2b payout attestation withheld ({reason}); rewards and balances remain pending")
{
    internal BitcoinBlake2bPayoutAttestationReason Reason { get; } = reason;
}
