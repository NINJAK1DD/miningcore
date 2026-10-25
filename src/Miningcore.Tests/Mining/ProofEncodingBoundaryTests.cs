using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Alephium;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Equihash;
using Miningcore.Blockchain.Equihash.Custom.Veruscoin;
using Miningcore.Blockchain.Handshake;
using Miningcore.Blockchain.Nexa;
using Miningcore.Blockchain.Progpow;
using Miningcore.Blockchain.Satoshicash;
using Miningcore.Blockchain.Warthog;
using Miningcore.Blockchain.Xelis;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NLog;
using Xunit;

namespace Miningcore.Tests.Mining;

// Real outer proof validation, assignment and accepted-proof registries. The
// expensive consensus verifier is controlled; native/TCP proofs are separate.
public class ProofEncodingBoundaryTests
{
    private static readonly MockMasterClock Clock = new() { CurrentTime = DateTime.UnixEpoch.AddSeconds(1700000000) };
    private const string Time = "6553f100";
    public static IEnumerable<object[]> Fields => new[]
    {
        ("Bitcoin", "extraNonce2"), ("Bitcoin", "ntime"), ("Bitcoin", "nonce"),
        ("Handshake", "extraNonce2"), ("Handshake", "ntime"), ("Handshake", "nonce"),
        ("Satoshicash", "extraNonce2"), ("Satoshicash", "ntime"), ("Satoshicash", "nonce"),
        ("Warthog", "extraNonce2"), ("Warthog", "ntime"), ("Warthog", "nonce"),
        ("Nexa", "nonce"), ("Xelis", "nonce"), ("Alephium", "nonce"),
        ("Equihash", "extraNonce2"), ("Equihash", "ntime"), ("Equihash", "solution"),
        ("Veruscoin", "extraNonce2"), ("Veruscoin", "ntime"), ("Veruscoin", "solution"),
        ("Progpow", "nonce"), ("Progpow", "header"), ("Progpow", "mix")
    }.Select(x => new object[] { x.Item1, x.Item2 });

    internal static string NibbleAlias(string value)
    {
        var chars = value.ToCharArray();
        for(var i = 0; i < chars.Length; i += 2)
        {
            var high = Convert.ToInt32(chars[i].ToString(), 16);
            var low = Convert.ToInt32(chars[i + 1].ToString(), 16);
            if(high == 0) continue;
            chars[i] = "0123456789abcdef"[high - 1];
            chars[i + 1] = (char) ('g' + low); // Permissive decoder treats this as 16+low.
            return new string(chars);
        }
        throw new InvalidOperationException("Vector needs a nonzero high nibble");
    }

    private static Share Credit(StratumConnection worker, object job) => new()
        { Difficulty = worker.Context.ValidateShareDifficulty(20, false, job) };
    private static string Hex(int length) => "10" + new string('0', length - 4) + "ab";

