using System;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using Miningcore.Crypto.Hashing;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Mining;
using Xunit;

namespace Miningcore.Tests.Crypto;

public class Blake2bKeyLengthTests
{
    // Fixed known answers generated independently with Python hashlib.blake2b.
    [Theory]
    [InlineData(0,
        "0e5751c026e543b2e8ab2eb06099daa1d1e5df47778f7787faab45cdf12fe3a8",
        "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce",
        "3d8c3d594928271f44aad7a04b177154806867bcf918e1549c0bc16f9da2b09b",
        "40a374727302d9a4769c17b5f409ff32f58aa24ff122d7603e4fda1509e919d4107a52c57570a6d94e50967aea573b11f86f473f537565c66f7039830a85d186")]
    [InlineData(1,
        "52897a9c27e9781839168d22a76145b07a8a7f8cd85c1d3709e5b09468405099",
        "aaf42280524929171e417e77be67f9edec3a8461bbe7b5c2bd1d9a3d0928f1dbbd1f6600bb866b72f0e3b3e22282c145f69873a3d250ddc43c423685d1247657",
        "f77f5fe3ea70d07bf2221f86e28ec125992762cda0b918a81c602ab2517d2dad",
        "e14360c6075f4a96bbf00654dec0eabc94ef2d65219fdb4860b512ec9a01d2cdccb1e39ecf1fae8663bf1f4d1833058bd6660e1d6da533c41c9fe1fef4bb616f")]
    [InlineData(9,
        "a80b6aa20f20b15ebabe2b1949527f78a257594a732e774de637d85e6973a768",
        "3ea8cf6d26e3d5d7c161310f8613444ff14a8cf8a4f00f0f409e095fd3a0100c7e6b11ff995c2da3d5645ba8cdd923d07220ec0fdd72faffda920dc2227bbc1a",
        "6f97f3b44764fcce84e8570a2c84ae99e68f4d2178ed826f04d7c20d051e3636",
        "f8cd56827a7d04c63f128a77cdb9567f1b7341249c5806c260f146d08176bb03cd7214274fb9802a6d67767aa9addd5ae2c52cf27cb62eea5511d20fe9ad73ed")]
    [InlineData(31,
        "fbf1e801e71b6de84200b69c351218b989b7064d3111d993cc975e21159005da",
        "66997d91cb848d5d2357f2a81c5179aed882dc2fef64e4261e13e24dcd6f30fdf7be2165fb64a196bfb0edd5b28d3e2cc3fa0462383aefd824928aecaf23507a",
        "3ef0ffed04e454a1216c98814a5b181eb1974c0c5826ada210a96697a2303706",
        "d21d51ea82f4491d19471c36a87517c994f75688f1a49a32137cb9e05d484c23772454091d937cae44977722ed978c67a42fce189fc3a7587b6b3da9a904de6c")]
    [InlineData(32,
        "4e51e7a913fc80137da52880fecca175bf81e117d5c68126dc2774033517ea0d",
        "84bfa69f0d90df7db2a3ee026042988b5bd9caa2320af1f371823dd28351202f8e6277c40c050711c8dd4e2c1ac30c34c9aed0bddd468b031287fe872675e0cc",
        "e14fc9161564dd081204f2dd6146a9ffbef66f95d5dc80e0a225e213c09dad7b",
        "2346d2f70d774641a330c2a050bae000985fb90a6619c5512d609c0531b769710d92b80cf9a544cbdee6d2ab51fc8b6cc1839245fb63e074409dec7c0dc330c9")]
    [InlineData(33,
        "b4f3c9aa56e2585ac7cb1740d266f2a92f2165048167d3ec5b22b7f859f0e7ad",
        "1e97aca75bbc21527bfc99f9801858e015af2020d505356d4384ce2aa6017e96403bbcd758adc01a94e4de8e65b5085b657aab0c963ddc5934dd24311f21940f",
        "ed4d6f5723f13ab74633eb597f60ff29baf2dc300cb46f739d66dbaa12e241f5",
        "4c2d4a5f79d67f71f0c878507015754aaffe3cc0455aba6387b9cee20d252b7ba773a8652d06dfe2ed78946bd9408458797038bad02f668c1366a1fa8516c938")]
    [InlineData(64,
        "2fa9fbd9be36437de204e139e97d402bce68c828f43391608c891b5faed8a98a",
        "10ebb67700b1868efb4417987acf4690ae9d972fb7a590c2f02871799aaa4786b5e996e8f0f4eb981fc214b005f42d2ff4233499391653df7aefcbc13fc51568",
        "3e57c5ab79418defd6e252719a380096d9abf1901db38e0be7d404eb7206c0dc",
        "33d0825dddf7ada99b0e7e307104ad07ca9cfd9692214f1561356315e784f3e5a17e364ae9dbb14cb2036df932b77f4b292761365fb328de7afdc6d8998f5fc1")]
    public void ValidKeysAndOutputSizesMatchIndependentBlake2b(int keyLength,
        string empty32, string empty64, string input32, string input64)
    {
        var key = keyLength == 9 ? Encoding.ASCII.GetBytes("BlockHash") : Enumerable.Range(0, keyLength).Select(x => (byte) x).ToArray();
        if(keyLength > 0)
        {
            var available = Blake2bNativeCapability.IsKeyedAvailable();
            if(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
                Assert.True(available, "Source-built Linux runtime must support keyed Blake2b");
            if(!available)
            {
                var output = Enumerable.Repeat((byte) 0xa5, 32).ToArray();
                Assert.Throws<TrustedPoolStartupException>(() => new Blake2b(key).Digest(new byte[] { 0, 1, 2 }, output));
                Assert.All(output, value => Assert.Equal((byte) 0xa5, value));
                return;
            }
        }
        var vectors = new[]
        {
            (Input: Array.Empty<byte>(), Expected: empty32),
            (Input: Array.Empty<byte>(), Expected: empty64),
            (Input: new byte[] { 0, 1, 2 }, Expected: input32),
            (Input: new byte[] { 0, 1, 2 }, Expected: input64),
        };
        foreach(var (input, expectedHex) in vectors)
        {
            var expected = Convert.FromHexString(expectedHex);
            var output = new byte[expected.Length];
            new Blake2b(key).Digest(input, output);
            Assert.Equal(expected, output);
            if(keyLength == 0)
            {
                new Blake2b().Digest(input, output);
                Assert.Equal(expected, output);
            }
        }
    }

    [Theory]
    [InlineData(65)]
    [InlineData(1024)]
    public void ConstructorRejectsOversizedKeys(int length)
    {
        Assert.Equal("dataKey", Assert.Throws<ArgumentException>(() => new Blake2b(new byte[length])).ParamName);
    }

    private sealed class MutableHasher : Blake2b
    {
        internal void ReplaceKey(byte[] key) => dataKey = key;
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("ignores-key", false)]
    [InlineData("wrong", false)]
    [InlineData("unwritten", false)]
    [InlineData("missing", false)]
    public void KeyedCapabilityRejectsBrokenNativeImplementations(string mode, bool expected)
    {
        Assert.Equal(expected, Blake2bNativeCapability.IsKeyedAvailable(input =>
        {
            if(mode == "missing") throw new EntryPointNotFoundException();
            if(mode == "unwritten") return new byte[32];
            if(mode == "ignores-key") return Convert.FromHexString(input.Length == 0
                ? "0e5751c026e543b2e8ab2eb06099daa1d1e5df47778f7787faab45cdf12fe3a8"
                : "3d8c3d594928271f44aad7a04b177154806867bcf918e1549c0bc16f9da2b09b");
            var output = Convert.FromHexString(input.Length == 0
                ? "a80b6aa20f20b15ebabe2b1949527f78a257594a732e774de637d85e6973a768"
                : "6f97f3b44764fcce84e8570a2c84ae99e68f4d2178ed826f04d7c20d051e3636");
            if(mode == "wrong" && input.Length > 0) output[0] ^= 1;
            return output;
        }));
    }

    [Fact]
    public void SubclassCannotBypassKeyedRuntimeGuard()
    {
        if(Blake2bNativeCapability.IsKeyedAvailable()) return;
        var hasher = new MutableHasher();
        hasher.ReplaceKey(Encoding.ASCII.GetBytes("BlockHash"));
        var output = Enumerable.Repeat((byte) 0xa5, 32).ToArray();
        Assert.Throws<TrustedPoolStartupException>(() => hasher.Digest(new byte[] { 0, 1, 2 }, output));
        Assert.All(output, value => Assert.Equal((byte) 0xa5, value));
    }

    [Theory]
    [InlineData(65)]
    [InlineData(1024)]
    public void InvalidSubclassStateIsRejectedWithoutWritingOutput(int length)
    {
        var hasher = new MutableHasher();
        hasher.ReplaceKey(new byte[length]);
        var output = Enumerable.Repeat((byte) 0xa5, 32).ToArray();
        Assert.Throws<InvalidOperationException>(() => hasher.Digest(new byte[] { 0, 1, 2 }, output));
        Assert.All(output, value => Assert.Equal((byte) 0xa5, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(65)]
    public void InvalidManagedOutputSizeIsRejectedWithoutWriting(int length)
    {
        var output = Enumerable.Repeat((byte) 0xa5, length).ToArray();
        Assert.Throws<ArgumentException>(() => new Blake2b().Digest(new byte[] { 0, 1, 2 }, output));
        Assert.All(output, value => Assert.Equal((byte) 0xa5, value));
    }
}
