using System;
using System.Runtime.InteropServices;
using Miningcore.Crypto.Hashing;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Mining;
using Xunit;

namespace Miningcore.Tests.Crypto;

public class Blake3KeyLengthTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void ConstructorAcceptsUnkeyedAndExactly32ByteKey(int length)
    {
        var key = new byte[length];
        Assert.Same(key, new Blake3IHash(key).dataKey);
        Assert.Null(new Blake3IHash().dataKey);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(1024)]
    public void ConstructorRejectsInvalidKeyLength(int length)
    {
        var error = Assert.Throws<ArgumentException>(() => new Blake3IHash(new byte[length]));
        Assert.Equal("dataKey", error.ParamName);
    }

    private sealed class MutableHasher : Blake3IHash
    {
        internal void ReplaceKey(byte[] key) => dataKey = key;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(33)]
    public void ProtectedSetterCannotBypassDigestValidationOrOverwriteOutput(int length)
    {
        var hasher = new MutableHasher();
        hasher.ReplaceKey(new byte[length]);
        var output = new byte[32];
        Array.Fill(output, (byte) 0xa5);
        Assert.Throws<InvalidOperationException>(() => hasher.Digest(new byte[] { 0, 1, 2 }, output));
        Assert.All(output, value => Assert.Equal((byte) 0xa5, value));
    }

    [Theory]
    [InlineData("", "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262")]
    [InlineData("000102", "e1be4d7a8ab5560aa4199eea339849ba8e293d55ca0a81006726d184519e647f")]
    public void SupportedRuntimeHashesNullAndEmptyKeysEquivalently(string inputHex, string expectedHex)
    {
        if(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            Assert.True(Blake3NativeCapability.IsAvailable());
        if(!Blake3NativeCapability.IsAvailable())
        {
            // The bundled Windows runtime is unsupported: verify rejection rather
            // than claiming that native digest equivalence executed on that host.
            Assert.Throws<TrustedPoolStartupException>(() => Blake3NativeCapability.EnsureAvailable("empty-key-test"));
            return;
        }
        var input = Convert.FromHexString(inputHex);
        var expected = Convert.FromHexString(expectedHex);
        foreach(var key in new[] { null, Array.Empty<byte>() })
        {
            var output = new byte[32];
            new Blake3IHash(key).Digest(input, output);
            Assert.Equal(expected, output);
        }
    }
}
