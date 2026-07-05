using System.Collections.Generic;
using Miningcore.Configuration;
using Miningcore.Mining;
using Xunit;

namespace Miningcore.Tests;

public class ProgramPoolTemplateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AssignPoolTemplates_IsIndependentOfParentAuxiliaryOrder(bool parentFirst)
    {
        var litecoin = new BitcoinTemplate { Symbol = "LTC", Family = CoinFamily.Bitcoin };
        var dogecoin = new BitcoinTemplate { Symbol = "DOGE", Family = CoinFamily.Bitcoin };
        var parent = new PoolConfig { Id = "ltc-solo", Coin = "litecoin", Enabled = true };
        var auxiliary = new PoolConfig { Id = "doge-solo", Coin = "dogecoin", Enabled = true };
        var pools = parentFirst
            ? new[] { parent, auxiliary }
            : new[] { auxiliary, parent };
        var templates = new Dictionary<string, CoinTemplate>
        {
            ["litecoin"] = litecoin,
            ["dogecoin"] = dogecoin,
        };

        Program.AssignPoolTemplates(pools, templates);

        Assert.Same(litecoin, parent.Template);
        Assert.Same(dogecoin, auxiliary.Template);
    }

    [Fact]
    public void AssignPoolTemplates_RejectsUndefinedCoinBeforePoolStartup()
    {
        var pool = new PoolConfig { Id = "missing", Coin = "undefined", Enabled = true };

        var ex = Assert.Throws<PoolStartupException>(() =>
            Program.AssignPoolTemplates(new[] { pool },
                new Dictionary<string, CoinTemplate>()));

        Assert.Equal(pool.Id, ex.PoolId);
    }
}
