using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public void ToPoolInfo_UsesDedicatedBanningDtoWithoutAliasingConfiguration()
    {
        var source = new PoolShareBasedBanningConfig
        {
            Enabled = true,
            CheckThreshold = 25,
            InvalidPercent = 12.5,
            Time = 600,
            MinerEffortPercent = 250.25,
            MinerEffortTime = 900,
        };
        var config = CreateMinimalPoolConfig();
        config.Banning = source;
        var mapper = AutoMapperFactory.CreateMapper();

        mapper.ConfigurationProvider.AssertConfigurationIsValid();
        var result = config.ToPoolInfo(mapper,
            new global::Miningcore.Persistence.Model.PoolStats(), null);

        var banning = Assert.IsType<ApiPoolShareBasedBanningConfig>(
            result.ShareBasedBanning);
        Assert.NotSame(source, banning);
        Assert.True(banning.Enabled);
        Assert.Equal(25, banning.CheckThreshold);
        Assert.Equal(12.5, banning.InvalidPercent);
        Assert.Equal(600, banning.Time);
        Assert.Equal(250.25, banning.MinerEffortPercent);
        Assert.Equal(900, banning.MinerEffortTime);

        source.Enabled = false;
        source.CheckThreshold = 1;
        source.InvalidPercent = 2;
        source.Time = 3;
        source.MinerEffortPercent = null;
        source.MinerEffortTime = null;

        Assert.True(banning.Enabled);
        Assert.Equal(25, banning.CheckThreshold);
        Assert.Equal(12.5, banning.InvalidPercent);
        Assert.Equal(600, banning.Time);
        Assert.Equal(250.25, banning.MinerEffortPercent);
        Assert.Equal(900, banning.MinerEffortTime);
    }

    [Fact]
    public void BanningDto_ExposesExactlyTheExistingSixFieldContract()
    {
        var properties = typeof(ApiPoolShareBasedBanningConfig)
            .GetProperties()
            .OrderBy(property => property.Name)
            .Select(property => (property.Name, property.PropertyType))
            .ToArray();

        Assert.Equal(new[]
        {
            ("CheckThreshold", typeof(int)),
            ("Enabled", typeof(bool)),
            ("InvalidPercent", typeof(double)),
            ("MinerEffortPercent", typeof(double?)),
            ("MinerEffortTime", typeof(int?)),
            ("Time", typeof(int)),
        }, properties);
    }

    [Fact]
    public void PoolResponses_PreserveBanningPropertyNamesAndValues()
    {
        var banning = new ApiPoolShareBasedBanningConfig
        {
            Enabled = true,
            CheckThreshold = 25,
            InvalidPercent = 12.5,
            Time = 600,
            MinerEffortPercent = 250.25,
            MinerEffortTime = 900,
        };
        var options = CreateApiJsonOptions(false);

        var pools = JsonSerializer.SerializeToElement(new GetPoolsResponse
        {
            Pools = new[] { new PoolInfo { ShareBasedBanning = banning } },
        }, options);
        var pool = JsonSerializer.SerializeToElement(new GetPoolResponse
        {
            Pool = new PoolInfo { ShareBasedBanning = banning },
        }, options);

        AssertBanningContract(
            pools.GetProperty("pools")[0].GetProperty("shareBasedBanning"));
        AssertBanningContract(
            pool.GetProperty("pool").GetProperty("shareBasedBanning"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PoolResponses_PreserveNullableBanningFields(bool legacyNulls)
    {
        var banning = new ApiPoolShareBasedBanningConfig
        {
            Enabled = true,
            CheckThreshold = 25,
            InvalidPercent = 12.5,
            Time = 600,
        };
        var options = CreateApiJsonOptions(legacyNulls);
        var payloads = new[]
        {
            JsonSerializer.SerializeToElement(new GetPoolsResponse
            {
                Pools = new[]
                {
                    new PoolInfo { ShareBasedBanning = banning },
                },
            }, options).GetProperty("pools")[0],
            JsonSerializer.SerializeToElement(new GetPoolResponse
            {
                Pool = new PoolInfo { ShareBasedBanning = banning },
            }, options).GetProperty("pool"),
        };

        foreach(var payload in payloads)
        {
            var publicBanning = payload.GetProperty("shareBasedBanning");

            Assert.Equal(legacyNulls,
                publicBanning.TryGetProperty("minerEffortPercent",
                    out var effortPercent));
            Assert.Equal(legacyNulls,
                publicBanning.TryGetProperty("minerEffortTime",
                    out var effortTime));

            if(legacyNulls)
            {
                Assert.Equal(JsonValueKind.Null, effortPercent.ValueKind);
                Assert.Equal(JsonValueKind.Null, effortTime.ValueKind);
            }
        }
    }

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
        Assert.Equal(sourceEndpoint.VarDiff.MinDiff, endpoint.VarDiff.MinDiff);
        Assert.Equal(sourceEndpoint.VarDiff.MaxDiff, endpoint.VarDiff.MaxDiff);
        Assert.Equal(sourceEndpoint.VarDiff.MaxDelta, endpoint.VarDiff.MaxDelta);
        Assert.Equal(sourceEndpoint.VarDiff.TargetTime,
            endpoint.VarDiff.TargetTime);
        Assert.Equal(sourceEndpoint.VarDiff.RetargetTime,
            endpoint.VarDiff.RetargetTime);
        Assert.Equal(sourceEndpoint.VarDiff.VariancePercent,
            endpoint.VarDiff.VariancePercent);
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

    private static PoolConfig CreateMinimalPoolConfig() => new()
    {
        Template = new AlephiumCoinTemplate
        {
            Family = CoinFamily.Alephium,
            Name = "Alephium",
            Symbol = "ALPH",
        },
        PaymentProcessing = new PoolPaymentProcessingConfig(),
    };

    private static JsonSerializerOptions CreateApiJsonOptions(
        bool legacyNulls) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = legacyNulls
            ? JsonIgnoreCondition.Never
            : JsonIgnoreCondition.WhenWritingNull,
    };

    private static void AssertBanningContract(JsonElement banning)
    {
        Assert.Equal(new[]
        {
            "checkThreshold",
            "enabled",
            "invalidPercent",
            "minerEffortPercent",
            "minerEffortTime",
            "time",
        }, banning.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray());
        Assert.True(banning.GetProperty("enabled").GetBoolean());
        Assert.Equal(25, banning.GetProperty("checkThreshold").GetInt32());
        Assert.Equal(12.5,
            banning.GetProperty("invalidPercent").GetDouble());
        Assert.Equal(600, banning.GetProperty("time").GetInt32());
        Assert.Equal(250.25,
            banning.GetProperty("minerEffortPercent").GetDouble());
        Assert.Equal(900,
            banning.GetProperty("minerEffortTime").GetInt32());
    }
}
