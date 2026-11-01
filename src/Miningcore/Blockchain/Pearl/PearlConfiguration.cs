using System.IO;
using Miningcore.Configuration;
using Newtonsoft.Json.Linq;

namespace Miningcore.Configuration
{
    public sealed class PearlCoinTemplate : CoinTemplate
    {
        public override string GetAlgorithmName() => "PearlHash";
    }
}

namespace Miningcore.Blockchain.Pearl
{
    internal sealed class PearlPoolConfig
    {
        internal static void AddSchemaRules(JObject document)
        {
            var pool = (JObject) document["definitions"]?["PoolConfig"] ??
                throw new InvalidOperationException("Missing pool schema");
            pool["allOf"] = new JArray(JObject.Parse("""
                {"anyOf":[
                  {"not":{"properties":{"coin":{"enum":["pearl"]}},"required":["coin"]}},
                  {"required":["proofWorker"],"properties":{
                    "network":{"type":"string","enum":["mainnet","regtest"],"default":"mainnet"},
                    "proofWorker":{"type":"string","minLength":1,"description":"Absolute path to the pinned Linux x64 Pearl proof worker."},
                    "templatePollSeconds":{"type":"integer","minimum":1,"maximum":30,"default":5},
                    "jobLifetimeSeconds":{"type":"integer","minimum":30,"maximum":180,"default":120},
                    "maximumJobsPerWorker":{"type":"integer","minimum":2,"maximum":16,"default":8},
                    "paymentProcessing":{"properties":{
                      "minimumPayment":{"type":"number","minimum":0.01},
                      "feeRateGrainsPerKb":{"type":"integer","minimum":1,"maximum":100000000,"default":10000},
                      "minimumConfirmations":{"type":"integer","minimum":101,"maximum":100000,"default":101},
                      "payoutScheme":{"enum":["PPS","SOLO","PPLNS","PROP"]}
                    }}
                  }}
                ]}
                """));
        }
        public string Network { get; set; } = "mainnet";
        public string ProofWorker { get; set; }
        public int TemplatePollSeconds { get; set; } = 5;
        public int JobLifetimeSeconds { get; set; } = 120;
        public int MaximumJobsPerWorker { get; set; } = 8;

        internal PearlNetwork Validate(PoolConfig pool)
        {
            if(pool.PaymentProcessing != null && pool.PaymentProcessing.PayoutScheme is not
                (PayoutScheme.PPS or PayoutScheme.SOLO or PayoutScheme.PPLNS or PayoutScheme.PROP))
                throw new InvalidDataException("Pearl supports only PPS, SOLO, PPLNS and PROP reward schemes");
            if(pool.PaymentProcessing?.PayoutScheme == PayoutScheme.PPLNS &&
               pool.PaymentProcessing.PayoutSchemeConfig is {Type: not JTokenType.Null} schemeConfig)
            {
                if(schemeConfig is not JObject window)
                    throw new InvalidDataException("Pearl PPLNS configuration must be an object");
                // Newtonsoft binds property names case-insensitively. Validate every
                // matching spelling so a later alias cannot bypass the admission guard.
                foreach(var factor in window.Properties().Where(x => string.Equals(x.Name, "factor", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value))
                    if(factor.Type is not (JTokenType.Integer or JTokenType.Float) || factor.Value<decimal>() <= 0)
                        throw new InvalidDataException("Pearl PPLNS factor must be a positive number");
            }
            var network = Network switch
            {
                "mainnet" => PearlNetwork.Mainnet,
                "regtest" => PearlNetwork.Regtest,
                _ => throw new InvalidDataException("Only certified mainnet and proof-enabled regtest Pearl networks are supported")
            };
            if(!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
                System.Runtime.InteropServices.Architecture.X64 || string.IsNullOrEmpty(ProofWorker) ||
                !Path.IsPathFullyQualified(ProofWorker))
                throw new InvalidDataException("Pearl requires the pinned Linux x64 proof worker at an absolute path");
            if(TemplatePollSeconds is < 1 or > 30 || JobLifetimeSeconds is < 30 or > 180 ||
                MaximumJobsPerWorker is < 2 or > 16)
                throw new InvalidDataException("Invalid Pearl job limits");
            if(pool.EnableInternalStratum != true)
                throw new InvalidDataException("Pearl requires local Stratum and durable local accounting");
            foreach(var endpoint in pool.Ports.Values)
            {
                PearlWork.Target(endpoint.Difficulty);
                if(endpoint.DifficultyGraceSeconds is < 0 or > 30)
                    throw new InvalidDataException("Pearl difficulty grace cannot exceed 30 seconds");
                if(endpoint.VarDiff != null)
                {
                    PearlWork.Target(endpoint.VarDiff.MinDiff);
                    if(endpoint.VarDiff.MaxDiff.HasValue) PearlWork.Target(endpoint.VarDiff.MaxDiff.Value);
                }
            }
            PearlAddress.RewardScript(pool.Address, PearlNetworkRules.AddressPrefix(network));
            var recipients = pool.RewardRecipients ?? Array.Empty<RewardRecipient>();
            if(recipients.Any(x => x == null || x.Percentage < 0) || recipients.Sum(x => x.Percentage) >= 100)
                throw new InvalidDataException("Invalid Pearl reward recipients");
            foreach(var recipient in recipients.Where(x => x.Percentage > 0))
                PearlAddress.RewardScript(recipient.Address, PearlNetworkRules.AddressPrefix(network));
            return network;
        }
    }
}
