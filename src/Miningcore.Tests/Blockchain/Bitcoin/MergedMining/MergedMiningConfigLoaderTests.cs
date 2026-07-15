using System.Collections.Generic;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Configuration;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class MergedMiningConfigLoaderTests
{
    [Theory]
    [InlineData("doge", "doge")]
    [InlineData(" doge ", "doge")]
    [InlineData(" ", "doge")]
    [InlineData(null, "doge")]
    public void GetNormalizedConfig_NormalizesAddressParameter(string configured, string expected)
    {
        var pool = new PoolConfig
        {
            Extra = new Dictionary<string, object>
            {
                ["mergedMining"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["addressParameter"] = configured,
                },
            },
        };

        var result = MergedMiningConfigLoader.GetNormalizedConfig(pool);

        Assert.NotNull(result);
        Assert.True(result.Enabled);
        Assert.Equal(expected, result.AddressParameter);
    }
}
