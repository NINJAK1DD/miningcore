using Xunit;

namespace Miningcore.Tests;

public class AutoMapperProfileTests
{
    [Fact]
    public void Configuration_IsValid()
    {
        var mapper = AutoMapperFactory.CreateMapper();

        mapper.ConfigurationProvider.AssertConfigurationIsValid();
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("not-json")]
    public void DirectRecipientApiProjection_FailsClosedPerRow(string value)
    {
        Assert.Empty(AutoMapperProfile.DeserializeDirectRecipientOutputs(
            value));
    }
}
