using System.Collections.Generic;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Equihash;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Rpc;
using Miningcore.Tests.Util;
using Miningcore.Time;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinJobManagerBaseTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void EquihashConfigure_PreservesLegacyOverrideWithoutTemplateCast(
        bool? configuredOverride, bool expected)
    {
        using var container = BuildContainer();
        var manager = new TestEquihashJobManager(container,
            MockMasterClock.FromTicks(638010200200475015),
            new MessageBus(), Substitute.For<IExtraNonceProvider>());
        var extra = configuredOverride.HasValue
            ? new Dictionary<string, object>
            {
                ["hasLegacyDaemon"] = configuredOverride.Value,
            }
            : null;
        var pool = new PoolConfig
        {
            Id = "equihash-test",
            Template = new EquihashCoinTemplate(),
            Daemons = new[]
            {
                new DaemonEndpointConfig
                {
                    Host = "127.0.0.1",
                    Port = 8232,
                },
            },
            Extra = extra,
        };

        manager.Configure(pool, new ClusterConfig());

        Assert.Equal(expected, manager.LegacyDaemonEnabled);
    }

    [Fact]
    public void LegacyConnectionFailure_DoesNotDereferenceMissingResponse()
    {
        var response = new RpcResponse<DaemonInfo>(null,
            new JsonRpcError(-1, "getinfo unavailable", null));

        var connected = BitcoinJobManagerBase<BitcoinJob>
            .TryGetLegacyDaemonConnection(response, out var version);

        Assert.False(connected);
        Assert.Null(version);
    }

    [Fact]
    public void LegacyConnectionSuccess_ReturnsVersion()
    {
        var response = new RpcResponse<DaemonInfo>(new DaemonInfo
        {
            Connections = 1,
            Version = "v1.2.3",
        });

        var connected = BitcoinJobManagerBase<BitcoinJob>
            .TryGetLegacyDaemonConnection(response, out var version);

        Assert.True(connected);
        Assert.Equal("v1.2.3", version);
    }

    private static IContainer BuildContainer()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());

        return builder.Build();
    }

    private sealed class TestEquihashJobManager : EquihashJobManager
    {
        public TestEquihashJobManager(IComponentContext ctx,
            IMasterClock clock, IMessageBus messageBus,
            IExtraNonceProvider extraNonceProvider) :
            base(ctx, clock, messageBus, extraNonceProvider)
        {
        }

        public bool LegacyDaemonEnabled => hasLegacyDaemon;
    }
}