    [Theory]
    [InlineData("Cryptonote", 8)]
    [InlineData("Conceal", 8)]
    [InlineData("Zano", 16)]
    public void BlobNoncesRejectTrailingNewlineAndOtherAliasesBeforeNativeWork(string family, int width)
    {
        var assembly = typeof(WorkerContextBase).Assembly;
        var jobType = assembly.GetType($"Miningcore.Blockchain.{family}.{family}Job");
        var job = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(jobType);
        var context = (WorkerContextBase) Activator.CreateInstance(assembly.GetType($"Miningcore.Blockchain.{family}.{family}WorkerContext"));
        context.Init(10, null, Clock);
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), Clock, "blob-alias", true);
        worker.SetContext(context);
        var nonce = Hex(width);
        foreach(var alias in new[] { NibbleAlias(nonce), "0x" + nonce, nonce + "f", nonce + "\n" })
        {
            object[] arguments = family == "Zano" ? new object[] { LogManager.GetCurrentClassLogger(), alias, 1u, worker } :
                new object[] { alias, 1u, new string('0', 64), worker };
            var thrown = Assert.Throws<TargetInvocationException>(() => jobType.GetMethod("ProcessShare").Invoke(job, arguments));
            Assert.Equal("malformed nonce", Assert.IsType<StratumException>(thrown.InnerException).Message);
        }
    }
    private static void Set(object value, string name, object field)
    {
        for(var type = value.GetType(); type != null; type = type.BaseType)
        {
            if(type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) is { } property)
            { property.SetValue(value, field); return; }
            if(type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) is { } member)
            { member.SetValue(value, field); return; }
        }
        throw new InvalidOperationException(name);
    }

    private sealed class Bitcoin : BitcoinJob
    {
        protected override (Share Share, string BlockHex) ProcessShareInternal(StratumConnection worker, string en2, uint time, uint nonce, uint? version) => (Credit(worker, this), null);
    }
    private sealed class Handshake : HandshakeJob
    {
        protected override (Share Share, string BlockHex) ProcessShareInternal(StratumConnection worker, string en2, uint time, uint nonce) => (Credit(worker, this), null);
    }
    private sealed class Satoshicash : SatoshicashJob
    {
        public override (Share Share, string BlockHex) ProcessShareInternal(StratumConnection worker, string en2, uint time, uint nonce) => (Credit(worker, this), null);
    }
    private sealed class Warthog : WarthogJob
    {
        protected override (Share Share, string HeaderHex) ProcessShareInternal(StratumConnection worker, string en2, uint time, uint nonce) => (Credit(worker, this), null);
    }
    private sealed class Nexa : NexaJob
    {
        protected override (Share Share, object submitParams) ProcessShareInternal(StratumConnection worker, string nonce, string en1) => (Credit(worker, this), null);
    }
    private sealed class Xelis : XelisJob
    {
        protected override (Share Share, string BlockTemplate) ProcessShareInternal(StratumConnection worker, string nonce) => (Credit(worker, this), null);
    }
    private sealed class Alephium : AlephiumJob
    {
        protected override Share ProcessShareInternal(StratumConnection worker, string nonce) => Credit(worker, this);
    }
    private sealed class Equihash : EquihashJob
    {
        protected override (Share Share, string BlockHex) ProcessShareInternal(StratumConnection worker, string nonce, uint time, string solution) => (Credit(worker, this), null);
    }
    private sealed class Veruscoin : VeruscoinJob
    {
        protected override (Share Share, string BlockHex) ProcessShareInternal(StratumConnection worker, string nonce, uint time, string solution) => (Credit(worker, this), null);
    }
    private sealed class Progpow : ProgpowJob
    {
        public override (Share Share, string BlockHex) ProcessShareInternal(ILogger logger, StratumConnection worker, ulong nonce, string header, string mix) => (Credit(worker, this), null);
    }

    [Fact]
    public void VerusPbaasUsesActualNonceAndAllowsRetryAfterRejectedProof()
    {
        var context = new EquihashWorkerContext { ExtraNonce1 = "10000001" };
        context.Init(100, null, Clock);
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), Clock, "pbaas", true);
        worker.SetContext(context);
        var source = new Veruscoin { isPBaaSActive = true };
        Set(source, "clock", Clock);
        Set(source, "BlockTemplate", new Miningcore.Blockchain.Equihash.DaemonResponses.EquihashBlockTemplate { CurTime = 1700000000, Nonce = Hex(64) });
        Set(source, "networkParams", new EquihashCoinTemplate.EquihashNetworkParams { SolutionSize = 1344, SolutionPreambleSize = 3 });
        var solution = Hex(2694 - 30) + context.ExtraNonce1 + new string('0', 22);
        NonBitcoinCandidateCreditTests.ConfigurePolicy(source);
        var hard = source.ForWorker(context);
        Assert.Equal(StratumError.LowDifficultyShare, Assert.Throws<StratumException>(() => hard.ProcessShare(worker, Hex(56), "00f15365", solution)).Code);
        context.SetDifficulty(10);
        var easy = source.ForWorker(context, () => "2");
        Assert.Equal(10, easy.ProcessShare(worker, Hex(56), "00f15365", solution).Share.Difficulty);
        Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() =>
            easy.ProcessShare(worker, new string('f', 56), "00f15365", solution.ToUpperInvariant())).Code);
    }

    [Theory]
    [InlineData("Bitcoin", 10)]
    [InlineData("Bitcoin", 12)]
    [InlineData("Handshake", 10)]
    [InlineData("Handshake", 12)]
    [InlineData("Satoshicash", 10)]
    [InlineData("Satoshicash", 12)]
    public void ExtranonceWidthFollowsJobLayout(string family, int placeholderBytes)
    {
        var source = Activator.CreateInstance(typeof(ProofEncodingBoundaryTests).GetNestedTypes(BindingFlags.NonPublic).Single(x => x.Name == family));
        WorkerContextBase context = family switch
        {
            "Bitcoin" => new BitcoinWorkerContext(), "Handshake" => new HandshakeWorkerContext(),
            _ => new SatoshicashWorkerContext()
        };
        context.Init(10, null, Clock);
        Set(context, "ExtraNonce1", "10000001");
        Set(source, "extraNoncePlaceHolderLength", placeholderBytes);
        Set(source, "clock", Clock);
        var template = Activator.CreateInstance(source.GetType().BaseType.GetProperty("BlockTemplate").PropertyType);
        Set(template, "CurTime", 1700000000u);
        Set(source, "BlockTemplate", template);
        NonBitcoinCandidateCreditTests.ConfigurePolicy(source);
        var job = context.JobForDifficulty(source, (_, _) => typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(source, null), "layout");
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), Clock, "layout", true);
        worker.SetContext(context);
        Share Submit(string en2) => family switch
        {
            "Bitcoin" => ((BitcoinJob) job).ProcessShare(worker, en2, Time, "00000001").Share,
            "Handshake" => ((HandshakeJob) job).ProcessShare(worker, en2, Time, "00000001").Share,
            _ => ((SatoshicashJob) job).ProcessShare(worker, en2, Time, "00000001").Share
        };
        Assert.Equal("incorrect size of extraNonce2", Assert.Throws<StratumException>(() => Submit(Hex(14))).Message);
        var valid = Hex(placeholderBytes * 2 - 8);
        Assert.Equal(10, Submit(valid).Difficulty);
        Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() => Submit(valid.ToUpperInvariant())).Code);
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void SubmittedHexAliasesNeverReachAnotherAcceptedCredit(string family, string field)
    {
        var types = typeof(ProofEncodingBoundaryTests).GetNestedTypes(BindingFlags.NonPublic);
        var source = Activator.CreateInstance(types.Single(x => x.Name == family));
        WorkerContextBase context = family switch
        {
            "Bitcoin" => new BitcoinWorkerContext(), "Handshake" => new HandshakeWorkerContext(),
            "Satoshicash" => new SatoshicashWorkerContext(), "Warthog" => new WarthogWorkerContext(),
            "Nexa" => new NexaWorkerContext(), "Xelis" => new XelisWorkerContext(),
            "Alephium" => new AlephiumWorkerContext(), "Progpow" => new ProgpowWorkerContext(),
            _ => new EquihashWorkerContext()
        };
        context.Init(10, null, Clock);
        Set(context, "ExtraNonce1", "10000001");
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), Clock, "encoding", true);
        worker.SetContext(context);
        if(family is "Bitcoin" or "Handshake" or "Satoshicash")
        {
            Set(source, "extraNoncePlaceHolderLength", BitcoinConstants.ExtranoncePlaceHolderLength);
            Set(source, "clock", Clock);
            var templateType = source.GetType().BaseType.GetProperty("BlockTemplate").PropertyType;
            var template = Activator.CreateInstance(templateType);
            Set(template, "CurTime", 1700000000u);
            Set(source, "BlockTemplate", template);
        }
        if(family == "Warthog")
        { Set(source, "clock", Clock); Set(source, "nTimeBytes", Convert.FromHexString(Time)); }
        if(family == "Nexa")
            Set(source, "MiningCandidate", new Miningcore.Blockchain.Nexa.DaemonResponses.MiningCandidate { HeaderCommitment = Hex(64) });
        if(family is "Equihash" or "Veruscoin")
        {
            Set(source, "clock", Clock);
            Set(source, "BlockTemplate", new Miningcore.Blockchain.Equihash.DaemonResponses.EquihashBlockTemplate { CurTime = 1700000000 });
            Set(source, "networkParams", new EquihashCoinTemplate.EquihashNetworkParams { SolutionSize = 1344, SolutionPreambleSize = 3 });
        }
        NonBitcoinCandidateCreditTests.ConfigurePolicy(source);
        var original = context.JobForDifficulty(source, (_, _) => typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(source, null), "1");
        context.SetDifficulty(15);
        var current = context.JobForDifficulty(source, (_, _) => typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(source, null), "1", () => "2");
        var en2 = Hex(family == "Warthog" ? 12 : family is "Equihash" or "Veruscoin" ? 56 : 14);
        var nonce = Hex(family == "Alephium" ? 48 : family is "Nexa" or "Xelis" or "Progpow" ? 16 : 8);
        var solution = Hex(family is "Equihash" or "Veruscoin" ? 2694 : 64);
        var header = Hex(64);
        var time = family is "Equihash" or "Veruscoin" ? "00f15365" : Time;
        if(family == "Progpow") nonce = "1000" + nonce[4..];
        Share Submit(object job, string alias = null)
        {
            var e = field == "extraNonce2" ? alias ?? en2 : en2;
            var n = field == "nonce" ? alias ?? nonce : nonce;
            var t = field == "ntime" ? alias ?? time : time;
            var s = field is "solution" or "mix" ? alias ?? solution : solution;
            var h = field == "header" ? alias ?? header : header;
            return family switch
            {
                "Bitcoin" => ((BitcoinJob) job).ProcessShare(worker, e, t, n).Share,
                "Handshake" => ((HandshakeJob) job).ProcessShare(worker, e, t, n).Share,
                "Satoshicash" => ((SatoshicashJob) job).ProcessShare(worker, e, t, n).Share,
                "Warthog" => ((WarthogJob) job).ProcessShare(worker, e, t, n).Share,
                "Nexa" => ((NexaJob) job).ProcessShare(worker, n, "10000001").Share,
                "Xelis" => ((XelisJob) job).ProcessShare(worker, n).Share,
                "Alephium" => ((AlephiumJob) job).ProcessShare(worker, n),
                "Equihash" or "Veruscoin" => ((EquihashJob) job).ProcessShare(worker, e, t, s).Share,
                _ => new ProgpowWorkerJob("worker", "10000001") { Job = (ProgpowJob) job }.ProcessShare(LogManager.GetCurrentClassLogger(), worker, n, h, s).Share
            };
        }
        var value = field switch { "extraNonce2" => en2, "ntime" => time, "nonce" => nonce, "header" => header, _ => solution };
        foreach(var alias in new[] { NibbleAlias(value), "0x" + value, value + "f", value + "z", value + "\n", " " + value[1..] })
        {
            if(family == "Alephium") Assert.Throws<AlephiumStratumException>(() => Submit(original, alias));
            else Assert.Throws<StratumException>(() => Submit(original, alias));
        }
        Assert.Equal(10, Submit(original).Difficulty);
        if(family == "Alephium") Assert.Throws<AlephiumStratumException>(() => Submit(current, value.ToUpperInvariant()));
        else Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() => Submit(current, value.ToUpperInvariant())).Code);
    }
}
