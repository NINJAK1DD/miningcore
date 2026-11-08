using System;
using System.Linq;
using System.Runtime.InteropServices;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Alephium;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Blockchain.Xelis;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Tests.Mining;
using Miningcore.Time;
using NSubstitute;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Xunit;

namespace Miningcore.Tests.Crypto;

public class Blake3NativeCapabilityTests
{
    internal static byte[] ReferenceHash(ReadOnlySpan<byte> input, byte[] key = null)
    {
        var digest = new Blake3Digest(256);
        if(key != null) digest.Init(Blake3Parameters.Key(key));
        digest.BlockUpdate(input);
        var output = new byte[32];
        digest.DoFinal(output);
        return output;
    }

    private sealed class Hasher(byte[] key, string mode) : IHashAlgorithm
    {
        public void Digest(ReadOnlySpan<byte> input, Span<byte> output, params object[] extra)
        {
            if(mode == "missing") throw new EntryPointNotFoundException();
            if(mode == "unwritten") return;
            if(mode == "zero") { output.Clear(); return; }
            var result = ReferenceHash(mode == "ignores-input" ? ReadOnlySpan<byte>.Empty : input,
                mode == "ignores-key" ? null : key);
            if(mode == "wrong") result[0] ^= 1;
            result.CopyTo(output);
        }
    }

    [Theory]
    [InlineData("valid", true)] [InlineData("missing", false)] [InlineData("unwritten", false)]
    [InlineData("zero", false)] [InlineData("wrong", false)] [InlineData("ignores-input", false)]
    [InlineData("ignores-key", false)]
    public void RequiresCorrectUnkeyedAndKeyedHashes(string mode, bool expected)
    {
        Assert.Equal(expected, Blake3NativeCapability.IsAvailable(name => name == "blake3_export",
            key => new Hasher(key, mode)));
    }

    [Fact]
    public void MissingExportAndLoaderFailuresFailClosedBeforeHashing()
    {
        Assert.False(Blake3NativeCapability.IsAvailable(_ => false, _ => throw new Exception("must not hash")));
        foreach(var failure in new Exception[] { new DllNotFoundException(), new EntryPointNotFoundException(), new BadImageFormatException() })
            Assert.False(Blake3NativeCapability.IsAvailable(_ => throw failure, _ => throw new Exception("must not hash")));
    }

    private sealed class AlephiumProbe() : AlephiumJobManager(Substitute.For<IComponentContext>(),
        Substitute.For<IMessageBus>(), new StandardClock(), Substitute.For<IExtraNonceProvider>())
    {
        internal bool ConfiguredDaemons;
        protected override void ConfigureDaemons() => ConfiguredDaemons = true;
    }

    private sealed class KaspaProbe() : KaspaJobManager(Substitute.For<IComponentContext>(),
        Substitute.For<IMessageBus>(), new StandardClock(), Substitute.For<IExtraNonceProvider>())
    {
        internal bool ConfiguredDaemons;
        protected override void ConfigureDaemons() => ConfiguredDaemons = true;
    }

    private static PoolConfig Pool(CoinTemplate template) => new()
    {
        Id = "native-capability-test", Template = template,
        PaymentProcessing = new PoolPaymentProcessingConfig(), Daemons = [],
    };

    [Fact]
    public void AlephiumChecksNativeCapabilityBeforeConfiguringDaemons()
    {
        var manager = new AlephiumProbe();
        var pool = Pool(new AlephiumCoinTemplate { Symbol = "ALPH" });
        if(Blake3NativeCapability.IsAvailable())
            manager.Configure(pool, new ClusterConfig());
        else
            Assert.Contains("blake3_export", Assert.Throws<TrustedPoolStartupException>(
                () => manager.Configure(pool, new ClusterConfig())).Message);
        Assert.Equal(Blake3NativeCapability.IsAvailable(), manager.ConfiguredDaemons);
    }

    [Theory]
    [InlineData("AIX", true)] [InlineData("CAS", true)] [InlineData("HTN", true)]
    [InlineData("KLS", true)] [InlineData("CSS", true)] [InlineData("NTL", true)]
    [InlineData("NXL", true)] [InlineData("PUG", true)] [InlineData("PYI", true)]
    [InlineData("WALA", true)] [InlineData("KAS", false)] [InlineData("SPR", false)]
    public void KaspaChecksOnlyDependentFamiliesBeforeConfiguringDaemons(string symbol, bool required)
    {
        Assert.Equal(required, KaspaJobManager.RequiresBlake3(symbol));
        var manager = new KaspaProbe();
        var pool = Pool(new KaspaCoinTemplate { Symbol = symbol });
        var allowed = !required || Blake3NativeCapability.IsAvailable();
        if(allowed)
            manager.Configure(pool, new ClusterConfig());
        else
            Assert.Contains("blake3_export", Assert.Throws<TrustedPoolStartupException>(
                () => manager.Configure(pool, new ClusterConfig())).Message);
        Assert.Equal(allowed, manager.ConfiguredDaemons);
    }

    [Fact]
    public void RuntimeChecksAndRealXelisCandidateIdentitiesAgreeWithIndependentBlake3()
    {
        if(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            Assert.True(Blake3NativeCapability.IsAvailable());
            Assert.True(XelisNativeCapability.IsAvailable());
        }
        if(!Blake3NativeCapability.IsAvailable())
        {
            Assert.False(XelisNativeCapability.IsAvailable());
            return; // Unsupported runtime is tested as rejection, never certified by a skip.
        }
        Blake3NativeCapability.EnsureAvailable("test");
        if(!XelisNativeCapability.IsAvailable()) return;
        foreach(var algorithm in new[] { "xel/v1", "xel/v2" })
        {
            var job = XelisDifficultyTests.Init(1, algorithm);
            var worker = XelisDifficultyTests.Worker(1);
            var issued = job.ForWorker(worker.ContextAs<XelisWorkerContext>());
            var identities = new string[2];
            for(var i = 0; i < identities.Length; i++)
            {
                var nonce = (i + 1).ToString("x16");
                // Difficulty one makes every real proof a candidate. No hash mocks.
                var (share, solved) = issued.ProcessShare(worker, nonce);
                var work = new byte[112];
                Convert.FromHexString(nonce).CopyTo(work, 40);
                Assert.True(share.IsBlockCandidate);
                Assert.Equal(Convert.ToHexString(ReferenceHash(work)).ToLowerInvariant(), share.BlockHash);
                Assert.Equal(nonce, solved.Substring(34, 16));
                identities[i] = share.BlockHash;
            }
            Assert.NotEqual(identities[0], identities[1]);
        }
    }
}
