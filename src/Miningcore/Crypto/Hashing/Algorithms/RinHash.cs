using System;
using System.Security.Cryptography;
using System.Text;
using Blake3;
using Isopoh.Cryptography.Argon2;
using Miningcore.Contracts;
using Org.BouncyCastle.Crypto.Digests;

namespace Miningcore.Crypto.Hashing.Algorithms;

/// <summary>
/// RinHash: BLAKE3 -> Argon2d -> SHA3-256
/// </summary>
[Identifier("rinhash")]
public unsafe class RinHash : IHashAlgorithm
{
    private static readonly byte[] Salt;

    static RinHash()
    {
        Salt = Encoding.UTF8.GetBytes("RinCoinSalt");
    }

    public unsafe void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(result.Length >= 32);

        // 1. BLAKE3
        var hash = Blake3.Hasher.Hash(data);
        var blake3 = hash.AsSpanUnsafe().ToArray();

        // 2. Argon2d
        var config = new Argon2Config
        {
            Type = Argon2Type.DataDependentAddressing,
            Version = Argon2Version.Nineteen,
            TimeCost = 2,
            MemoryCost = 64, // 64 MB
            Lanes = 1,
            Threads = 1,
            Password = blake3,
            Salt = Salt,
            HashLength = 32
        };

        var argon2 = new Argon2(config);
        var arresult = argon2.Hash();

        // 3. SHA3-256
        HashSha3(arresult.Buffer, result, SHA3_256.IsSupported);
    }

    internal static void HashSha3(ReadOnlySpan<byte> data, Span<byte> result, bool platformSha3Supported)
    {
        if(platformSha3Supported)
        {
            SHA3_256.HashData(data).CopyTo(result);
            return;
        }

        var input = data.ToArray();
        var hash = new byte[32];
        var digest = new Sha3Digest(256);

        digest.BlockUpdate(input, 0, input.Length);
        digest.DoFinal(hash, 0);
        hash.CopyTo(result);
    }
}
