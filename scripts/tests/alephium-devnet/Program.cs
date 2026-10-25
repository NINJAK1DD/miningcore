using System.Buffers.Binary;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Miningcore.Blockchain.Alephium;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Time;
using Microsoft.IO;
using NBitcoin;
using NLog;

// Reproduce the two historical algorithms independently of the production
// verifier. The intermediate state is intentionally stricter than pre-PR dev.
static CandidateComparison Compare(BigInteger proof, BigInteger target)
{
    var roundedProof = proof.IsZero ? BigInteger.Zero : new Target(proof).ToBigInteger();
    var roundedTarget = new Target(target).ToBigInteger();
    return new(roundedProof <= roundedTarget, proof <= roundedTarget, proof <= target,
        proof > roundedTarget, roundedProof.ToString("x"));
}

static CandidateComparison CheckFalsePositiveBoundary(BigInteger target)
{
    var above = Compare(target + 1, target);
    if(!above.LegacyDevCandidate || above.ExactCandidate)
        throw new Exception("Expected target+1 to expose the legacy false positive");
    foreach(var proof in new[] { target - 1, target })
    {
        var valid = Compare(proof, target);
        if(!valid.LegacyDevCandidate || !valid.ExactCandidate)
            throw new Exception("Valid boundary proof must remain a candidate");
    }
    return above;
}

if(args.Length == 2 && args[0] == "--verify-receipt")
{
    using var receipt = JsonDocument.Parse(File.ReadAllText(args[1]));
    var root = receipt.RootElement;
    var templateJson = root.GetProperty("template");
    var nonceBytes = Convert.FromHexString(root.GetProperty("nonce").GetString());
    if(nonceBytes.Length != 24) throw new Exception("Receipt nonce must have 24 bytes");
    var header = Convert.FromHexString(templateJson.GetProperty("HeaderBlob").GetString());
    var inputBytes = nonceBytes.Concat(header).ToArray();
    var replayHasher = new Blake3IHash();
    var intermediateHash = new byte[32]; var replayHash = new byte[32];
    replayHasher.Digest(inputBytes, intermediateHash); replayHasher.Digest(intermediateHash, replayHash);
    if(Convert.ToHexString(replayHash).ToLowerInvariant() != root.GetProperty("proofHash").GetString())
        throw new Exception("Receipt proof hash does not match native BLAKE3 replay");
    var (from, to) = AlephiumUtils.BlockChainIndex(replayHash);
    if(from != templateJson.GetProperty("FromGroup").GetInt32() || to != templateJson.GetProperty("ToGroup").GetInt32())
        throw new Exception("Receipt chain index mismatch");
    var target = new BigInteger(Convert.FromHexString(templateJson.GetProperty("TargetBlob").GetString()), true, true);
    var expectedTarget = BigInteger.Parse(root.GetProperty("exactTarget").GetString(), System.Globalization.NumberStyles.AllowHexSpecifier);
    var recordedRoundedTarget = BigInteger.Parse(root.GetProperty("oldRoundedTarget").GetString(), System.Globalization.NumberStyles.AllowHexSpecifier);
    if(expectedTarget != target || recordedRoundedTarget != new Target(target).ToBigInteger())
        throw new Exception("Receipt target integers disagree with its template");
    var expectedCredit = (double) new Miningcore.Util.BigRational(AlephiumConstants.Diff1Target, target);
    if(root.GetProperty("candidateCeiling").GetDouble() != expectedCredit || root.GetProperty("candidateCredit").GetDouble() != expectedCredit)
        throw new Exception("Receipt credit is not capped at its exact target threshold");
    var comparison = Compare(new BigInteger(replayHash, true, true), target);
    if(!comparison.LegacyDevCandidate || comparison.IntermediateExactHashRoundedTargetCandidate || !comparison.ExactCandidate)
        throw new Exception("Receipt must distinguish dev, intermediate and exact classification");
    if(JsonSerializer.Serialize(comparison) != JsonSerializer.Serialize(root.GetProperty("classification")))
        throw new Exception("Recorded comparison differs from recomputed classification");
    var boundary = CheckFalsePositiveBoundary(target);
    if(JsonSerializer.Serialize(boundary) != JsonSerializer.Serialize(root.GetProperty("targetPlusOneClassification")))
        throw new Exception("Recorded target+1 classification mismatch");
    var provenance = root.GetProperty("build");
    if(!Regex.IsMatch(provenance.GetProperty("sourceCommit").GetString(), "^[0-9a-f]{40}$") ||
       !Regex.IsMatch(provenance.GetProperty("assemblySha256").GetString(), "^[0-9a-f]{64}$"))
        throw new Exception("Missing or malformed Miningcore build provenance");
    Console.WriteLine("Native receipt replay, all three classifications, target+1 false positive and build provenance verified");
    return;
}

