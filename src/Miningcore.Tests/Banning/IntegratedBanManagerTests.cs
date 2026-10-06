using System;
using System.Net;
using System.Threading;
using Autofac;
using Miningcore.Banning;
using Miningcore.Configuration;
using Xunit;

namespace Miningcore.Tests.Banning;

public class IntegratedBanManagerTests : TestBase
{
    private static readonly IPAddress address = IPAddress.Parse("192.168.1.1");

    [Fact]
    public void Ban_Valid_Address()
    {
        var manager = ModuleInitializer.Container.ResolveKeyed<IBanManager>(BanManagerKind.Integrated);

        Assert.False(manager.IsBanned(address));
        manager.Ban(address, TimeSpan.FromSeconds(1));
        Assert.True(manager.IsBanned(address));

        // let it expire
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Assert.False(manager.IsBanned(address));
    }

    [Fact]
    public void Throw_Invalid_Duration()
    {
        var manager = ModuleInitializer.Container.ResolveKeyed<IBanManager>(BanManagerKind.Integrated);

        Assert.ThrowsAny<ArgumentException>(() => manager.Ban(address, TimeSpan.Zero));
    }

    [Fact]
    public void Dont_Ban_Loopback()
    {
        var manager = ModuleInitializer.Container.ResolveKeyed<IBanManager>(BanManagerKind.Integrated);

        manager.Ban(IPAddress.Loopback, TimeSpan.FromSeconds(1));
        Assert.False(manager.IsBanned(address));

        manager.Ban(IPAddress.IPv6Loopback, TimeSpan.FromSeconds(1));
        Assert.False(manager.IsBanned(address));

        manager.Ban(IPAddress.Loopback.MapToIPv6(), TimeSpan.FromSeconds(1));
        Assert.False(manager.IsBanned(IPAddress.Loopback));
        Assert.False(manager.IsBanned(IPAddress.Loopback.MapToIPv6()));
    }

    [Fact]
    public void Ban_Normalizes_Mapped_Addresses_On_Read_And_Write()
    {
        var manager = ModuleInitializer.Container.ResolveKeyed<IBanManager>(BanManagerKind.Integrated);
        var mappedWrite = IPAddress.Parse("203.0.113.244");
        var plainWrite = IPAddress.Parse("203.0.113.245");

        manager.Ban(mappedWrite.MapToIPv6(), TimeSpan.FromSeconds(10));
        Assert.True(manager.IsBanned(mappedWrite));
        Assert.True(manager.IsBanned(mappedWrite.MapToIPv6()));

        manager.Ban(plainWrite, TimeSpan.FromSeconds(10));
        Assert.True(manager.IsBanned(plainWrite.MapToIPv6()));
    }
}
