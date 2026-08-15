using System;
using System.Collections.Generic;
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
        var sourceEndpoint = new PoolEndpoint
        {
            ListenAddress = "127.0.0.1",
            Name = "public-endpoint",
            Difficulty = 42,
            Tls = true,
            TlsPfxFile = "pool.pfx",
            TlsPfxPassword = "secret",
        };
        var config = new PoolConfig
        {
            Template = new AlephiumCoinTemplate
            {
                Family = CoinFamily.Alephium,
                Name = "Alephium",
                Symbol = "ALPH",
            },
            PaymentProcessing = new PoolPaymentProcessingConfig(),
            Ports = new Dictionary<int, PoolEndpoint>
            {
                [3031] = sourceEndpoint,
                [3032] = null,
            },
        };
        var mapper = AutoMapperFactory.CreateMapper();

        var result = config.ToPoolInfo(mapper,
            new global::Miningcore.Persistence.Model.PoolStats(), null);

        var endpoint = Assert.Single(result.Ports).Value;
        Assert.NotNull(endpoint);
        Assert.NotSame(config.Ports, result.Ports);
        Assert.NotSame(sourceEndpoint, endpoint);
        Assert.Equal(sourceEndpoint.ListenAddress, endpoint.ListenAddress);
        Assert.Equal(sourceEndpoint.Name, endpoint.Name);
        Assert.Equal(sourceEndpoint.Difficulty, endpoint.Difficulty);
        Assert.True(endpoint.Tls);
        Assert.Null(endpoint.TlsPfxFile);
        Assert.Null(endpoint.TlsPfxPassword);
        Assert.False(result.Ports.ContainsKey(3032));
        Assert.Equal(2, config.Ports.Count);
        Assert.Null(config.Ports[3032]);
        Assert.Equal("pool.pfx", sourceEndpoint.TlsPfxFile);
        Assert.Equal("secret", sourceEndpoint.TlsPfxPassword);
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