if(args.Length != 2) throw new ArgumentException("Expected private devnet host and evidence output path");
var assembly = typeof(AlephiumJob).Assembly;
var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
var sourceCommit = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(x => x.Key == "MiningcoreSourceCommit")?.Value;
sourceCommit ??= Regex.Match(informationalVersion ?? "", "[0-9a-f]{40}$").Value;
if(!Regex.IsMatch(sourceCommit, "^[0-9a-f]{40}$"))
    throw new Exception("Miningcore assembly must embed its source commit; rebuild with -p:MiningcoreSourceCommit=<commit>");
var build = new { sourceCommit, informationalVersion,
    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant() };
var host = args[0];
using var http = new HttpClient { BaseAddress = new Uri($"http://{host}:22973"), Timeout = TimeSpan.FromSeconds(10) };
var parameters = JsonDocument.Parse(await http.GetStringAsync("/infos/chain-params"));
if(parameters.RootElement.GetProperty("networkId").GetInt32() != 4) throw new Exception("Devnet network-id 4 required");
var version = await http.GetStringAsync("/infos/version");
if(JsonDocument.Parse(version).RootElement.GetProperty("version").GetString() != "v4.7.3") throw new Exception("Expected pinned Alephium v4.7.3");
Console.WriteLine("Node: " + version);
using var client = new TcpClient();
await client.ConnectAsync(host, 20973);
client.ReceiveTimeout = 30000;
client.SendTimeout = 30000;
using var stream = client.GetStream();
byte[] Packet()
{
    var size = new byte[4]; stream.ReadExactly(size);
    var length = BinaryPrimitives.ReadInt32BigEndian(size);
    if(length < 2 || length > 1_000_000) throw new Exception("Unexpected mining packet length");
    var bytes = new byte[length]; stream.ReadExactly(bytes); return bytes;
}
var message = Packet();
if(message[0] != 1 || message[1] != 0) throw new Exception("Expected version-1 jobs packet");
var pos = 2;
int UInt() { var n = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(pos)); pos += 4; return n; }
byte[] Blob() { var size = UInt(); var bytes = message.AsSpan(pos,size).ToArray(); pos += size; return bytes; }
var count = UInt();
AlephiumBlockTemplate template = null;
BigInteger exactTarget = 0, roundedTarget = 0;
for(var index=0; index<count; index++)
{
    var from = UInt(); var to = UInt(); var header = Blob(); var txs = Blob(); var target = Blob(); var height = UInt();
    var integer = new BigInteger(target, true, true);
    var compact = new Target(integer).ToBigInteger();
    if(template == null && integer > compact)
    {
        exactTarget = integer; roundedTarget = compact;
        template = new AlephiumBlockTemplate { JobId="live-devnet", FromGroup=from,ToGroup=to,ChainIndex=from*4+to,
            Height=(ulong)height,HeaderBlob=Convert.ToHexString(header),TxsBlob=Convert.ToHexString(txs),TargetBlob=Convert.ToHexString(target) };
    }
}
if(template == null) throw new Exception("No template with an unsigned-mantissa rounding window");
var source = new AlephiumJob(); source.Init(template);
var context = new AlephiumWorkerContext(); context.Init(source.Difficulty / AlephiumConstants.Pow2xDiff1TargetNumZero * 200, null, new StandardClock());
var connection = new StratumConnection(new NullLogger(LogManager.LogFactory),new RecyclableMemoryStreamManager(),new StandardClock(),"live-devnet",true);
connection.SetContext(context);
// Deliberately exercise the existing internal issuance path without widening
// production InternalsVisibleTo access for an optional smoke executable. Keep
// this signature lookup in sync with AlephiumJob.ForWorker when it changes.
var forWorker = typeof(AlephiumJob).GetMethod("ForWorker", BindingFlags.Instance | BindingFlags.NonPublic,
    null, new[] { typeof(WorkerContextBase), typeof(Func<string>) }, null)
    ?? throw new MissingMethodException("AlephiumJob.ForWorker signature changed; update the devnet harness");
