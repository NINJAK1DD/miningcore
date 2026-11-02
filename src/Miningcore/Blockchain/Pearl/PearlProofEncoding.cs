using System.IO;
using System.IO.Compression;

namespace Miningcore.Blockchain.Pearl;

/// <summary>Strict base64 and bounded gzip/zlib decoding for the named-parameter Pearl dialect.</summary>
internal static class PearlProofEncoding
{
    internal const int MaximumEncodedCharacters = 4 * ((PearlPlainProof.MaximumBytes + 2) / 3);

    internal static byte[] Decode(string encoded)
    {
        if(string.IsNullOrEmpty(encoded) || encoded.Length > MaximumEncodedCharacters ||
            encoded.Length % 4 != 0 || encoded.Any(char.IsWhiteSpace))
            throw new InvalidDataException("Invalid Pearl proof encoding");

        byte[] wire;
        try { wire = Convert.FromBase64String(encoded); }
        catch(FormatException) { throw new InvalidDataException("Invalid Pearl proof encoding"); }
        if(wire.Length > PearlPlainProof.MaximumBytes)
            throw new InvalidDataException("Pearl proof exceeds the decoded limit");

        // Dense dimensions are little-endian integers and can resemble zlib magic.
        // A fully valid raw structure takes precedence over compression heuristics.
        try
        {
            var raw = PearlPlainProof.Inspect(wire);
            if(wire.Length == raw.CanonicalLength) Array.Resize(ref wire, raw.CanonicalLength + 1);
            return wire;
        }
        catch(InvalidDataException) { }

        var gzip = wire.Length >= 2 && wire[0] == 0x1f && wire[1] == 0x8b;
        var zlib = wire.Length >= 2 && (wire[0] & 0x0f) == 8 && wire[0] >> 4 <= 7 &&
            ((wire[0] << 8) + wire[1]) % 31 == 0;
        byte[] result;
        if(gzip || zlib)
        {
            using var input = new MemoryStream(wire, false);
            using Stream compressed = gzip
                ? new GZipStream(input, CompressionMode.Decompress)
                : new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while((count = compressed.Read(buffer, 0,
                Math.Min(buffer.Length, PearlPlainProof.MaximumBytes - (int) output.Length + 1))) != 0)
            {
                if(output.Length + count > PearlPlainProof.MaximumBytes)
                    throw new InvalidDataException("Pearl decompressed proof exceeds the limit");
                output.Write(buffer, 0, count);
            }
            result = output.ToArray();
        }
        else
            result = wire;

        var dimensions = PearlPlainProof.Inspect(result);
        // Canonicalize legacy omitted-None and current None formats before replay hashing.
        if(result.Length == dimensions.CanonicalLength)
            Array.Resize(ref result, dimensions.CanonicalLength + 1);
        return result;
    }
}
