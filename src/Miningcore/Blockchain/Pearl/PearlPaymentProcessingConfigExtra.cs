namespace Miningcore.Blockchain.Pearl;

public sealed class PearlPaymentProcessingConfigExtra
{
    public string WalletPassword { get; set; }
    public long? FeeRateGrainsPerKb { get; set; } = 10000;
    public int? MinimumConfirmations { get; set; } = 101;
}
