namespace Miningcore.Blockchain.Bitcoin.DaemonResponses;

public class MiningInfo
{
    public int Blocks { get; set; }
    public int CurrentBlockSize { get; set; }
    public int CurrentBlockWeight { get; set; }
    public double Difficulty { get; set; }
    [Newtonsoft.Json.JsonProperty("difficulty_blake2b")]
    public Blake2bHashWork Blake2bExpectedHashWork { get; set; }
    public MiningInfoNext Next { get; set; }
    public double NetworkHashps { get; set; }
    public string Chain { get; set; }
}

public class MiningInfoNext
{
    public uint Height { get; set; }
    public string Bits { get; set; }
    public string Target { get; set; }
    public double? Difficulty { get; set; }
    [Newtonsoft.Json.JsonProperty("difficulty_blake2b")]
    public Blake2bHashWork Blake2bExpectedHashWork { get; set; }
}
