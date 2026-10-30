using System.Security.Cryptography;
using NBitcoin.Crypto;
using NBitcoin.Secp256k1;

namespace Miningcore.Blockchain.Warthog;

/// <summary>Derives the Warthog address locally so wallet secrets never reach a daemon.</summary>
public static class WarthogWalletAddress
{
    public static ECPrivKey ValidatePrivateKey(string privateKey, string poolAddress)
    {
        ECPrivKey key;
        byte[] keyBytes = null;

        try
        {
            if(privateKey?.Length != 64)
                throw new FormatException();

            keyBytes = Convert.FromHexString(privateKey);
            key = Context.Instance.CreateECPrivKey(keyBytes);
        }
        catch(Exception)
        {
            // Never retain a parsing or secp256k1 exception as an inner exception:
            // its message may contain the supplied key.
            throw new ArgumentException("Configured Warthog wallet private key is invalid");
        }
        finally
        {
            if(keyBytes != null)
                CryptographicOperations.ZeroMemory(keyBytes);
        }

        string derivedAddress;
        try
        {
            derivedAddress = DeriveAddress(key);
        }
        catch(Exception)
        {
            key.Dispose();
            throw new ArgumentException("Configured Warthog wallet private key is invalid");
        }

        if(!string.Equals(poolAddress, poolAddress?.ToLowerInvariant(), StringComparison.Ordinal))
        {
            key.Dispose();
            throw new ArgumentException("Configured Warthog pool address must be lowercase hex");
        }

        if(!string.Equals(derivedAddress, poolAddress, StringComparison.Ordinal))
        {
            key.Dispose();
            throw new ArgumentException("Configured Warthog wallet private key does not match the pool address");
        }

        return key;
    }

    public static string DeriveAddress(ECPrivKey key)
    {
        // Warthog: RIPEMD160(SHA256(compressed secp256k1 public key)), followed
        // by the first four bytes of SHA256(raw address).
        var rawAddress = Hashes.Hash160(key.CreatePubKey().ToBytes()).ToBytes();
        var checksum = System.Security.Cryptography.SHA256.HashData(rawAddress);
        var address = new byte[24];
        rawAddress.CopyTo(address, 0);
        checksum.AsSpan(0, 4).CopyTo(address.AsSpan(20));
        return Convert.ToHexStringLower(address);
    }
}
