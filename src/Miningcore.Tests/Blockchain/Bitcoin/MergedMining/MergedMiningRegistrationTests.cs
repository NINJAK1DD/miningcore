using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Autofac;
using Autofac.Features.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class MergedMiningRegistrationTests : TestBase
{
    [Theory]
    [InlineData("bitcoin")]
    [InlineData("litecoin")]
    [InlineData("dogecoin")]
    [InlineData("dash")]
    public void BitcoinFamilyPool_ConfiguresNormallyWithoutMergedMining(string coin)
    {
        using var scope = container.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(Substitute.For<IConnectionFactory>());
            builder.RegisterInstance(Substitute.For<IStatsRepository>());
            builder.RegisterInstance(Substitute.For<IBlockRepository>());
            builder.RegisterInstance(Substitute.For<IShareRepository>());
            builder.RegisterInstance(Substitute.For<IHttpClientFactory>());
            builder.RegisterInstance(Substitute.For<IMemoryCache>());
            builder.RegisterInstance(new NicehashService(
                Substitute.For<IHttpClientFactory>(), Substitute.For<IMemoryCache>()));
        });
        var registration = Assert.Single(scope
            .Resolve<IEnumerable<Meta<Lazy<IMiningPool, CoinFamilyAttribute>>>>()
            .Where(x => x.Value.Metadata.SupportedFamilies.Contains(CoinFamily.Bitcoin)));
        var pool = Assert.IsType<MergedMiningBitcoinPool>(registration.Value.Value);
        var template = ModuleInitializer.CoinTemplates[coin];
        var config = new PoolConfig
        {
            Id = $"{coin}-test",
            Coin = coin,
            Template = template,
        };

        pool.Configure(config, new ClusterConfig());

        Assert.Same(template, pool.Config.Template);
    }
}
