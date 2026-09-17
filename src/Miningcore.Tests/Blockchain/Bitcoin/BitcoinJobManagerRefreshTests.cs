using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Rpc;
using Miningcore.Time;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinJobManagerRefreshTests : TestBase
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedForcedRefresh_OnlyRebroadcastsAnExistingBitcoinJob(bool existing, bool throws)
    {
        var manager = new RefreshManager(container, throws);
        manager.Configure(new PoolConfig
        {
            Id = "bitcoin-refresh", Coin = "bitcoin", Template = ModuleInitializer.CoinTemplates["bitcoin"],
            Extra = new Dictionary<string, object> { ["soloCoinbasePayout"] = false },
            Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 1 } },
        }, new ClusterConfig());
        var previous = existing ? new BitcoinJob() : null;
        manager.Seed(previous);
        var result = await manager.Refresh();
        Assert.False(result.IsNew);
        Assert.Equal(existing, result.Force);
        Assert.Same(previous, manager.GetJobForStratum());
    }

    private sealed class RefreshManager(IComponentContext ctx, bool throws) : BitcoinJobManager(
        ctx, Substitute.For<IMasterClock>(), Substitute.For<IMessageBus>(), new BitcoinExtraNonceProvider("bitcoin-refresh", null))
    {
        internal void Seed(BitcoinJob job) => currentJob = job;
        internal Task<(bool IsNew, bool Force)> Refresh() => UpdateJob(CancellationToken.None, true);
        protected override Task<RpcResponse<BlockTemplate>> GetBlockTemplateAsync(CancellationToken ct) =>
            throws ? throw new HttpRequestException("test transport unavailable") :
                Task.FromResult(new RpcResponse<BlockTemplate>(null, new JsonRpcError(-500, "test daemon unavailable", null)));
    }
}
