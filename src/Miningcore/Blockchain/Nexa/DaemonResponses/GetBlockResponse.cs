using Newtonsoft.Json;

namespace Miningcore.Blockchain.Nexa.DaemonResponses;

public class Block
{
    [JsonProperty("hash")]
    public string Hash { get; set; }

    public uint Height { get; set; }
    public int Confirmations { get; set; }

    [JsonProperty("txid")]
    public string[] Transactions { get; set; }
}
