using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using NBitcoin;
using Newtonsoft.Json;
using Transaction = NBitcoin.Transaction;

namespace Miningcore.Blockchain.Pearl;

internal sealed class PearlBlockTemplate : BlockTemplate
{
    [JsonProperty("requiredcertversion")]
    public uint RequiredCertificateVersion { get; set; }

    [JsonProperty("vsizelimit")]
    public long VirtualSizeLimit { get; set; }
}

/// <summary>Immutable, worker-specific coinbase, header and transaction snapshot.</summary>
internal sealed class PearlBlockJob
{
    private readonly byte[] header;
    private readonly byte[][] transactions;
    internal ReadOnlyMemory<byte> Header => header;
    internal uint Height { get; }
    internal string PreviousBlockHash { get; }
    internal long RewardGrains { get; }
    internal string CoinbaseId { get; }

    internal PearlBlockJob(PearlBlockTemplate template, string rewardAddress, PearlNetwork network,
        ReadOnlySpan<byte> workerJobNonce)
    {
        ArgumentNullException.ThrowIfNull(template);
        PearlNetworkRules.RequireCurrentMining(network, template.Height, template.RequiredCertificateVersion);
        if(template.RequiredCertificateVersion != 3)
            throw new InvalidDataException("Only salted integer V3 mining is supported");
        if(template.Height == 0 || template.CoinbaseValue <= 0 ||
            template.VirtualSizeLimit is <= 0 or > 4_000_000)
            throw new InvalidDataException("Invalid Pearl template limits");
        if(workerJobNonce.Length != 24)
            throw new ArgumentException("Worker job nonce must contain 24 bytes", nameof(workerJobNonce));
        if(template.Transactions == null || template.Transactions.Length > 100000)
            throw new InvalidDataException("Invalid Pearl transaction list");
        var target = PearlConsensus.DecodeTarget(uint.Parse(template.Bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        if(template.Target?.Length != 64 || new BigInteger(Convert.FromHexString(template.Target),
               isUnsigned: true, isBigEndian: true) != target)
            throw new InvalidDataException("Template target disagrees with bits");

        if(template.PreviousBlockhash?.Length != 64 || template.CoinbaseAux?.Flags?.Length > 64)
            throw new InvalidDataException("Invalid template hash or coinbase flags");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockTransactions = new List<Transaction>();
        long serializedBytes = 0;
        foreach(var tx in template.Transactions)
        {
            if(string.IsNullOrEmpty(tx.Data) || tx.Data.Length % 2 != 0 ||
                tx.Data.Length > 8_000_000 || tx.TxId?.Length != 64)
                throw new InvalidDataException("Invalid template transaction");
            serializedBytes += tx.Data.Length / 2;
            if(serializedBytes > 4_000_000)
                throw new InvalidDataException("Template transaction bytes exceed limit");
            var parsed = Transaction.Load(Convert.FromHexString(tx.Data), Network.Main);
            if(!string.Equals(parsed.GetHash().ToString(), tx.TxId, StringComparison.OrdinalIgnoreCase) ||
                !Convert.ToHexString(parsed.ToBytes()).Equals(tx.Data, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Template transaction identity or encoding mismatch");
            if(!identities.Add(parsed.GetHash().ToString()))
                throw new InvalidDataException("Duplicate template transaction");
            blockTransactions.Add(parsed);
        }

        var witnessLeaves = new List<byte[]> {new byte[32]};
        witnessLeaves.AddRange(blockTransactions.Select(x => x.GetWitHash().ToBytes()));
        var witnessPreimage = new byte[64];
        MerkleRoot(witnessLeaves).CopyTo(witnessPreimage, 0);
        var commitment = DoubleSha256(witnessPreimage);
        if(template.DefaultWitnessCommitment?.Length != 64 ||
            !Convert.FromHexString(template.DefaultWitnessCommitment).AsSpan().SequenceEqual(commitment))
            throw new InvalidDataException("Pearl witness commitment mismatch");

        var coinbase = Transaction.Create(Network.Main);
        coinbase.Version = 2;
        var flags = template.CoinbaseAux?.Flags is {Length: > 0} encodedFlags
            ? Convert.FromHexString(encodedFlags) : Array.Empty<byte>();
        if(flags.Length > 32)
            throw new InvalidDataException("Coinbase flags exceed supported size");
        var script = new Script(Op.GetPushOp((long) template.Height),
            Op.GetPushOp(workerJobNonce.ToArray()), Op.GetPushOp(flags));
        if(script.Length is < 2 or > 100)
            throw new InvalidDataException("Invalid coinbase script length");
        coinbase.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, uint.MaxValue), script)
        {
            WitScript = new WitScript(new[] {new byte[32]})
        });
        coinbase.Outputs.Add(new TxOut(Money.Satoshis(template.CoinbaseValue),
            PearlAddress.RewardScript(rewardAddress, PearlNetworkRules.AddressPrefix(network))));
        coinbase.Outputs.Add(new TxOut(Money.Zero,
            new Script(Convert.FromHexString("6a24aa21a9ed").Concat(commitment).ToArray())));
        blockTransactions.Insert(0, coinbase);
        transactions = blockTransactions.Select(x => x.ToBytes()).ToArray();
        var weight = blockTransactions.Sum(x => (long) x.GetSerializedSize(TransactionOptions.None) * 3 + x.GetSerializedSize());
        // Reserve worst-case certificate/header framing conservatively.
        if(weight + (65000 + PearlConsensus.HeaderSize + 9) * 4 > template.VirtualSizeLimit * 4)
            throw new InvalidDataException("Pearl candidate exceeds the supported block weight");
        var merkle = MerkleRoot(blockTransactions.Select(x => x.GetHash().ToBytes()).ToList());
        var previous = Convert.FromHexString(template.PreviousBlockhash);
        Array.Reverse(previous);
        header = PearlConsensus.SerializeIncompleteHeader(template.Version, previous, merkle,
            template.CurTime, uint.Parse(template.Bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        Height = template.Height;
        RewardGrains = template.CoinbaseValue;
        PreviousBlockHash = template.PreviousBlockhash;
        CoinbaseId = coinbase.GetHash().ToString();
    }

    internal byte[] BuildBlock(PearlProofVerifier.Result result)
    {
        if(!result.IsBlockCandidate || result.Certificate.Length == 0 ||
            !result.Header.AsSpan().SequenceEqual(header))
            throw new InvalidDataException("A verified network-target certificate is required");
        var certified = PearlConsensus.SerializeCertifiedHeader(header, 3, result.PublicData, result.Certificate);
        using var stream = new MemoryStream();
        stream.Write(certified);
        var bitcoinStream = new BitcoinStream(stream, true);
        var count = (ulong) transactions.Length;
        bitcoinStream.ReadWriteAsVarInt(ref count);
        foreach(var tx in transactions)
            stream.Write(tx);
        return stream.ToArray();
    }

    private static byte[] MerkleRoot(List<byte[]> hashes)
    {
        while(hashes.Count > 1)
        {
            var next = new List<byte[]>((hashes.Count + 1) / 2);
            for(var i = 0; i < hashes.Count; i += 2)
            {
                var pair = new byte[64];
                hashes[i].CopyTo(pair, 0);
                hashes[Math.Min(i + 1, hashes.Count - 1)].CopyTo(pair, 32);
                next.Add(DoubleSha256(pair));
            }
            hashes = next;
        }
        return hashes[0];
    }

    private static byte[] DoubleSha256(ReadOnlySpan<byte> value) => SHA256.HashData(SHA256.HashData(value));
}
