using System;
using System.Collections.Generic;
using System.Text.Json;
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
    public void ToPoolInfo_UsesDedicatedEndpointDtosAndOmitsPrivateListenerData()
    {
        var sourceEndpoint = new PoolEndpoint
        {
            ListenAddress = "127.0.0.1",
            Name = "public-endpoint",
            Difficulty = 42,
            VarDiff = new VarDiffConfig
            {
                MinDiff = 1,
                MaxDiff = 100,
                MaxDelta = 10,
                TargetTime = 15,
                RetargetTime = 90,
                VariancePercent = 30,
            },
            TcpProxyProtocol = new TcpProxyProtocolConfig
            {
                Enable = true,
                Mandatory = true,
                ProxyAddresses = new[] { "10.0.0.5" },
            },
            Tls = true,
            TlsAuto = true,
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

        var endpoint = Assert.IsType<ApiPoolEndpoint>(
            Assert.Single(result.Ports).Value);
        Assert.Equal(sourceEndpoint.ListenAddress, endpoint.ListenAddress);
        Assert.Equal(sourceEndpoint.Name, endpoint.Name);
        Assert.Equal(sourceEndpoint.Difficulty, endpoint.Difficulty);
        Assert.NotSame(sourceEndpoint.VarDiff, endpoint.VarDiff);
        Assert.Equal(sourceEndpoint.VarDiff.MinDiff, endpoint.VarDiff.MinDiff);
        Assert.Equal(sourceEndpoint.VarDiff.MaxDiff, endpoint.VarDiff.MaxDiff);
        Assert.Equal(sourceEndpoint.VarDiff.MaxDelta, endpoint.VarDiff.MaxDelta);
        Assert.Equal(sourceEndpoint.VarDiff.TargetTime,
            endpoint.VarDiff.TargetTime);
        Assert.Equal(sourceEndpoint.VarDiff.RetargetTime,
            endpoint.VarDiff.RetargetTime);
        Assert.Equal(sourceEndpoint.VarDiff.VariancePercent,
            endpoint.VarDiff.VariancePercent);
        Assert.NotSame(sourceEndpoint.TcpProxyProtocol,
            endpoint.TcpProxyProtocol);
        Assert.True(endpoint.TcpProxyProtocol.Enable);
        Assert.True(endpoint.TcpProxyProtocol.Mandatory);
        Assert.True(endpoint.Tls);
        Assert.True(endpoint.TlsAuto);

        var json = JsonSerializer.Serialize(endpoint,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        using var document = JsonDocument.Parse(json);
        var publicEndpoint = document.RootElement;
        Assert.False(publicEndpoint.TryGetProperty("tlsPfxFile", out _));
        Assert.False(publicEndpoint.TryGetProperty("tlsPfxPassword", out _));
        Assert.True(publicEndpoint.TryGetProperty("tcpProxyProtocol",
            out var publicProxyProtocol));
        Assert.False(publicProxyProtocol.TryGetProperty("proxyAddresses",
            out _));

        Assert.False(result.Ports.ContainsKey(3032));
        Assert.Equal(2, config.Ports.Count);
        Assert.Null(config.Ports[3032]);
        Assert.Equal("pool.pfx", sourceEndpoint.TlsPfxFile);
        Assert.Equal("secret", sourceEndpoint.TlsPfxPassword);
        Assert.Equal(new[] { "10.0.0.5" },
            sourceEndpoint.TcpProxyProtocol.ProxyAddresses);
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
