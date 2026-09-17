using System;
using System.Linq;
using Miningcore.Crypto.Hashing.Algorithms;
using Xunit;

namespace Miningcore.Tests.Crypto;

public class RinHashCompatibilityTests
{
    // Captured from the complete RinHash pipeline with Blake3 0.5.1 before
    // migrating to the managed 3.x implementation. Cover an empty input, a
    // short input, a block header, and input spanning BLAKE3's 1024-byte chunks.
    [Theory]
    [InlineData(0, "56a14ac4106071e23d5da83f01b5373ff5dc7c3cb65c2a710872d0d9c552c0c2")]
    [InlineData(3, "2cf9e242fa1a195a143acc42c1d4c31b1efcb34d26f0a176ff65604b1b1acb54")]
    [InlineData(80, "f79c08256a6db8827ae84d2575539813e9b05f62939c8235de2df1ffed12142a")]
    [InlineData(1025, "4d91d42b323068a624d1817ecbad87b062f3de03cdb2913f937adb0c9865b0eb")]
    public void Digest_MatchesLegacyBlake3(int length, string expected)
    {
        var input = Enumerable.Range(0, length).Select(x => (byte) x).ToArray();
        var digest = new byte[32];
        new RinHash().Digest(input, digest);
        Assert.Equal(expected, Convert.ToHexString(digest).ToLowerInvariant());
    }
}
