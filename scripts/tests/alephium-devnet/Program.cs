using System.Buffers.Binary;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using Miningcore.Blockchain.Alephium;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Time;
using Microsoft.IO;
using NBitcoin;
using NLog;

if(args.Length != 2) throw new ArgumentException("Expected private devnet host and evidence output path");
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
if(template == null) throw new Exception("No template with an unsigned-mantissa recovery window");
var source = new AlephiumJob(); source.Init(template);
var context = new AlephiumWorkerContext(); context.Init(source.Difficulty / AlephiumConstants.Pow2xDiff1TargetNumZero * 200, null, new StandardClock());
var connection = new StratumConnection(new NullLogger(LogManager.LogFactory),new RecyclableMemoryStreamManager(),new StandardClock(),"live-devnet",true);
connection.SetContext(context);
source = (AlephiumJob) typeof(AlephiumJob).GetMethod("ForWorker", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(source, new object[] { context, null });
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
if(attempts == 30_000_000) throw new Exception("Recovery proof not found");
var nonce = Convert.ToHexString(input.AsSpan(0,24));
var share = source.ProcessShare(connection,nonce);
if(!share.IsBlockCandidate || share.BlockHash != Convert.ToHexString(hash).ToLowerInvariant()) throw new Exception("Production verifier did not recover candidate");
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
    if(!accepted) throw new Exception("Daemon rejected recovered block");
    break;
}
var blockJson = await http.GetStringAsync("/blockflow/blocks/" + share.BlockHash);
using var block = JsonDocument.Parse(blockJson);
if(block.RootElement.GetProperty("hash").GetString() != share.BlockHash || block.RootElement.GetProperty("height").GetInt64() != (long)template.Height) throw new Exception("Accepted block not persisted");
var evidence = new { nodeVersion="v4.7.3", imageDigest="sha256:c8bc624a166f7328c244f975d9fe5581609fe5a66f09533ff7115b39ffa80351",networkId=4,
    template, nonce, attempts, exactTarget=exactTarget.ToString("x"),oldRoundedTarget=roundedTarget.ToString("x"),proofHash=share.BlockHash,
    recoveredAboveOldTarget=true, candidateCredit=share.Difficulty, candidateCeiling=ceiling, accepted, persistedBlock=block.RootElement.Clone() };
var json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented=true });
File.WriteAllText(args[1],json); Console.WriteLine(json);
