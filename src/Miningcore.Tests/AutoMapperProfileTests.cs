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
}
