using System.Collections.Concurrent;
using Autofac;
using AutoMapper;
using Miningcore.Api;
using Miningcore.Api.Controllers;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Api.Controllers;

public class AdminApiPaymentProcessingTests
{
    [Fact]
    public void BulkEnable_RejectsPpsWithoutClusterSchedulerBeforeMutatingAnyPool()
    {
        var pps = Pool("pps", PayoutScheme.PPS, false);
        var ordinary = Pool("ordinary", PayoutScheme.PPLNS, false);
        var controller = CreateController(false, pps, ordinary);

        var error = Assert.Throws<ApiException>(() =>
            controller.EnablePoolsPaymentProcessing());

        Assert.Equal(409, error.ResponseStatusCode);
        Assert.Contains("cluster-level payment processing was not active at startup",
            error.Message);
        Assert.False(pps.PaymentProcessing.Enabled);
        Assert.False(ordinary.PaymentProcessing.Enabled);
    }

    [Fact]
    public void BulkDisable_RejectsActivePpsBeforeMutatingAnyPool()
    {
        var pps = Pool("pps", PayoutScheme.PPS, true);
        var ordinary = Pool("ordinary", PayoutScheme.PPLNS, true);
        var controller = CreateController(true, pps, ordinary);

        var error = Assert.Throws<ApiException>(() =>
            controller.DisablePoolsPaymentProcessing());

        Assert.Equal(409, error.ResponseStatusCode);
        Assert.Contains("Cannot disable payment processing while PPS pool 'pps'",
            error.Message);
        Assert.Contains("Non-PPS pools remain individually controllable", error.Message);
        Assert.True(pps.PaymentProcessing.Enabled);
        Assert.True(ordinary.PaymentProcessing.Enabled);
    }

    [Fact]
    public void OrdinaryPoolToggle_RemainsAvailableWithoutClusterScheduler()
    {
        var ordinary = Pool("ordinary", PayoutScheme.PPLNS, false);
        var controller = CreateController(false, ordinary);

        var result = controller.EnablePoolPaymentProcessing(ordinary.Id);

        Assert.Equal("Ok", result.Value);
        Assert.True(ordinary.PaymentProcessing.Enabled);
    }

    private static PoolConfig Pool(string id, PayoutScheme scheme, bool enabled) =>
        new()
        {
            Id = id,
            Enabled = true,
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = enabled,
                PayoutScheme = scheme,
            },
        };

    private static AdminApiController CreateController(bool clusterPaymentEnabled,
        params PoolConfig[] poolConfigs)
    {
        var clusterConfig = new ClusterConfig
        {
            PaymentProcessing = new ClusterPaymentProcessingConfig
            {
                Enabled = clusterPaymentEnabled,
            },
            Pools = poolConfigs,
        };
        var pools = new ConcurrentDictionary<string, IMiningPool>();
        foreach(var config in poolConfigs)
        {
            var pool = Substitute.For<IMiningPool>();
            pool.Config.Returns(config);
            Assert.True(pools.TryAdd(config.Id, pool));
        }

        var builder = new ContainerBuilder();
        builder.RegisterInstance(Substitute.For<IMapper>());
        builder.RegisterInstance(clusterConfig);
        builder.RegisterInstance(Substitute.For<IConnectionFactory>());
        builder.RegisterInstance(new global::Miningcore.Api.Responses.AdminGcStats());
        builder.RegisterInstance(Substitute.For<IMinerRepository>());
        builder.RegisterInstance(Substitute.For<IPaymentRepository>());
        builder.RegisterInstance(Substitute.For<IBalanceRepository>());
        builder.RegisterInstance(pools);
        var container = builder.Build();

        return new AdminApiController(container);
    }
}
