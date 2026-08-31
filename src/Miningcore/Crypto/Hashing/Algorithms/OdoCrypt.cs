using System.IO;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Native;
using Miningcore.Stratum;

namespace Miningcore.Crypto.Hashing.Algorithms;

/// <summary>
/// DigiByte Odocrypt proof-of-work. Odocrypt changes its permutation on a
/// consensus-defined, network-specific wall-clock schedule.
/// </summary>
[Identifier("odocrypt")]
public unsafe class OdoCrypt : IHashAlgorithm
{
    internal static uint DeriveKey(uint nTime, uint shapeChangeInterval)
    {
        if(shapeChangeInterval == 0)
            throw new ArgumentOutOfRangeException(nameof(shapeChangeInterval));

        return nTime - nTime % shapeChangeInterval;
    }

    internal static void ValidateJobContract(BlockTemplate blockTemplate,
        BitcoinTemplate.BitcoinNetworkParams network)
    {
        ArgumentNullException.ThrowIfNull(blockTemplate);
        ArgumentNullException.ThrowIfNull(network);

        var activationHeight = network.OdoCryptActivationHeight;

        if(!activationHeight.HasValue || activationHeight.Value == 0)
        {
            throw new InvalidDataException(
                "Odocrypt requires a nonzero network activation height");
        }

        if(blockTemplate.Height < activationHeight.Value)
        {
            throw new InvalidDataException(
                $"Odocrypt is not active at height {blockTemplate.Height}; " +
                $"activation is {activationHeight.Value}");
        }

        var interval = network.OdoCryptShapeChangeInterval;

        if(!interval.HasValue || interval.Value == 0)
        {
            throw new InvalidDataException(
                "Odocrypt requires a nonzero network shape-change interval");
        }

        if(!blockTemplate.OdoKey.HasValue)
        {
            throw new InvalidDataException(
                "Odocrypt requires the daemon's 'odokey' getblocktemplate field. " +
                "Use a compatible DigiByte Core daemon and request templates " +
                "with the Odocrypt algorithm parameter (algo=odo)");
        }

        var templateKey = DeriveKey(blockTemplate.CurTime, interval.Value);

        if(blockTemplate.OdoKey.Value != templateKey)
        {
            throw new InvalidDataException(
                $"Odocrypt daemon key {blockTemplate.OdoKey.Value} does not " +
                $"match the key {templateKey} derived from template time " +
                $"{blockTemplate.CurTime}");
        }
    }

    public void Digest(ReadOnlySpan<byte> data, Span<byte> result,
        params object[] extra)
    {
        Contract.Requires<ArgumentException>(data.Length == 80,
            $"{nameof(data)} must be exactly 80 bytes");
        Contract.Requires<ArgumentException>(result.Length >= 32,
            $"{nameof(result)} must be at least 32 bytes");
        Contract.Requires<ArgumentException>(extra.Length >= 4,
            $"{nameof(extra)} must contain the block time and network parameters");
        Contract.Requires<ArgumentException>(extra[0] is ulong,
            $"{nameof(extra)} block time must be an unsigned 64-bit value");
        Contract.Requires<ArgumentException>(
            extra[3] is BitcoinTemplate.BitcoinNetworkParams,
            $"{nameof(extra)} must contain Bitcoin network parameters");

        var nTime64 = (ulong) extra[0];

        if(nTime64 > uint.MaxValue)
        {
            throw new StratumException(StratumError.Other,
                "Odocrypt share time exceeds the 32-bit header field");
        }

        var network = (BitcoinTemplate.BitcoinNetworkParams) extra[3];
        var interval = network.OdoCryptShapeChangeInterval;

        if(!interval.HasValue || interval.Value == 0)
        {
            throw new StratumException(StratumError.Other,
                "Odocrypt job has no valid shape-change interval");
        }

        var key = DeriveKey((uint) nTime64, interval.Value);

        fixed(byte* input = data)
        fixed(byte* output = result)
        {
            if(OdoCryptNative.Hash((IntPtr) input, (IntPtr) output,
                   (uint) data.Length, key) != 1)
            {
                throw new StratumException(StratumError.Other,
                    "Native Odocrypt share hashing failed");
            }
        }
    }
}
