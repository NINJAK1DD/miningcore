using System;
using System.Collections.Generic;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Miningcore.Configuration;
using Xunit;

namespace Miningcore.Tests;

public class ApiListenerConfigurationTests
{
    [Fact]
    public void OmittedDedicatedPorts_PreserveSharedListenerCompatibility()
    {
        var ports = Program.ResolveApiEndpointPorts(new ApiConfig
        {
            Port = 5000,
        });

        Assert.Equal(5000, ports.PublicPort);
        Assert.Equal(5000, ports.AdminPort);
        Assert.Equal(5000, ports.MetricsPort);
        Assert.Equal(new[] { 5000 }, ports.ListenerPorts);
        Assert.True(Program.IsApiRequestAllowed(5000,
            new PathString("/api/admin/logging/level/info"), ports));
        Assert.True(Program.IsApiRequestAllowed(5000,
            new PathString("/metrics"), ports));
    }

    [Fact]
    public void MissingApiSection_UsesLegacyDefaultListener()
    {
        var ports = Program.ResolveApiEndpointPorts(null);

        Assert.Equal(Program.DefaultApiPort, ports.PublicPort);
        Assert.Equal(new[] { Program.DefaultApiPort }, ports.ListenerPorts);
    }

    [Theory]
    [InlineData(5000, "/api/pools", true)]
    [InlineData(5000, "/notifications", true)]
    [InlineData(5000, "/api/admin/logging/level/info", false)]
    [InlineData(5000, "/metrics", false)]
    [InlineData(5001, "/api/admin/logging/level/info", true)]
    [InlineData(5001, "/api/pools", false)]
    [InlineData(5001, "/metrics", false)]
    [InlineData(5002, "/metrics", true)]
    [InlineData(5002, "/api/pools", false)]
    [InlineData(5002, "/api/admin/logging/level/info", false)]
    public void DedicatedPorts_IsolateRouteFamilies(int port, string path,
        bool expected)
    {
        var ports = Program.ResolveApiEndpointPorts(new ApiConfig
        {
            Port = 5000,
            AdminPort = 5001,
            MetricsPort = 5002,
        });

        Assert.Equal(expected, Program.IsApiRequestAllowed(port,
            new PathString(path), ports));
    }

    [Fact]
    public void DedicatedPorts_CreateThreeListeners()
    {
        var ports = Program.ResolveApiEndpointPorts(new ApiConfig
        {
            Port = 5000,
            AdminPort = 5001,
            MetricsPort = 5002,
        });

        Assert.Equal(new[] { 5000, 5001, 5002 }, ports.ListenerPorts);
    }

    [Fact]
    public void OneOmittedDedicatedPort_FallsBackIndependently()
    {
        var ports = Program.ResolveApiEndpointPorts(new ApiConfig
        {
            Port = 5000,
            MetricsPort = 5002,
        });

        Assert.Equal(5000, ports.AdminPort);
        Assert.Equal(5002, ports.MetricsPort);
        Assert.Equal(new[] { 5000, 5002 }, ports.ListenerPorts);
        Assert.True(Program.IsApiRequestAllowed(5000,
            new PathString("/api/admin/stats/gc"), ports));
        Assert.False(Program.IsApiRequestAllowed(5000,
            new PathString("/metrics"), ports));
    }

    [Theory]
    [InlineData("/api/administrator")]
    [InlineData("/metrics-export")]
    public void SimilarPublicPathNames_AreNotMisclassified(string path)
    {
        var ports = Program.ResolveApiEndpointPorts(new ApiConfig
        {
            Port = 5000,
            AdminPort = 5001,
            MetricsPort = 5002,
        });

        Assert.True(Program.IsApiRequestAllowed(5000,
            new PathString(path), ports));
        Assert.False(Program.IsApiRequestAllowed(5001,
            new PathString(path), ports));
        Assert.False(Program.IsApiRequestAllowed(5002,
            new PathString(path), ports));
    }

    [Theory]
    [InlineData(0, 5001, 5002)]
    [InlineData(5000, 0, 5002)]
    [InlineData(5000, 5001, 65536)]
    [InlineData(5000, 5000, 5002)]
    [InlineData(5000, 5001, 5000)]
    [InlineData(5000, 5001, 5001)]
    public void InvalidOrDuplicateConfiguredPorts_AreRejected(int publicPort,
        int? adminPort, int? metricsPort)
    {
        var config = new ApiConfig
        {
            Enabled = true,
            ListenAddress = "127.0.0.1",
            Port = publicPort,
            AdminPort = adminPort,
            MetricsPort = metricsPort,
        };

        var result = new ApiConfigValidator().Validate(config);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void DistinctConfiguredPorts_AreValid()
    {
        var config = new ApiConfig
        {
            Enabled = true,
            ListenAddress = "127.0.0.1",
            Port = 5000,
            AdminPort = 5001,
            MetricsPort = 5002,
        };

        new ApiConfigValidator().ValidateAndThrow(config);
    }

    [Fact]
    public void OmittedDedicatedPorts_AreValid()
    {
        var config = new ApiConfig
        {
            Enabled = true,
            ListenAddress = "127.0.0.1",
            Port = 5000,
        };

        new ApiConfigValidator().ValidateAndThrow(config);
    }

    [Fact]
    public void ClusterValidation_ExecutesEnabledApiValidation()
    {
        var result = new ClusterConfigValidator().Validate(new ClusterConfig
        {
            Api = new ApiConfig
            {
                Enabled = true,
                ListenAddress = "127.0.0.1",
                Port = 5000,
                AdminPort = 5000,
            },
            Pools = Array.Empty<PoolConfig>(),
        });

        Assert.Contains(result.Errors, error =>
            error.ErrorMessage ==
            "API: adminPort must differ from port when configured");
    }

    [Fact]
    public void DedicatedApiPortConflictWithEnabledStratum_IsReported()
    {
        var config = new ClusterConfig
        {
            Api = new ApiConfig
            {
                Enabled = true,
                Port = 5000,
                AdminPort = 5001,
                MetricsPort = 5002,
            },
            Pools = new[]
            {
                new PoolConfig
                {
                    Enabled = true,
                    EnableInternalStratum = true,
                    Ports = new Dictionary<int, PoolEndpoint>
                    {
                        [5002] = new(),
                    },
                },
            },
        };

        Assert.Equal(5002,
            Program.FindApiListenerStratumPortConflict(config));
    }

    [Fact]
    public void DisabledOrRelayOnlyPools_DoNotConflictWithApiListeners()
    {
        var config = new ClusterConfig
        {
            Api = new ApiConfig
            {
                Enabled = true,
                Port = 5000,
                AdminPort = 5001,
                MetricsPort = 5002,
            },
            Pools = new[]
            {
                new PoolConfig
                {
                    Enabled = false,
                    EnableInternalStratum = true,
                    Ports = new Dictionary<int, PoolEndpoint>
                    {
                        [5001] = new(),
                    },
                },
                new PoolConfig
                {
                    Enabled = true,
                    EnableInternalStratum = false,
                    Ports = new Dictionary<int, PoolEndpoint>
                    {
                        [5002] = new(),
                    },
                },
            },
        };

        Assert.Null(Program.FindApiListenerStratumPortConflict(config));
    }
}