source = (AlephiumJob) forWorker.Invoke(source, new object[] { context, null });
var hasher = new Blake3IHash();
var input = new byte[24 + template.HeaderBlob.Length/2]; Convert.FromHexString(template.HeaderBlob).CopyTo(input,24);
var first = new byte[32]; var hash = new byte[32];
var deadline = DateTime.UtcNow.AddMinutes(2);
ulong attempts = 0;
for(; attempts < 30_000_000; attempts++)
{
    if((attempts & 0xffff) == 0 && DateTime.UtcNow > deadline) throw new TimeoutException("Nonce search timeout");
    BinaryPrimitives.WriteUInt64BigEndian(input.AsSpan(16,8), attempts);
    hasher.Digest(input,first); hasher.Digest(first,hash);
    var proof = new BigInteger(hash,true,true);
    if(proof <= roundedTarget || proof > exactTarget) continue;
    var (from,to) = AlephiumUtils.BlockChainIndex(hash);
    if(from != template.FromGroup || to != template.ToGroup) continue;
    break;
}
if(attempts == 30_000_000) throw new Exception("Valid proof in rounding window not found");
var proofInteger = new BigInteger(hash, true, true);
var classification = Compare(proofInteger, exactTarget);
var targetPlusOneClassification = CheckFalsePositiveBoundary(exactTarget);
if(!classification.LegacyDevCandidate || classification.IntermediateExactHashRoundedTargetCandidate || !classification.ExactCandidate)
    throw new Exception("Expected valid proof accepted by legacy dev and final code, missed only by the intermediate state");
var nonce = Convert.ToHexString(input.AsSpan(0,24));
var share = source.ProcessShare(connection,nonce);
if(!share.IsBlockCandidate || share.BlockHash != Convert.ToHexString(hash).ToLowerInvariant()) throw new Exception("Production verifier did not accept the valid rounding-window proof");
var ceiling = source.Difficulty / AlephiumConstants.Pow2xDiff1TargetNumZero;
if(share.Difficulty != ceiling) throw new Exception("Production candidate credit was not capped");
stream.Write(source.SerializeCoinbase(nonce,1));
bool accepted = false;
var submitDeadline = DateTime.UtcNow.AddSeconds(30);
while(true)
{
    if(DateTime.UtcNow > submitDeadline) throw new TimeoutException("Submission acknowledgement timeout");
    var response = Packet();
    if(response[0] != 1) throw new Exception("Unexpected response version");
    if(response[1] == 0) continue;
    if(response[1] != 1 || response.Length != 43) throw new Exception("Unexpected submission response: " + Convert.ToHexString(response));
    if(!response.AsSpan(10,32).SequenceEqual(hash)) throw new Exception("Submission hash mismatch");
    accepted = response[^1] == 1;
    if(!accepted) throw new Exception("Daemon rejected valid rounding-window block");
    break;
}
var blockJson = await http.GetStringAsync("/blockflow/blocks/" + share.BlockHash);
using var block = JsonDocument.Parse(blockJson);
if(block.RootElement.GetProperty("hash").GetString() != share.BlockHash || block.RootElement.GetProperty("height").GetInt64() != (long)template.Height) throw new Exception("Accepted block not persisted");
var evidence = new { nodeVersion="v4.7.3", imageDigest="sha256:c8bc624a166f7328c244f975d9fe5581609fe5a66f09533ff7115b39ffa80351",networkId=4,
    template, nonce, attempts, exactTarget=exactTarget.ToString("x"),oldRoundedTarget=roundedTarget.ToString("x"),proofHash=share.BlockHash,
    build, classification, targetPlusOneClassification, candidateCredit=share.Difficulty, candidateCeiling=ceiling, accepted, persistedBlock=block.RootElement.Clone() };
var json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented=true });
File.WriteAllText(args[1],json); Console.WriteLine(json);

internal sealed record CandidateComparison(bool LegacyDevCandidate,
    bool IntermediateExactHashRoundedTargetCandidate, bool ExactCandidate,
    bool ProofAboveRoundedTarget, string RoundedProof);
