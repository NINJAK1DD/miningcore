using System.Collections.Concurrent;
using System.Collections.Generic;
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

    [Fact]
    public void BulkDisable_RejectsActiveDirectSoloBeforeMutatingAnyPool()
    {
        var direct = Pool("direct", PayoutScheme.SOLO, true,
            directCoinbase: true);
        var ordinary = Pool("ordinary", PayoutScheme.PPLNS, true);
        var controller = CreateController(true, direct, ordinary);

        var error = Assert.Throws<ApiException>(() =>
            controller.DisablePoolsPaymentProcessing());

        Assert.Equal(409, error.ResponseStatusCode);
        Assert.Contains(
            "Cannot disable payment processing while direct-SOLO pool 'direct'",
            error.Message);
        Assert.True(direct.PaymentProcessing.Enabled);
        Assert.True(ordinary.PaymentProcessing.Enabled);
    }

    [Fact]
    public void BulkEnable_RejectsDirectSoloWithoutStartupScheduler()
    {
        var direct = Pool("direct", PayoutScheme.SOLO, false,
            directCoinbase: true);
        var ordinary = Pool("ordinary", PayoutScheme.PPLNS, false);
        var controller = CreateController(false, direct, ordinary);

        var error = Assert.Throws<ApiException>(() =>
            controller.EnablePoolsPaymentProcessing());

        Assert.Equal(409, error.ResponseStatusCode);
        Assert.Contains("valid direct-SOLO configuration", error.Message);
        Assert.False(direct.PaymentProcessing.Enabled);
        Assert.False(ordinary.PaymentProcessing.Enabled);
    }

    [Fact]
    public void BulkDisable_ProtectsDefaultBitcoinDirectSolo()
    {
        var direct = Pool("direct-default", PayoutScheme.SOLO, true);
        direct.Coin = "bitcoin";
        var ordinary = Pool("ordinary", PayoutScheme.PPLNS, true);
        var controller = CreateController(true, direct, ordinary);

        var error = Assert.Throws<ApiException>(() =>
            controller.DisablePoolsPaymentProcessing());

        Assert.Equal(409, error.ResponseStatusCode);
        Assert.Contains(
            "Cannot disable payment processing while direct-SOLO pool 'direct-default'",
            error.Message);
        Assert.True(direct.PaymentProcessing.Enabled);
        Assert.True(ordinary.PaymentProcessing.Enabled);
    }

    [Fact]
    public void ExplicitFalseBitcoinSolo_RemainsAdministrativelyControllable()
    {
        var custodial = Pool("custodial", PayoutScheme.SOLO, true);
        custodial.Coin = "bitcoin";
        custodial.Extra = new Dictionary<string, object>
        {
            ["soloCoinbasePayout"] = false,
        };
        var controller = CreateController(true, custodial);

        var result = controller.DisablePoolPaymentProcessing(custodial.Id);

        Assert.Equal("Ok", result.Value);
        Assert.False(custodial.PaymentProcessing.Enabled);
    }

    private static PoolConfig Pool(string id, PayoutScheme scheme, bool enabled,
        bool directCoinbase = false)
    {
        var result = new PoolConfig
        {
            Id = id,
            Enabled = true,
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = enabled,
                PayoutScheme = scheme,
            },
        };

        if(directCoinbase)
            result.Extra = new Dictionary<string, object>
            {
                ["soloCoinbasePayout"] = true,
            };

        return result;
    }

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
