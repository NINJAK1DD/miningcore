namespace Miningcore.Blockchain.Bitcoin.DaemonResponses;

public class AuxBlockTemplate
{
    public string Hash { get; set; }
    public int ChainId { get; set; }
    public string PreviousBlockhash { get; set; }
    public long CoinbaseValue { get; set; }
    public string Bits { get; set; }
    public uint Height { get; set; }
}
