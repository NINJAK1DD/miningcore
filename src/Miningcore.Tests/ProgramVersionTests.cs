using Xunit;

namespace Miningcore.Tests;

public class ProgramVersionTests
{
    [Fact]
    public void SelectVersion_ReleaseMetadataTakesPrecedenceOverGitVersion()
    {
        Assert.Equal(
            "0.1.0-rc.3 [c7e7c1a83985765311878bd8c7be0771a5b2c030]",
            Program.SelectVersion(
                "0.1.0-rc.3",
                "c7e7c1a83985765311878bd8c7be0771a5b2c030",
                "0.1.0-alpha.3039",
                "0123456789abcdef0123456789abcdef01234567"));
    }

    [Fact]
    public void SelectVersion_WithoutReleaseMetadataUsesGitVersion()
    {
        Assert.Equal(
            "0.1.0-alpha.3039 [0123456789abcdef0123456789abcdef01234567]",
            Program.SelectVersion(
                null,
                null,
                "0.1.0-alpha.3039",
                "0123456789abcdef0123456789abcdef01234567"));
    }

    [Theory]
    [InlineData("0.1.0-rc.3", null)]
    [InlineData(null, "c7e7c1a83985765311878bd8c7be0771a5b2c030")]
    public void SelectVersion_IncompleteReleaseMetadataReturnsUnknown(string releaseVersion, string releaseSha)
    {
        Assert.Equal(
            "unknown",
            Program.SelectVersion(
                releaseVersion,
                releaseSha,
                "0.1.0-alpha.3039",
                "0123456789abcdef0123456789abcdef01234567"));
    }

    [Theory]
    [InlineData("0.1.0-rc.2", "3a0f69437ba1f638f40124a241112514a5100989")]
    [InlineData("0.1.1-dev.3+12", "0123456789abcdef0123456789abcdef01234567")]
    public void FormatVersion_PreservesFullSemVerAndCommit(string fullSemVer, string sha)
    {
        Assert.Equal($"{fullSemVer} [{sha}]", Program.FormatVersion(fullSemVer, sha));
    }

    [Theory]
    [InlineData(null, "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0.1.0-rc.2", null)]
    [InlineData("", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0.1.0-rc.2", "")]
    public void FormatVersion_MissingBuildInformationReturnsUnknown(string fullSemVer, string sha)
    {
        Assert.Equal("unknown", Program.FormatVersion(fullSemVer, sha));
    }

    [Fact]
    public void FormatDonationAddresses_PreservesMaintainerAddressesAndReadmeOrder()
    {
        var expected = string.Join(System.Environment.NewLine, new[]
        {
            " Donations to support development and maintenance of this NINJAK1DD Miningcore fork:",
            string.Empty,
            " BTC  - bc1q94x9ncw62g09c80yr38jkewyn6cre3h473g54j",
            " ETH  - 0x4DE55672F0bBB88882A5a589b320eE40FfbdebF9",
            " DOGE - DQKEyZ2sTzcCPeeqzP4xUiPHzwtCS9LUTt",
            " ZEC  - t1TbjCnoNdGWnwEt9QqCZvHuG3MsWf4Bj66",
            " XMR  - 43iiCs5pjvqbzYDvGSPgwtTdR4E4s996cSBsCSTe5HHbSrzr4HBosKZch8t7Fpg34" +
            "DL9dNcN22T7H6JWEC23B9iDLAZqQsp",
            " BCH  - bitcoincash:qzyvaurh8vlj22jvyhpdce6ld4lt3zfc3svyt665de",
            " LTC  - ltc1qgnt28drw663gldx76zp3s28xl58wsp0ccv4vxg",
            " KAS  - kaspa:qzdtdjatlzecrt9u4v22p5vgud6w6ylvemly9df6zpu0gp0yks9xxp24q79pu",
            " ETC  - 0x331e6c8d7Caae3Dd1136EefF6c828dBDe5ae64F0",
            " FIRO - aH1tURoFqY1quNraAtceE6YFPv3DLFo8zT",
            " XEL  - xel:gt8m2j4al22k8ecp99uducy84vnhn2nlx6ftxjgw2rfr0hg5n47sqkec7n4",
            " WART - 4701843e274a2a4dfbac59678cb693233274bf5fefcc4e46",
        });

        Assert.Equal(expected, Program.FormatDonationAddresses());

        var readme = System.IO.File.ReadAllText(
            System.IO.Path.Combine(System.AppContext.BaseDirectory, "README.md"));
        var previousRowIndex = -1;
        var lines = expected.Split(System.Environment.NewLine);

        Assert.Contains(lines[0].Trim(), readme);

        for(var i = 2; i < lines.Length; i++)
        {
            var fields = lines[i].Trim().Split(" - ", 2,
                System.StringSplitOptions.None);
            var symbol = fields[0].Trim();
            var row = $"| {symbol} | `{fields[1]}` |";
            var rowIndex = readme.IndexOf(row, System.StringComparison.Ordinal);

            Assert.True(rowIndex > previousRowIndex,
                $"README.md is missing the ordered donation row: {row}");
            previousRowIndex = rowIndex;
        }
    }
}
