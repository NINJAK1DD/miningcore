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
    // Sub-unit values saturate at uint256 max, including below 1/255 where the
    // old quantized divisor became zero. This changes the wire target only.
    public static IEnumerable<object[]> Targets
    {
        get
        {
            var vectors = new[]
            {
                (0.5d, "ffffffff", new string('F', 64)),
                (0.9d, "ffffffff", new string('F', 64)),
                (0.1d, "ffffffff", new string('F', 64)),
                (1d / 255d, "ffffffff", new string('F', 64)),
                (1d / 256d, "ffffffff", new string('F', 64)),
                (double.Epsilon, "ffffffff", new string('F', 64)),
                (1d, "ffffffff", new string('F', 64)),
                (55d, "4a90a704", "04A7904A7904A7904A7904A7904A7904A7904A7904A7904A7904A7904A7903FC"),
                (1000d, "37894100", "004189374BC6A7EF9DB22D0E5604189374BC6A7EF9DB22D0E5604189374BC685"),
            };
            foreach(var type in new[] { typeof(ConcealJob), typeof(CryptonoteJob), typeof(ZanoJob) })
            foreach(var (difficulty, shortTarget, fullTarget) in vectors)
                yield return new object[] { type, difficulty, type == typeof(ZanoJob) ? 32 : 4,
                    type == typeof(ZanoJob) ? "0x" + fullTarget.ToLowerInvariant() : shortTarget };
        }
    }

    public static IEnumerable<object[]> InvalidDifficulties
    {
        get
        {
            foreach(var type in new[] { typeof(ConcealJob), typeof(CryptonoteJob), typeof(ZanoJob) })
            foreach(var difficulty in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                yield return new object[] { type, difficulty, type == typeof(ZanoJob) ? 32 : 4 };
        }
    }

    [Theory]
    [MemberData(nameof(InvalidDifficulties))]
    public void TargetEncoding_RejectsNonPositiveOrNonFiniteDifficulty(Type type, double difficulty, int size)
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
}
