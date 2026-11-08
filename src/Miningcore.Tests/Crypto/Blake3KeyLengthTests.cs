using System;
using Miningcore.Crypto.Hashing.Algorithms;
using Xunit;

namespace Miningcore.Tests.Crypto;

public class Blake3KeyLengthTests
{
    [Theory]
    [InlineData(0)] [InlineData(32)]
    public void ConstructorAcceptsUnkeyedAndExactly32ByteKey(int length)
    {
        var key = new byte[length];
        Assert.Same(key, new Blake3IHash(key).dataKey);
        Assert.Null(new Blake3IHash().dataKey);
    }

    [Theory]
    [InlineData(1)] [InlineData(31)] [InlineData(33)] [InlineData(1024)]
    public void ConstructorRejectsInvalidLengthBeforeLoadingNativeCode(int length)
    {
        var error = Assert.Throws<ArgumentException>(() => new Blake3IHash(new byte[length]));
        Assert.Equal("dataKey", error.ParamName);
    }

    private sealed class MutableHasher : Blake3IHash
    {
        internal void ReplaceKey(byte[] key) => dataKey = key;
    }

    [Theory]
    [InlineData(1)] [InlineData(31)] [InlineData(33)]
    public void ProtectedSetterCannotBypassDigestValidationOrOverwriteOutput(int length)
    {
        var hasher = new MutableHasher();
        hasher.ReplaceKey(new byte[length]);
        var output = new byte[32];
        Array.Fill(output, (byte) 0xa5);
        Assert.Throws<ArgumentException>(() => hasher.Digest(new byte[] { 0, 1, 2 }, output));
        Assert.All(output, value => Assert.Equal((byte) 0xa5, value));
    }
}
