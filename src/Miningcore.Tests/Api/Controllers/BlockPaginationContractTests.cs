using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Miningcore.Api.Controllers;
using Xunit;

namespace Miningcore.Tests.Api.Controllers;

public class BlockPaginationContractTests
{
    [Theory]
    [InlineData(typeof(ClusterApiController), "PageBlocksPagedAsync")]
    [InlineData(typeof(PoolApiController), "PagePoolBlocksAsync")]
    [InlineData(typeof(PoolApiController), "PagePoolBlocksV2Async")]
    [InlineData(typeof(PoolApiController), "PageMinerBlocksAsync")]
    [InlineData(typeof(PoolApiController), "PageMinerBlocksV2Async")]
    public void PublicBlockEndpoints_BoundPageAndPageSize(Type controllerType,
        string methodName)
    {
        var method = Assert.Single(controllerType.GetMethods(), x =>
            x.Name == methodName);
        var parameters = method.GetParameters();

        AssertRange(parameters.Single(x => x.Name == "page"), 0,
            int.MaxValue);
        AssertRange(parameters.Single(x => x.Name == "pageSize"), 1, 100);
    }

    private static void AssertRange(System.Reflection.ParameterInfo parameter,
        int minimum, int maximum)
    {
        var range = Assert.Single(parameter
            .GetCustomAttributes(typeof(RangeAttribute), false)
            .Cast<RangeAttribute>());

        Assert.Equal(minimum, Convert.ToInt32(range.Minimum));
        Assert.Equal(maximum, Convert.ToInt32(range.Maximum));
    }
}
