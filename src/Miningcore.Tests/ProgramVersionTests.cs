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
}
