using Xunit;
using Miningcore.Api.Extensions;
using Miningcore.Configuration;
using Miningcore.Mining;
using NSubstitute;
using System.Text.Json;

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
    [InlineData("starting")]
    [InlineData("online")]
    [InlineData("draining")]
    [InlineData("stopping")]
    [InlineData("faulted")]
    public void IsolatedPoolState_IsPublicWithoutChangingOrdinaryPoolResponses(string state)
    {
        var config = new PoolConfig { Id = "test", Template = new BitcoinTemplate() };
        var pool = Substitute.For<IMiningPool, IIsolatedMiningPool>();
        ((IIsolatedMiningPool) pool).MiningState.Returns(state);
        var mapper = AutoMapperFactory.CreateMapper();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var isolated = JsonDocument.Parse(JsonSerializer.Serialize(config.ToPoolInfo(mapper, null, pool), options));
        Assert.Equal(state, isolated.RootElement.GetProperty("miningState").GetString());
        using var ordinary = JsonDocument.Parse(JsonSerializer.Serialize(config.ToPoolInfo(mapper, null, null), options));
        Assert.False(ordinary.RootElement.TryGetProperty("miningState", out _));
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
