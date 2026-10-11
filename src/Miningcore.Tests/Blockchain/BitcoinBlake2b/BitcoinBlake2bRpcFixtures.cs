using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Newtonsoft.Json;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

// Source-contract fixtures only. Production does not consume expected hash
// work; these types prove its units differ from assigned-share/accounting units.
public class BitcoinBlake2bMiningInfo : MiningInfo
{
    [JsonProperty("difficulty_blake2b")]
    public Blake2bHashWork Blake2bExpectedHashWork { get; set; }
    public BitcoinBlake2bMiningInfoNext Next { get; set; }
}

public class BitcoinBlake2bMiningInfoNext
{
    public uint Height { get; set; }
    public string Bits { get; set; }
    public string Target { get; set; }
    public double? Difficulty { get; set; }
    [JsonProperty("difficulty_blake2b")]
    public Blake2bHashWork Blake2bExpectedHashWork { get; set; }
}

public class BitcoinBlake2bRpcBlock : Block
{
    [JsonProperty("difficulty_blake2b")]
    public Blake2bHashWork Blake2bExpectedHashWork { get; set; }
}

public class BitcoinBlake2bBlockchainInfo : BlockchainInfo
{
    [JsonProperty("difficulty_blake2b")]
    public Blake2bHashWork Blake2bExpectedHashWork { get; set; }
}
