using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Miningcore.Blockchain.Conceal;
using Miningcore.Blockchain.Cryptonote;
using Miningcore.Blockchain.Zano;
using Xunit;

namespace Miningcore.Tests.Mining;

public class PoolFamilyAssignmentTargetTests
{
    // Independent fixed vectors for the existing protocol's 1/255 quantization:
    // big-endian uint256 quotient, or its first four bytes reversed for CryptoNote.
    // 1 has a signed 33-byte representation, 55 fills 32 bytes, 1000 needs padding.
    // Sub-unit values are rejected with the assignment, never saturated only on wire.
    public static IEnumerable<object[]> Targets
    {
        get
        {
            var vectors = new[]
            {
                (1d, "ffffffff", new string('F', 64)),
                (55d, "4a90a704", "04A7904A7904A7904A7904A7904A7904A7904A7904A7904A7904A7904A7903FC"),
                (1000d, "37894100", "004189374BC6A7EF9DB22D0E5604189374BC6A7EF9DB22D0E5604189374BC685"),
                (CryptonoteDifficulty.ShortTargetMaximum, "65000000", "000000650000001B5454545BB9742EEBA4A0F1971A79DAF8B2D5ADB2BAEE8590"),
            };
            foreach(var type in new[] { typeof(ConcealJob), typeof(CryptonoteJob), typeof(ZanoJob) })
            foreach(var (difficulty, shortTarget, fullTarget) in vectors)
                yield return new object[] { type, difficulty, type == typeof(ZanoJob) ? 32 : 4,
                    type == typeof(ZanoJob) ? "0x" + fullTarget.ToLowerInvariant() : shortTarget };
            yield return new object[] { typeof(ZanoJob), CryptonoteDifficulty.FullTargetMaximum, 32,
                "0x00000000000001fe00000000001fe00000000001fe00000000001fe000000000" };
        }
    }

    public static IEnumerable<object[]> InvalidDifficulties
    {
        get
        {
            foreach(var type in new[] { typeof(ConcealJob), typeof(CryptonoteJob), typeof(ZanoJob) })
            foreach(var difficulty in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
                0.5d, 0.9d, 0.1d, 1d / 255d, 1d / 256d, double.Epsilon,
                Math.BitIncrement(CryptonoteDifficulty.FullTargetMaximum), Math.BitIncrement(9223372036854775808d / 255d), 1e20d, double.MaxValue })
                yield return new object[] { type, difficulty, type == typeof(ZanoJob) ? 32 : 4 };
            foreach(var type in new[] { typeof(ConcealJob), typeof(CryptonoteJob) })
            foreach(var difficulty in new[] { Math.BitIncrement(CryptonoteDifficulty.ShortTargetMaximum),
                1e8d, 1e9d, 2.2e9d, 3e9d, Math.BitDecrement(4294967296d),
                4294967296d, Math.BitIncrement(4294967296d), 5e9d, CryptonoteDifficulty.FullTargetMaximum })
                yield return new object[] { type, difficulty, 4 };
        }
    }

    [Theory]
    [MemberData(nameof(InvalidDifficulties))]
    public void TargetEncoding_RejectsUnrepresentableDifficulty(Type type, double difficulty, int size)
    {
        var job = RuntimeHelpers.GetUninitializedObject(type);
        var encode = type.GetMethod("EncodeTarget", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(encode);
        var error = Assert.Throws<TargetInvocationException>(() => encode.Invoke(job, new object[] { difficulty, size }));
        var invalid = Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
        Assert.Equal("difficulty", invalid.ParamName);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void TargetEncoding_PreservesFullWidthSignedPrefixAndZeroPadding(Type type, double difficulty, int size, string expected)
    {
        // EncodeTarget is pure managed arithmetic; no blob/native constructor is
        // needed. The concrete contention tests separately exercise real jobs.
        var job = RuntimeHelpers.GetUninitializedObject(type);
        var encode = type.GetMethod("EncodeTarget", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(encode);
        Assert.Equal(expected, (string) encode.Invoke(job, new object[] { difficulty, size }));
    }

    [Theory]
    [InlineData(typeof(ConcealJob))]
    [InlineData(typeof(CryptonoteJob))]
    public void ShortBoundary_IsConsumableByXmrigWithoutAZeroDivisor(Type type)
    {
        var job = RuntimeHelpers.GetUninitializedObject(type);
        var encode = type.GetMethod("EncodeTarget", BindingFlags.Instance | BindingFlags.NonPublic);
        var target = (string) encode.Invoke(job, new object[] { CryptonoteDifficulty.ShortTargetMaximum, 4 });
        var raw = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString(target));
        Assert.Equal(101u, raw);
        // XMRig Job.cpp's four-byte parser, verified against upstream source.
        var minerTarget = ulong.MaxValue / (uint.MaxValue / (ulong) raw);
        Assert.True(minerTarget > 0);
    }

    [Theory]
    [InlineData(typeof(ConcealJob))]
    [InlineData(typeof(CryptonoteJob))]
    public void ShortTarget_IntegerMinerWorkStaysWithinOnePercentOfNominalCredit(Type type)
    {
        var job = RuntimeHelpers.GetUninitializedObject(type);
        var encode = type.GetMethod("EncodeTarget", BindingFlags.Instance | BindingFlags.NonPublic);
        void Check(double difficulty)
        {
            var target = (string) encode.Invoke(job, new object[] { difficulty, 4 });
            var raw = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString(target));
            Assert.True(raw >= 101, $"Short target lost precision: {difficulty:R}, {raw}");
            // Reproduce both integer divisions, not the approximate 2^32 / t.
            var minerTarget = ulong.MaxValue / (uint.MaxValue / (ulong) raw);
            var impliedDifficulty = (double) ulong.MaxValue / minerTarget;
            Assert.True(impliedDifficulty / difficulty < 1.01,
                $"Work/credit exceeded the budget: {difficulty:R}, {raw}, {impliedDifficulty:R}");
        }
        foreach(var difficulty in new[] { 1d, 55d, 1000d, 1e5d, 1e6d, 3e7d, CryptonoteDifficulty.ShortTargetMaximum })
            Check(difficulty);
        // Check both sides of every coarse bin where truncation is material.
        // Above 10,000 target units the theoretical error is below 0.01%.
        for(uint raw = 101; raw <= 10_000; raw++)
        {
            var transition = 4294967296d / (raw + 1d);
            Check(Math.BitDecrement(transition));
            Check(transition);
            Check(Math.BitIncrement(transition));
        }
    }
}
