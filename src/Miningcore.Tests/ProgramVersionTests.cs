using Xunit;

namespace Miningcore.Tests;

public class ProgramVersionTests
{
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
