using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public class BitcoinBlake2bHeaderTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    public void CachedHasher_RequiresUnmaskedFixedTimeProfileZero(byte flags, bool keyed)
    {
        var key = new byte[16];
        if(keyed) key[0] = 1;
        var fields = new BitcoinBlake2bHeader.ConsensusFields(0xa0000000,
            new byte[32], new byte[32], 1700000000, 0x207fffff, 1, flags, 0, key, 21, new byte[32]);
        byte[] Cached() => BitcoinBlake2bHeader.ComputeUnmaskedProfile0Hash(fields,
            BitcoinBlake2bHeader.HeaderCommitment(fields), BitcoinBlake2bHeader.HiddenPreviousBlockHash(fields.PreviousBlockHash),
            new byte[8], new byte[8], new byte[16]);
        if(flags == 0 && !keyed)
            Assert.Equal(BitcoinBlake2bHeader.ComputeHash(fields, new byte[8], new byte[8], new byte[16]), Cached());
        else Assert.Throws<InvalidOperationException>(Cached);
    }

    [Theory]
    [InlineData(-1, 1, 1, "hash")]
    [InlineData(0, 0, 1, "assignedTarget")]
    [InlineData(0, 1, 0, "networkTarget")]
    public void ProofBounds_NameTheInvalidArgument(int hash, int assigned, int network, string parameter) =>
        Assert.Equal(parameter, Assert.Throws<ArgumentOutOfRangeException>(() =>
            BitcoinBlake2bHeader.ClassifyProof(hash, assigned, network)).ParamName);

    // Immutable vectors copied from Bitcoin Knots
    // v29.4.1.knots20260508 (8c85b1585dac23f964e2dd32045624de7f02aa58),
    // src/test/data/block_header_v2.json. These exercise every ASIC profile,
    // time/nonce ordering, header serialization and the final XOR transform.
    public static IEnumerable<object[]> OfficialHeaderVectors()
    {
        yield return Vector(0, 28, 0,
            "00000000000000000000000000000000", 3, 840000,
            195948557, 287454020, 2309737967, 600,
            "00112233445566778899aabbccddeeff",
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            "000000a01f1e1d1c1b1a191817161514131211100f0e0d0c0b0a0908070605040302010000112233445566778899aabbccddeeff00102030405060708090a0b0c0d0e0f0a8913577ffff001d0df0ad0b44332211efcdab89ffeeddccbbaa998877665544332211005802000003001c000000000000000000000000000000000040d10c008967452301efcdab8967452301efcdab8967452301efcdab8967452301efcdab",
            "4b495dcf05d70a49785b799b22284fbcd9dd1209237c53c87e4674b15587d704");

        yield return Vector(1, 29, 0,
            "0123456789abcdef0123456789abcdef", 1, 840001,
            195948557, 287454020, 2309737967, 600,
            "00112233445566778899aabbccddeeff",
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            "000000a01f1e1d1c1b1a191817161514131211100f0e0d0c0b0a0908070605040302010000112233445566778899aabbccddeeff00102030405060708090a0b0c0d0e0f0a8913577ffff001d0df0ad0b44332211efcdab89ffeeddccbbaa998877665544332211005802000001001d00efcdab8967452301efcdab896745230141d10c008967452301efcdab8967452301efcdab8967452301efcdab8967452301efcdab",
            "44b383821dea9af8d7d81ba7741c34ac8c07ab81ab081d8b6bf0575a787a1eef");

        yield return Vector(2, 30, 7,
            "fedcba9876543210fedcba9876543210", 3, 840000,
            195948557, 2864434397, 2309737967, 600,
            "00112233445566778899aabbccddeeff",
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            "000000a01f1e1d1c1b1a191817161514131211100f0e0d0c0b0a0908070605040302010000112233445566778899aabbccddeeff00102030405060708090a0b0c0d0e0f0a8913577ffff001d0df0ad0bddccbbaaefcdab89ffeeddccbbaa998877665544332211005802000003001e071032547698badcfe1032547698badcfe40d10c008967452301efcdab8967452301efcdab8967452301efcdab8967452301efcdab",
            "06fddae4eaca10b85c87a3c7ed71717fd83998a32fe13f4780722b1f5d882e76");

        yield return Vector(3, 31, 8,
            "fedcba9876543210fedcba9876543210", 3, 840000,
            195948557, 287454020, 16909060, 600,
            "ffffffffffffffff0000000000000000",
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            "000000a01f1e1d1c1b1a191817161514131211100f0e0d0c0b0a0908070605040302010000112233445566778899aabbccddeeff00102030405060708090a0b0c0d0e0f0a8913577ffff001d0df0ad0b44332211040302010000000000000000ffffffffffffffff5802000003001f081032547698badcfe1032547698badcfe40d10c008967452301efcdab8967452301efcdab8967452301efcdab8967452301efcdab",
            "e6304527536f619d3ad71b1c21a22fdef9068498acc561b4100b034373a87058");

        // The selector-255 vector proves the mask-clear boundary without
        // reading beyond the 32-byte mask.
        yield return Vector(0, 24, 255,
            "11111111111111112222222222222222", 3, 840000,
            uint.MaxValue, 287454020, 2309737967, 1432778632,
            "00112233445566778899aabbccddeeff",
            new string('0', 64),
            "000000a01f1e1d1c1b1a191817161514131211100f0e0d0c0b0a0908070605040302010000112233445566778899aabbccddeeff00102030405060708090a0b0c0d0e0f000943577ffff001dffffffff44332211efcdab89ffeeddccbbaa9988776655443322110088776655030018ff2222222222222222111111111111111140d10c000000000000000000000000000000000000000000000000000000000000000000",
            "c31b24420d67f86e524f980a24a18e88f36c821046d5288251b5d88998c69f86");
    }

    [Theory]
    [MemberData(nameof(OfficialHeaderVectors))]
    public void HeaderV2_MatchesStableKnotsVectors(HeaderVector vector)
    {
        var fields = Fields(vector);
        var nonce = UInt32Pair(vector.Nonce, vector.Nonce2);
        var minerTime = UInt32Pair(vector.TimeOffset, vector.Nonce3);
        var headerExtraNonce = ReverseHex(vector.ExtraNonce);

        var serialized = BitcoinBlake2bHeader.Serialize(fields, nonce,
            minerTime, headerExtraNonce);
        var hash = BitcoinBlake2bHeader.ComputeHash(fields, nonce, minerTime,
            headerExtraNonce);

        Assert.Equal(BitcoinBlake2bHeader.HeaderSize, serialized.Length);
        Assert.Equal(vector.Serialized, Convert.ToHexString(serialized)
            .ToLowerInvariant());
        Assert.Equal(vector.Hash, Convert.ToHexString(hash).ToLowerInvariant());
        Assert.Equal(vector.Profile, fields.Flags & 3);
    }

    [Theory]
    [InlineData(1d, "1d00ffff")]
    [InlineData(2d, "1c7fff80")]
    [InlineData(65536d, "1b00ffff")]
    public void ShareTarget_RoundTripsTheExactWireTarget(double difficulty,
        string expectedCompact)
    {
        var target = BitcoinBlake2bHeader.TargetForDifficulty(difficulty);
        var compact = BitcoinBlake2bHeader.EncodeCompactTarget(target);

        Assert.Equal(expectedCompact, compact.ToString("x8"));
        Assert.Equal(target, BitcoinBlake2bHeader.DecodeCompactTarget(compact));
        Assert.True(target <= BitcoinConstants.Diff1 / new BigInteger(difficulty));
    }

    [Fact]
    public void ParserAndConsensusBounds_FailClosed()
    {
        Assert.Throws<InvalidDataException>(() =>
            BitcoinBlake2bHeader.ParseExactHex("00", 8, "nonce"));
        Assert.Throws<InvalidDataException>(() =>
            BitcoinBlake2bHeader.ParseExactHex(new string('g', 16), 8,
                "nonce"));
        Assert.Throws<InvalidDataException>(() =>
            BitcoinBlake2bHeader.DecodeCompactTarget(0));
        Assert.Throws<InvalidDataException>(() =>
            BitcoinBlake2bHeader.DecodeCompactTarget(0x1d80ffff));

        var valid = Fields((HeaderVector) OfficialHeaderVectors().First()[0]);
        Assert.Throws<InvalidDataException>(() =>
            BitcoinBlake2bHeader.HeaderCommitment(valid with { Flags = 0x40 }));
        Assert.Throws<InvalidDataException>(() =>
            BitcoinBlake2bHeader.HeaderCommitment(valid with
            {
                TransactionCount = 0,
            }));
    }

    [Theory]
    [InlineData(99, 100, 50, true, false)]
    [InlineData(100, 100, 50, true, false)]
    [InlineData(101, 100, 50, false, false)]
    [InlineData(49, 100, 50, true, true)]
    [InlineData(50, 100, 50, true, true)]
    [InlineData(51, 100, 50, true, false)]
    [InlineData(100, 50, 100, true, true)]
    public void ProofBoundary_UsesExactInclusiveTargetsAndPreservesHarderCandidates(
        int hash, int shareTarget, int networkTarget, bool accepted, bool candidate)
    {
        Assert.Equal((accepted, candidate), BitcoinBlake2bHeader.ClassifyProof(
            hash, shareTarget, networkTarget));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.Epsilon)]
    [InlineData(double.MaxValue)]
    public void ImpossibleShareTargets_AreRejected(double difficulty) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BitcoinBlake2bHeader.TargetForDifficulty(difficulty));

    [Theory]
    [InlineData(0x1a00ffffU, 22, 0x1c3fffc0U)]
    [InlineData(0x1d00ffffU, 22, 0x1d00ffffU)]
    public void ActivationShift_UsesSaturatingIntegerArithmetic(uint before, byte shift, uint expected) =>
        Assert.Equal(expected, BitcoinBlake2bHeader.ActivationTarget(before, shift, 0x1d00ffffU));

    private static object[] Vector(int profile, byte flags, byte clearBits,
        string xorKey, ushort transactionCount, uint height, uint nonce,
        uint nonce2, uint nonce3, uint timeOffset, string extraNonce,
        string mmRhs, string serialized, string hash) =>
        new object[]
        {
            new HeaderVector(profile, flags, clearBits, xorKey,
                transactionCount, height, nonce, nonce2, nonce3, timeOffset,
                extraNonce, mmRhs, serialized, hash),
        };

    private static BitcoinBlake2bHeader.ConsensusFields Fields(
        HeaderVector vector) => new(
        536870912,
        Convert.FromHexString(
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"),
        Convert.FromHexString(
            "f0e0d0c0b0a090807060504030201000ffeeddccbbaa99887766554433221100"),
        (vector.Flags & BitcoinBlake2bHeader.UseTimeOffsetFlag) != 0
            ? unchecked(2000000000U - vector.TimeOffset)
            : 2000000000U,
        486604799,
        vector.TransactionCount,
        vector.Flags,
        vector.ClearBits,
        ReverseHex(vector.XorKey),
        vector.Height,
        ReverseHex(vector.MergeMiningRightHandSide));

    private static byte[] UInt32Pair(uint first, uint second)
    {
        var result = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(result, first);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), second);
        return result;
    }

    private static byte[] ReverseHex(string value) =>
        Convert.FromHexString(value).Reverse().ToArray();

    public sealed record HeaderVector(int Profile, byte Flags,
        byte ClearBits, string XorKey, ushort TransactionCount, uint Height,
        uint Nonce, uint Nonce2, uint Nonce3, uint TimeOffset,
        string ExtraNonce, string MergeMiningRightHandSide,
        string Serialized, string Hash);
}
