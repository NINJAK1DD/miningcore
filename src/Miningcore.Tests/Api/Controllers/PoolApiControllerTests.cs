using System;
using System.Collections.Generic;
using AutoMapper;
using Miningcore.Api.Controllers;
using Miningcore.Api.Extensions;
using Miningcore.Api.Responses;
using Miningcore.Configuration;
using Miningcore.Mining;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Api.Controllers;

public class PoolApiControllerTests
{
    [Fact]
    public void ToPoolInfo_OmitsNullEndpointsAndStripsTlsSecrets()
    {
        var mapped = new PoolInfo
        {
            Coin = new ApiCoinConfig(),
            PaymentProcessing = new ApiPoolPaymentProcessingConfig(),
            Ports = new Dictionary<int, PoolEndpoint>
            {
                [3031] = new()
                {
                    TlsPfxFile = "pool.pfx",
                    TlsPfxPassword = "secret",
                },
                [3032] = null,
            },
        };
        var config = new PoolConfig
        {
            Template = Substitute.For<CoinTemplate>(),
        };
        var mapper = Substitute.For<IMapper>();
        mapper.Map<PoolInfo>(config).Returns(mapped);

        var result = config.ToPoolInfo(mapper,
            new global::Miningcore.Persistence.Model.PoolStats(), null);

        Assert.Same(mapped, result);
        var endpoint = Assert.Single(result.Ports).Value;
        Assert.NotNull(endpoint);
        Assert.Null(endpoint.TlsPfxFile);
        Assert.Null(endpoint.TlsPfxPassword);
        Assert.False(result.Ports.ContainsKey(3032));
    }

    [Fact]
    public void ConfigurePayoutSchemeConfig_WithSoloAndNoSchemeConfig_IsNullSafe()
    {
        var poolInfo = new PoolInfo
        {
            PaymentProcessing = new ApiPoolPaymentProcessingConfig()
        };
        var payoutConfig = new PoolPaymentProcessingConfig
        {
            Enabled = true,
            PayoutScheme = PayoutScheme.SOLO
        };

        PoolApiController.ConfigurePayoutSchemeConfig(poolInfo, payoutConfig);

        Assert.NotNull(poolInfo.PaymentProcessing.PayoutSchemeConfig);
        Assert.Null(poolInfo.PaymentProcessing.PayoutSchemeConfig.BlockFinderPercentage);
    }

    [Fact]
    public void ConfigurePayoutSchemeConfig_WithMissingMappedPaymentConfig_IsNullSafe()
    {
        var poolInfo = new PoolInfo();

        PoolApiController.ConfigurePayoutSchemeConfig(poolInfo, null);

        Assert.NotNull(poolInfo.PaymentProcessing);
        Assert.NotNull(poolInfo.PaymentProcessing.PayoutSchemeConfig);
        Assert.Null(poolInfo.PaymentProcessing.PayoutSchemeConfig.BlockFinderPercentage);
    }

    [Fact]
    public void ShouldCalculatePoolEffort_WithLastBlockButNoRuntimePool_ReturnsFalse()
    {
        Assert.False(PoolApiController.ShouldCalculatePoolEffort(DateTime.UtcNow, null));
    }

    [Fact]
    public void ShouldCalculatePoolEffort_WithRuntimePoolButNoLastBlock_ReturnsFalse()
    {
        Assert.False(PoolApiController.ShouldCalculatePoolEffort(null,
            Substitute.For<IMiningPool>()));
    }

    [Fact]
    public void ShouldCalculatePoolEffort_WithRuntimePoolAndLastBlock_ReturnsTrue()
    {
        Assert.True(PoolApiController.ShouldCalculatePoolEffort(DateTime.UtcNow,
            Substitute.For<IMiningPool>()));
    }
}
