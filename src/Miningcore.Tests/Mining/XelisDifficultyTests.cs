using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Microsoft.IO;
using Miningcore.Blockchain.Xelis;
using Miningcore.Blockchain.Xelis.DaemonResponses;
using Miningcore.Crypto;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NLog;
using Newtonsoft.Json;
using Xunit;

namespace Miningcore.Tests.Mining;

public class XelisDifficultyTests
{
    // Independent integer-division vectors, also reproducible with Python's int // int.
    [Theory]
    [InlineData("1", "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("2", "7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("3", "5555555555555555555555555555555555555555555555555555555555555555")]
    [InlineData("128", "01ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("255", "0101010101010101010101010101010101010101010101010101010101010101")]
    [InlineData("256", "00ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("257", "00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff")]
    [InlineData("1024", "003fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("71135336520", "000000000f74e4d461f658b27513d9d60a9dbf9a52ac50a29738cb8d1925979d")]
    [InlineData("9007199254740991", "0000000000000800000000000040000000000002000000000000100000000000")]
    [InlineData("9007199254740993", "00000000000007ffffffffffffc0000000000001fffffffffffff00000000000")]
    [InlineData("115792089237316195423570985008687907853269984665640564039457584007913129639935", "0000000000000000000000000000000000000000000000000000000000000001")]
    public void IndependentTargets(string difficulty, string target)
    {
        var value = BigInteger.Parse(difficulty, CultureInfo.InvariantCulture);
        var expected = Convert.FromHexString(target);
        Assert.Equal(expected, XelisUtils.DifficultyToTarget(value));
        var job = Init(value);
        Assert.Equal(expected, job.blockTargetBytes);
        Assert.Equal(new BigInteger(expected, true, true), new BigInteger(job.blockTargetValue.ToBytes(), true));
    }

    [Fact]
    public void InvalidIntegerDifficultyFailsBeforeTemplatePublication()
    {
        foreach(var value in new[] { BigInteger.Zero, -BigInteger.One, BigInteger.One << 256 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => XelisUtils.DifficultyToTarget(value));
            Assert.Throws<ArgumentOutOfRangeException>(() => Init(value));
        }
    }

    [Theory]
    [InlineData("0")] [InlineData("-1")] [InlineData("1.5")] [InlineData("1e3")]
    [InlineData("null")] [InlineData("true")] [InlineData("[]")] [InlineData("{}")]
    [InlineData("NaN")] [InlineData("Infinity")] [InlineData("-Infinity")] [InlineData("1e309")]
    [InlineData("\"NaN\"")] [InlineData("\"Infinity\"")] [InlineData("\"+1\"")]
    [InlineData("\" 1\"")] [InlineData("\"1e3\"")] [InlineData("\"1.0\"")]
    [InlineData("\"115792089237316195423570985008687907853269984665640564039457584007913129639936\"")]
    public void RpcRejectsMalformedDifficulty(string json)
    {
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<GetBlockTemplateResponse>("{\"difficulty\":" + json + "}"));
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<GetBlockHeaderResponse>("{\"difficulty\":" + json + "}"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RpcPreservesFullWidthInteger(bool quoted)
    {
        var value = XelisConstants.Diff1Target.ToString(CultureInfo.InvariantCulture);
        var json = "{\"difficulty\":" + (quoted ? "\"" + value + "\"" : value) + "}";
        Assert.Equal(XelisConstants.Diff1Target, JsonConvert.DeserializeObject<GetBlockTemplateResponse>(json).Difficulty);
        Assert.Equal(XelisConstants.Diff1Target, JsonConvert.DeserializeObject<GetBlockHeaderResponse>(json).Difficulty);
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<GetBlockTemplateResponse>("{\"difficulty\":\"" + new string('1', 1000) + "\"}"));
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<GetBlockTemplateResponse>("{}"));
    }

    private sealed class FixedHash(byte[] proof) : IHashAlgorithm
    {
        public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra) => proof.CopyTo(result);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("xel/v3")] [InlineData("unknown")]
    public void InitRejectsUnsupportedAlgorithmsBeforeIssuance(string algorithm)
    {
        var source = Init(1);
        source.BlockTemplate.Algorithm = algorithm;
        Assert.Throws<System.IO.InvalidDataException>(() => new XelisJob().Init(source.BlockHeader, source.BlockTemplate,
            "invalid", new MockMasterClock(), "stagenet", 1, source.PrevHash));
    }

    [Fact]
    public void InitRejectsInconsistentExactDifficulty()
    {
        var source = Init(1);
        source.BlockHeader.Difficulty = 2;
        Assert.Throws<System.IO.InvalidDataException>(() => new XelisJob().Init(source.BlockHeader, source.BlockTemplate,
            "invalid", new MockMasterClock(), "stagenet", 1, source.PrevHash));
    }

    // Control the digest only; run production initialization, issuance, validation and accounting.
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(257)] [InlineData(71135336520L)]
    public void CandidateBoundaryAndShareCreditUseCorrectedTarget(long difficulty)
    {
        var target = XelisConstants.Diff1Target / difficulty;
        foreach(var offset in new[] { -1, 0, 1 })
        {
            if(target + offset > XelisConstants.Diff1Target) continue;
            var job = Init(difficulty);
            job.BlockTemplate.Algorithm = "xel/v3"; // Published jobs retain their validated algorithm snapshot.
            NonBitcoinCandidateCreditTests.Set(job, "xelisHash", new FixedHash(Bytes(target + offset)));
            NonBitcoinCandidateCreditTests.Set(job, "blake3Hasher", new FixedHash(new byte[32]));
            var worker = Worker(difficulty * 100d);
            var issued = job.ForWorker(worker.ContextAs<XelisWorkerContext>());
            if(offset <= 0)
            {
                var result = issued.ProcessShare(worker, "0000000000000001").Share;
                Assert.True(result.IsBlockCandidate);
                Assert.InRange(result.Difficulty, difficulty * .999999999, difficulty * 1.000000001);
                Assert.Equal((double) difficulty, result.NetworkDifficulty);
                Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() => issued.ProcessShare(worker, "0000000000000001")).Code);
            }
            else
                Assert.Equal(StratumError.LowDifficultyShare, Assert.Throws<StratumException>(() => issued.ProcessShare(worker, "0000000000000001")).Code);
        }
        if(difficulty > 1)
        {
            var job = Init(difficulty);
            NonBitcoinCandidateCreditTests.Set(job, "xelisHash", new FixedHash(Bytes(target + 1)));
            var worker = Worker(difficulty / 2d);
            var result = job.ForWorker(worker.ContextAs<XelisWorkerContext>()).ProcessShare(worker, "0000000000000002").Share;
            Assert.False(result.IsBlockCandidate);
            Assert.Equal(difficulty / 2d, result.Difficulty);
        }
    }

    internal static byte[] Bytes(BigInteger value)
    {
        var result = new byte[32];
        var bytes = value.ToByteArray(true, true);
        bytes.CopyTo(result.AsSpan(32 - bytes.Length));
        return result;
    }

    internal static XelisJob Init(BigInteger difficulty)
    {
        var job = new XelisJob();
        job.Init(new GetBlockHeaderResponse { Template = new string('0', 256), TopoHeight = 1, Difficulty = difficulty, Algorithm = "xel/v1" },
            new GetBlockTemplateResponse { Template = new string('0', 224), Difficulty = difficulty, Algorithm = "xel/v1" },
            "test", new MockMasterClock(), "stagenet", 1d, new string('0', 64));
        return job;
    }

    internal static StratumConnection Worker(double difficulty)
    {
        var context = new XelisWorkerContext { ExtraNonce1 = new string('0', 64) };
        context.Init(difficulty, null, new MockMasterClock());
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), new MockMasterClock(), "test", true);
        worker.SetContext(context);
        return worker;
    }
}
