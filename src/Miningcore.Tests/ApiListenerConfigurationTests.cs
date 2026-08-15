using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AspNetCoreRateLimit;
using FluentValidation;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miningcore.Api;
using Miningcore.Api.Middlewares;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Stratum;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Prometheus;
using Xunit;

namespace Miningcore.Tests;

public class ApiListenerConfigurationTests
{
    [Fact]
    public void OmittedDedicatedPorts_PreserveSharedListenerCompatibility()
    {
        var ports = Program.ResolveApiEndpointPorts(new ApiConfig
        {
            Port = 4000,
        });

        Assert.Equal(4000, ports.PublicPort);
        Assert.Equal(4000, ports.AdminPort);
        Assert.Equal(4000, ports.MetricsPort);
        Assert.Equal(new[] { 4000 }, ports.ListenerPorts);
        Assert.True(Program.IsApiRequestAllowed(4000,
            new PathString("/api/admin/logging/level/info"), ports));
        Assert.True(Program.IsApiRequestAllowed(4000,
            new PathString("/metrics"), ports));
    }

    [Fact]
    public async Task MissingApiSection_StartsLegacyKestrelListener()
    {
        var config = JsonConvert.DeserializeObject<ClusterConfig>("{}");

        Assert.NotNull(config);
        Assert.Null(config.Api);

        var api = Program.NormalizeApiConfig(config);

        Assert.True(api.Enabled);
        Assert.Same(api, config.Api);
        var ports = Program.ResolveApiEndpointPorts(api);
        Assert.Equal(Program.DefaultApiPort, ports.PublicPort);
        Assert.Equal(new[] { Program.DefaultApiPort }, ports.ListenerPorts);

        // Use an ephemeral port for the real host while retaining the assertions
        // above for the production default.
        await using var host = await StartRouteTestHostWithRetryAsync(true);
        api.Port = host.Ports.PublicPort;
        using var client = new HttpClient();

        await AssertStatusAsync(client, api.Port, "/api/pools",
            HttpStatusCode.OK);
    }

    [Fact]
    public void MinimalApiSection_DeserializesToDefaultPublicPort()
    {
        var config = JsonConvert.DeserializeObject<ClusterConfig>(
            "{\"api\":{\"enabled\":true}}");

        Assert.NotNull(config?.Api);
        Assert.Equal(ApiConfig.DefaultPort, config.Api.Port);
        Assert.Equal(ApiConfig.DefaultPort,
            Program.ResolveApiEndpointPorts(config.Api).PublicPort);
        new ApiConfigValidator().ValidateAndThrow(config.Api);
    }

    [Fact]
    public void CommittedConfigSchema_MatchesGenerator()
    {
        // The build-output schema is the runtime-shipped artifact. MSBuild refreshes
        // it from src/Miningcore/config.schema.json before this test executes.
        var path = Path.Combine(AppContext.BaseDirectory,
            "config.schema.json");
        var committed = JObject.Parse(File.ReadAllText(path));
        var generated = Program.GenerateJsonConfigSchemaDocument();

        Assert.True(JToken.DeepEquals(generated, committed),
            "src/Miningcore/config.schema.json is stale; regenerate it with Miningcore -gcs");

        foreach(var itemTypePath in new[]
                {
                    "definitions.ApiConfig.properties.adminIpWhitelist.items.type",
                    "definitions.ApiConfig.properties.metricsIpWhitelist.items.type",
                    "definitions.ApiRateLimitConfig.properties.ipWhitelist.items.type",
                    "definitions.TcpProxyProtocolConfig.properties.proxyAddresses.items.type",
                    "properties.coinTemplates.items.type",
                })
            Assert.Equal("string",
                committed.SelectToken(itemTypePath)?.Value<string>());
    }

    [Fact]
    public void ConfigSchema_KeepsPoolEndpointNullable_ForDeferredValidation()
    {
        // Miningcore's generated structural schema does not encode the runtime
        // listener predicate: disabled, relay-only and recovery pools deliberately
        // defer endpoint validation. Enforce non-null endpoints in
        // PoolConfigValidator only when a listener will be started.
        var path = Path.Combine(AppContext.BaseDirectory,
            "config.schema.json");
        var committed = JObject.Parse(File.ReadAllText(path));

        Assert.Equal(new[] { "object", "null" }, committed.SelectToken(
                "definitions.PoolEndpoint.type")
            ?.Values<string>());
    }

    [Fact]
    public void RecoveryLoader_MatchesStrictJsonParsingAfterApplyingAllowlist()
    {
        var exampleConfig = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "config.example.json"));
        var corpus = new[]
        {
            exampleConfig,
            CreateRecoveryConfigDocument(true).ToString(Formatting.None),
            "{/* root */\"api\":{/* listener */\"enabled\":true}," +
            "\"pools\":[],\"logging\":{}}",
        };

        foreach(var json in corpus)
        {
            JObject expected;
            using(var expectedReader = new JsonTextReader(
                      new StringReader(json)))
            {
                expected = JObject.Load(expectedReader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling =
                        DuplicatePropertyNameHandling.Error,
                });
            }

            // Keep this independently mirrored rather than reading Program's allowlist. The
            // double-entry assertion detects accidental production-policy drift in either list.
            var recoveryProperties = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                "coinTemplates",
                "logging",
                "persistence",
                "pools",
                "shareRecoveryFile",
                "shareRecoveryStateDirectory",
            };
            foreach(var property in expected.Properties()
                        .Where(property =>
                            !recoveryProperties.Contains(property.Name))
                        .ToArray())
                property.Remove();

            JObject actual;
            using(var actualReader = new JsonTextReader(
                      new StringReader(json)))
                actual = Program.LoadConfigurationDocument(actualReader,
                    true);

            Assert.True(JToken.DeepEquals(expected, actual));
        }
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "[::1]")]
    [InlineData("::", "[::]")]
    public void ListenerHostFormatting_ProducesCopyableUriHost(
        string addressValue, string expected)
    {
        Assert.Equal(expected, Program.FormatListenerHost(
            IPAddress.Parse(addressValue)));
    }

    [Theory]
    [InlineData(null, null, 2)]
    [InlineData(4001, null, 1)]
    [InlineData(null, 4002, 1)]
    [InlineData(4001, 4002, 0)]
    public void SharedProtectedRoutes_ProduceStartupWarnings(
        int? adminPort, int? metricsPort, int expectedCount)
    {
        var warnings = Program.GetSharedProtectedRouteWarnings(new ApiConfig
        {
            AdminPort = adminPort,
            MetricsPort = metricsPort,
        });

        Assert.Equal(expectedCount, warnings.Length);
        Assert.Equal(!adminPort.HasValue,
            warnings.Any(message => message.Contains(
                AdminApiAuthenticationMiddleware.AdminRoutePrefix,
                StringComparison.Ordinal)));
        Assert.Equal(!metricsPort.HasValue,
            warnings.Any(message => message.Contains(
                Program.MetricsRoutePrefix, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(4000, "/api/pools", true)]
    [InlineData(4000, "/notifications", true)]
    [InlineData(4000, "/api/admin/logging/level/info", false)]
    [InlineData(4000, "/metrics", false)]
    [InlineData(4001, "/api/admin/logging/level/info", true)]
    [InlineData(4001, "/api/pools", false)]
    [InlineData(4001, "/metrics", false)]
    [InlineData(4002, "/metrics", true)]
    [InlineData(4002, "/api/pools", false)]
    [InlineData(4002, "/api/admin/logging/level/info", false)]
    public void DedicatedPorts_IsolateRouteFamilies(int port, string path,
        bool expected)
    {
        var ports = Program.ResolveApiEndpointPorts(new ApiConfig
        {
            Port = 4000,
            AdminPort = 4001,
            MetricsPort = 4002,
        });

        Assert.Equal(expected, Program.IsApiRequestAllowed(port,
            new PathString(path), ports));
    }

    [Fact]
    public void DedicatedPorts_CreateThreeListeners()
    {
        var ports = Program.ResolveApiEndpointPorts(new ApiConfig
        {
            Port = 4000,
            AdminPort = 4001,
            MetricsPort = 4002,
        });

        Assert.Equal(new[] { 4000, 4001, 4002 }, ports.ListenerPorts);
        Assert.Same(ports.ListenerPorts, ports.ListenerPorts);
    }

    [Fact]
    public void OneOmittedDedicatedPort_FallsBackIndependently()
    {
        var ports = Program.ResolveApiEndpointPorts(new ApiConfig
        {
            Port = 4000,
            MetricsPort = 4002,
        });

        Assert.Equal(4000, ports.AdminPort);
        Assert.Equal(4002, ports.MetricsPort);
        Assert.Equal(new[] { 4000, 4002 }, ports.ListenerPorts);
        Assert.True(Program.IsApiRequestAllowed(4000,
            new PathString("/api/admin/stats/gc"), ports));
        Assert.False(Program.IsApiRequestAllowed(4000,
            new PathString("/metrics"), ports));
    }

    [Fact]
    public void OmittedMetricsPort_FallsBackIndependently()
    {
        var ports = Program.ResolveApiEndpointPorts(new ApiConfig
        {
            Port = 4000,
            AdminPort = 4001,
        });

        Assert.Equal(4001, ports.AdminPort);
        Assert.Equal(4000, ports.MetricsPort);
        Assert.Equal(new[] { 4000, 4001 }, ports.ListenerPorts);
        Assert.True(Program.IsApiRequestAllowed(4000,
            new PathString("/metrics"), ports));
        Assert.False(Program.IsApiRequestAllowed(4000,
            new PathString("/api/admin/stats/gc"), ports));
    }

    [Theory]
    [InlineData("/api/administrator")]
    [InlineData("/metrics-export")]
    public void SimilarPublicPathNames_AreNotMisclassified(string path)
    {
        var ports = Program.ResolveApiEndpointPorts(new ApiConfig
        {
            Port = 4000,
            AdminPort = 4001,
            MetricsPort = 4002,
        });

        Assert.True(Program.IsApiRequestAllowed(4000,
            new PathString(path), ports));
        Assert.False(Program.IsApiRequestAllowed(4001,
            new PathString(path), ports));
        Assert.False(Program.IsApiRequestAllowed(4002,
            new PathString(path), ports));
    }

    [Theory]
    [InlineData(-1, 4001, 4002)]
    [InlineData(0, 4001, 4002)]
    [InlineData(65536, 4001, 4002)]
    [InlineData(4000, -1, 4002)]
    [InlineData(4000, 0, 4002)]
    [InlineData(4000, 65536, 4002)]
    [InlineData(4000, 4001, -1)]
    [InlineData(4000, 4001, 65536)]
    [InlineData(4000, 4000, 4002)]
    [InlineData(4000, 4001, 4000)]
    [InlineData(4000, 4001, 4001)]
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

    [Theory]
    [InlineData(4000, 4000, 4002,
        "API: adminPort must differ from port when configured")]
    [InlineData(4000, 4001, 4000,
        "API: metricsPort must differ from port when configured")]
    [InlineData(4000, 4001, 4001,
        "API: adminPort and metricsPort must differ when configured")]
    public void DuplicateConfiguredPorts_ReportClearConfigurationErrors(
        int publicPort, int? adminPort, int? metricsPort,
        string expectedMessage)
    {
        var result = new ApiConfigValidator().Validate(new ApiConfig
        {
            Enabled = true,
            ListenAddress = "127.0.0.1",
            Port = publicPort,
            AdminPort = adminPort,
            MetricsPort = metricsPort,
        });

        Assert.Contains(result.Errors,
            error => error.ErrorMessage == expectedMessage);
    }

    [Theory]
    [InlineData(0, 4002, "adminPort")]
    [InlineData(4001, 65536, "metricsPort")]
    public void DedicatedPortRangeErrors_UsePublicPropertyNames(
        int? adminPort, int? metricsPort, string expectedPropertyName)
    {
        var result = new ApiConfigValidator().Validate(new ApiConfig
        {
            Enabled = true,
            Port = 4000,
            AdminPort = adminPort,
            MetricsPort = metricsPort,
        });

        Assert.Contains(result.Errors, error =>
            error.PropertyName == expectedPropertyName);
    }

    [Fact]
    public void DistinctConfiguredPorts_AreValid()
    {
        var config = new ApiConfig
        {
            Enabled = true,
            ListenAddress = "127.0.0.1",
            Port = 4000,
            AdminPort = 4001,
            MetricsPort = 4002,
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
            Port = 4000,
        };

        new ApiConfigValidator().ValidateAndThrow(config);
    }

    [Fact]
    public void OmittedListenAddress_RemainsValidForRuntimeLoopbackDefault()
    {
        var config = new ApiConfig
        {
            Enabled = true,
            Port = 4000,
        };

        var result = new ClusterConfigValidator().Validate(new ClusterConfig
        {
            Api = config,
            Pools = Array.Empty<PoolConfig>(),
        });

        Assert.DoesNotContain(result.Errors, error =>
            error.PropertyName.Contains(nameof(ApiConfig.ListenAddress),
                StringComparison.Ordinal));
        Assert.Null(config.ListenAddress);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("*", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("localhost", false)]
    [InlineData("not-an-ip-address", false)]
    [InlineData("127.0.0.999", false)]
    public void ListenAddress_RequiresWildcardOrIpLiteral(
        string listenAddress, bool expectedValid)
    {
        var result = new ApiConfigValidator().Validate(new ApiConfig
        {
            Enabled = true,
            ListenAddress = listenAddress,
            Port = 4000,
        });

        Assert.Equal(expectedValid, result.IsValid);
        Assert.Equal(!expectedValid, result.Errors.Any(error =>
            error.ErrorMessage ==
            "API: listenAddress must be '*' or a valid IPv4/IPv6 address"));
    }

    [Fact]
    public void StratumListenAddress_ReportsPoolAndPort()
    {
        var result = new PoolConfigValidator().Validate(new PoolConfig
        {
            Id = "btc-main",
            Coin = "bitcoin",
            Enabled = true,
            Address = "wallet",
            EnableInternalStratum = true,
            Ports = new Dictionary<int, PoolEndpoint>
            {
                [3333] = new()
                {
                    Difficulty = 1,
                    ListenAddress = "stratum.example.com",
                },
            },
            Daemons = new[]
            {
                new DaemonEndpointConfig
                {
                    Host = "127.0.0.1",
                    Port = 8332,
                },
            },
        });

        Assert.Contains(result.Errors, error =>
            error.PropertyName == "Ports[3333].ListenAddress" &&
            error.ErrorMessage ==
                "Pool 'btc-main' Stratum port 3333: listenAddress must be '*' or a valid IPv4/IPv6 address (received 'stratum.example.com')");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(ushort.MaxValue, true)]
    [InlineData(ushort.MaxValue + 1, false)]
    public void ActiveStratumPort_RequiresTcpPortRange(int port,
        bool expectedValid)
    {
        var result = new PoolConfigValidator().Validate(new PoolConfig
        {
            Id = "btc-main",
            Coin = "bitcoin",
            Enabled = true,
            Address = "wallet",
            EnableInternalStratum = true,
            Ports = new Dictionary<int, PoolEndpoint>
            {
                [port] = new()
                {
                    Difficulty = 1,
                    ListenAddress = "127.0.0.1",
                },
            },
            Daemons = new[]
            {
                new DaemonEndpointConfig
                {
                    Host = "127.0.0.1",
                    Port = 8332,
                },
            },
        });

        Assert.Equal(expectedValid, result.IsValid);
        Assert.Equal(!expectedValid, result.Errors.Any(error =>
            error.ErrorMessage ==
            $"Pool: Invalid stratum port number {port}"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ushort.MaxValue + 1)]
    public void RecoveryMode_DiscardsOutOfRangeStratumPorts(int port)
    {
        var config = CreateValidRecoveryConfig(new ApiConfig
        {
            Enabled = true,
            Port = 4000,
        });
        config.Pools[0].EnableInternalStratum = true;
        config.Pools[0].Ports = new Dictionary<int, PoolEndpoint>
        {
            [port] = new()
            {
                Difficulty = 1,
                ListenAddress = "127.0.0.1",
            },
        };
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, SerializeConfig(config));

            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));
            var recoveryConfig = Program.ReadAndValidateConfig(configFile,
                true);
            Assert.Empty(recoveryConfig.Pools[0].Ports);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void DisabledInternalStratumPools_AreReportedAsSkippedValidation()
    {
        var config = new ClusterConfig
        {
            Pools = new[]
            {
                new PoolConfig
                {
                    Id = "disabled-internal",
                    EnableInternalStratum = true,
                    Ports = new Dictionary<int, PoolEndpoint>
                    {
                        [3333] = new(),
                    },
                },
                new PoolConfig
                {
                    Id = "disabled-relay-only",
                    EnableInternalStratum = false,
                    Ports = new Dictionary<int, PoolEndpoint>
                    {
                        [4444] = new(),
                    },
                },
                new PoolConfig
                {
                    Id = "enabled-internal",
                    Enabled = true,
                    EnableInternalStratum = true,
                    Ports = new Dictionary<int, PoolEndpoint>
                    {
                        [5555] = new(),
                    },
                },
            },
        };

        Assert.Equal(new[] { "disabled-internal" },
            Program.GetPoolsWithSkippedStratumListenerValidation(config));
    }

    [Fact]
    public void ConflictScan_SkipsMalformedStratumAddressWithoutThrowing()
    {
        var config = CreateValidRecoveryConfig(new ApiConfig
        {
            Enabled = true,
            Port = 4000,
        });
        config.Pools[0].EnableInternalStratum = true;
        config.Pools[0].Ports = new Dictionary<int, PoolEndpoint>
        {
            [4000] = new()
            {
                Difficulty = 1,
                ListenAddress = "stratum.example.com",
            },
        };

        Assert.Null(Program.FindApiListenerStratumPortConflict(config, false));
        Assert.Throws<PoolStartupException>(() =>
            Program.ValidateConfig(config, false));
    }

    [Fact]
    public void ConflictScan_SkipsNullStratumEndpointWithoutThrowing()
    {
        var config = CreateValidRecoveryConfig(new ApiConfig
        {
            Enabled = true,
            Port = 4000,
        });
        config.Pools[0].EnableInternalStratum = true;
        config.Pools[0].Ports = new Dictionary<int, PoolEndpoint>
        {
            [4000] = null,
        };

        Assert.Null(Program.FindApiListenerStratumPortConflict(config, false));
        var validation = new ClusterConfigValidator().Validate(config);
        Assert.Contains(validation.Errors, failure =>
            failure.ErrorMessage ==
            "Pool 'recovery-pool' Stratum port 4000: endpoint configuration must not be null");
        Assert.Throws<PoolStartupException>(() =>
            Program.ValidateConfig(config, false));
    }

    [Fact]
    public void NullStratumEndpoint_FailsNormalLoadButNotRecovery()
    {
        var sourceConfig = CreateValidRecoveryConfig(new ApiConfig
        {
            Enabled = true,
            Port = 4000,
        });
        sourceConfig.Pools[0].EnableInternalStratum = true;
        sourceConfig.Pools[0].Ports = new Dictionary<int, PoolEndpoint>
        {
            [3031] = new()
            {
                Difficulty = 1,
                ListenAddress = "127.0.0.1",
            },
            [3032] = null,
        };
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, SerializeConfig(sourceConfig));

            var validation = new ClusterConfigValidator()
                .Validate(sourceConfig);
            Assert.Contains(validation.Errors, failure =>
                failure.ErrorMessage ==
                "Pool 'recovery-pool' Stratum port 3032: endpoint configuration must not be null");
            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));

            var recoveryConfig = Program.ReadAndValidateConfig(configFile,
                true);
            Assert.Empty(recoveryConfig.Pools[0].Ports);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void NullStratumEndpoint_RemainsDeferredForInactiveListener(
        bool enabled, bool internalStratum)
    {
        var sourceConfig = CreateValidRecoveryConfig(new ApiConfig
        {
            Enabled = true,
            Port = 4000,
        });
        var inactivePool = sourceConfig.Pools[0];
        inactivePool.Enabled = enabled;
        inactivePool.EnableInternalStratum = internalStratum;
        inactivePool.Ports = new Dictionary<int, PoolEndpoint>
        {
            [3032] = null,
        };

        if(!enabled)
        {
            var activePool = CreateValidRecoveryConfig(new ApiConfig())
                .Pools[0];
            activePool.Id = "active-relay-pool";
            sourceConfig.Pools = new[] { inactivePool, activePool };
        }

        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, SerializeConfig(sourceConfig));

            var config = Program.ReadAndValidateConfig(configFile, false);

            Assert.Null(config.Pools[0].Ports[3032]);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Theory]
    [InlineData(true, null,
        "API: adminIpWhitelist[0] must not be null")]
    [InlineData(true, "localhost",
        "API: adminIpWhitelist[0] contains invalid IP address 'localhost'")]
    [InlineData(false, null,
        "API: metricsIpWhitelist[0] must not be null")]
    [InlineData(false, "127.0.0.999",
        "API: metricsIpWhitelist[0] contains invalid IP address '127.0.0.999'")]
    public void IpWhitelists_RequireNonNullIpLiterals(bool admin,
        string address, string expectedMessage)
    {
        var config = new ApiConfig
        {
            Enabled = true,
            Port = 4000,
            AdminIpWhitelist = admin ? new[] { address } : null,
            MetricsIpWhitelist = admin ? null : new[] { address },
        };

        var result = new ApiConfigValidator().Validate(config);

        Assert.Contains(result.Errors,
            error => error.ErrorMessage == expectedMessage);
    }

    [Fact]
    public void IpWhitelists_AcceptIpv4Ipv6AndMappedLiterals()
    {
        var config = new ApiConfig
        {
            Enabled = true,
            Port = 4000,
            AdminIpWhitelist = new[] { "127.0.0.1", "::1" },
            MetricsIpWhitelist = new[] { "::ffff:192.0.2.1" },
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
                Port = 4000,
                AdminPort = 4000,
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
                Port = 4000,
                AdminPort = 4001,
                MetricsPort = 4002,
            },
            Pools = new[]
            {
                new PoolConfig
                {
                    Enabled = true,
                    EnableInternalStratum = true,
                    Ports = new Dictionary<int, PoolEndpoint>
                    {
                        [4002] = new(),
                    },
                },
            },
        };

        Assert.Equal(4002,
            Program.FindApiListenerStratumPortConflict(config, false));
    }

    [Fact]
    public void RecoveryMode_SkipsApiStratumListenerConflict()
    {
        var config = new ClusterConfig
        {
            Api = new ApiConfig
            {
                Enabled = true,
                Port = 4000,
                AdminPort = 4001,
                MetricsPort = 4002,
            },
            Pools = new[]
            {
                new PoolConfig
                {
                    Enabled = true,
                    EnableInternalStratum = true,
                    Ports = new Dictionary<int, PoolEndpoint>
                    {
                        [4002] = new(),
                    },
                },
            },
        };

        Assert.Null(Program.FindApiListenerStratumPortConflict(config, true));
    }

    [Theory]
    [InlineData("127.0.0.1", 4000, "127.0.0.1", 4000, true)]
    [InlineData("*", 4000, "192.168.10.20", 4000, true)]
    [InlineData("127.0.0.1", 4000, "*", 4000, true)]
    [InlineData("127.0.0.1", 4000, "192.168.10.20", 4000, false)]
    [InlineData("127.0.0.1", 4000, "127.0.0.1", 4003, false)]
    [InlineData("::", 4000, "127.0.0.1", 4000, true)]
    [InlineData("0.0.0.0", 4000, "::", 4000, true)]
    [InlineData("127.0.0.1", 4000, "::ffff:127.0.0.1", 4000, true)]
    [InlineData("::1", 4000, "127.0.0.1", 4000, false)]
    [InlineData("::1", 4000, "::1", 4000, true)]
    public void ApiStratumConflict_RequiresOverlappingAddressAndPort(
        string apiAddress, int apiPort, string stratumAddress, int stratumPort,
        bool expectedConflict)
    {
        var config = new ClusterConfig
        {
            Api = new ApiConfig
            {
                Enabled = true,
                ListenAddress = apiAddress,
                Port = apiPort,
            },
            Pools = new[]
            {
                new PoolConfig
                {
                    Enabled = true,
                    EnableInternalStratum = true,
                    Ports = new Dictionary<int, PoolEndpoint>
                    {
                        [stratumPort] = new()
                        {
                            ListenAddress = stratumAddress,
                        },
                    },
                },
            },
        };

        var conflict = Program.FindApiListenerStratumPortConflict(config, false);

        if(expectedConflict)
            Assert.Equal(apiPort, conflict);
        else
            Assert.Null(conflict);
    }

    [Theory]
    [InlineData("0.0.0.0", "::1")]
    [InlineData("0.0.0.0", "127.0.0.1")]
    [InlineData("::", "127.0.0.1")]
    [InlineData("::", "2001:db8::10")]
    [InlineData("127.0.0.1", "::ffff:127.0.0.1")]
    public void ListenAddressOverlap_IsSymmetric(string firstValue,
        string secondValue)
    {
        var first = Program.ResolveListenAddress(firstValue);
        var second = Program.ResolveListenAddress(secondValue);

        Assert.Equal(Program.ListenAddressesOverlap(first, second),
            Program.ListenAddressesOverlap(second, first));
    }

    [Theory]
    [InlineData("::", "127.0.0.1")]
    [InlineData("0.0.0.0", "::")]
    public async Task DualStackConflictValidation_MatchesRealSocketBinding(
        string apiListenAddress, string stratumListenAddress)
    {
        // Windows permits combinations with SO_REUSEADDR that Linux, the
        // production target, rejects. Linux CI pins the actual socket behavior;
        // the comparison cases above remain platform independent.
        if(!Socket.OSSupportsIPv6 || !OperatingSystem.IsLinux())
            return;

        var apiAddress = Program.ResolveListenAddress(apiListenAddress);
        var stratumAddress = Program.ResolveListenAddress(
            stratumListenAddress);
        Assert.True(Program.ListenAddressesOverlap(apiAddress,
            stratumAddress));

        var binding = await StartBindingTestHostWithRetryAsync(apiAddress);
        using var host = binding.Host;
        var port = binding.Port;
        using var stratumSocket = StratumServer.CreateListenSocket(
            new IPEndPoint(stratumAddress, port));
        stratumSocket.SetSocketOption(SocketOptionLevel.Socket,
            SocketOptionName.ReuseAddress, true);

        Assert.Throws<SocketException>(() =>
        {
            stratumSocket.Bind(new IPEndPoint(stratumAddress, port));
            stratumSocket.Listen();
        });
    }

    [Theory]
    [InlineData("127.0.0.1", AddressFamily.InterNetwork, false)]
    [InlineData("::1", AddressFamily.InterNetworkV6, false)]
    [InlineData("::", AddressFamily.InterNetworkV6, true)]
    public void StratumListenSocket_MirrorsConfiguredAddressFamily(
        string addressValue, AddressFamily expectedFamily, bool dualMode)
    {
        if(expectedFamily == AddressFamily.InterNetworkV6 &&
            !Socket.OSSupportsIPv6)
            return;

        var address = IPAddress.Parse(addressValue);
        using var socket = StratumServer.CreateListenSocket(
            new IPEndPoint(address, 4000));

        Assert.Equal(expectedFamily, socket.AddressFamily);
        if(expectedFamily == AddressFamily.InterNetworkV6)
            Assert.Equal(dualMode, socket.DualMode);
    }

    [Theory]
    [InlineData(4000, null)]
    [InlineData(null, 0)]
    public async Task RecoveryMode_ConfigFileListenerErrorsDoNotBlockImporter(
        int? adminPort, int? metricsPort)
    {
        var sourceConfig = CreateValidRecoveryConfig(new ApiConfig
        {
            Enabled = true,
            Port = 4000,
            AdminPort = adminPort,
            MetricsPort = metricsPort,
        });
        var configFile = Path.GetTempFileName();
        var recovered = false;
        var stopped = false;

        try
        {
            File.WriteAllText(configFile,
                SerializeConfig(sourceConfig));
            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));

            Program.ReadAndValidateConfig(configFile, true);
            await Program.RunRecoveryModeAsync(() =>
            {
                recovered = true;
                return Task.CompletedTask;
            }, () => stopped = true);
        }
        finally
        {
            File.Delete(configFile);
        }

        Assert.True(recovered);
        Assert.True(stopped);
    }

    [Fact]
    public async Task RecoveryMode_StaleStratumListenerSettingsDoNotBlockImporter()
    {
        var sourceConfig = CreateValidRecoveryConfig(new ApiConfig
        {
            Enabled = true,
            Port = 4000,
        });
        sourceConfig.Pools[0].EnableInternalStratum = true;
        sourceConfig.Pools[0].Ports = new Dictionary<int, PoolEndpoint>
        {
            [3333] = new()
            {
                Difficulty = 1,
                ListenAddress = "old-stratum.example.com",
                Tls = true,
                TlsPfxFile = Path.Combine(Path.GetTempPath(),
                    $"missing-stratum-{Guid.NewGuid():N}.pfx"),
            },
        };
        var configFile = Path.GetTempFileName();
        var recovered = false;
        var stopped = false;

        try
        {
            File.WriteAllText(configFile, SerializeConfig(sourceConfig));

            var validation = new ClusterConfigValidator().Validate(
                sourceConfig);
            Assert.Contains(validation.Errors, error =>
                error.PropertyName == "Pools[0].Ports[3333].ListenAddress" &&
                error.ErrorMessage.Contains("old-stratum.example.com",
                    StringComparison.Ordinal) &&
                error.ErrorMessage.Contains("recovery-pool",
                    StringComparison.Ordinal) &&
                error.ErrorMessage.Contains("3333",
                    StringComparison.Ordinal));
            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));

            var recoveryConfig = Program.ReadAndValidateConfig(configFile,
                true);
            Assert.Empty(recoveryConfig.Pools[0].Ports);
            await Program.RunRecoveryModeAsync(() =>
            {
                recovered = true;
                return Task.CompletedTask;
            }, () => stopped = true);
        }
        finally
        {
            File.Delete(configFile);
        }

        Assert.True(recovered);
        Assert.True(stopped);
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("not-a-port")]
    public async Task RecoveryOperations_IgnoreUnbindableRawStratumPortKeys(
        string rawPort)
    {
        var document = CreateRecoveryConfigDocument(true);
        var pool = Assert.IsType<JObject>(document["pools"]?[0]);
        pool["enableInternalStratum"] = true;
        pool["ports"] = new JObject
        {
            [rawPort] = new JObject
            {
                ["difficulty"] = 1,
                ["listenAddress"] = "127.0.0.1",
            },
        };
        var configFile = Path.GetTempFileName();
        var recovered = false;
        var stopped = false;

        try
        {
            File.WriteAllText(configFile, document.ToString());

            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));

            var importConfig = Program.ReadAndValidateConfig(configFile, true);
            Assert.Empty(importConfig.Pools[0].Ports);
            await Program.RunRecoveryModeAsync(() =>
            {
                recovered = true;
                return Task.CompletedTask;
            }, () => stopped = true);
            Assert.True(recovered);
            Assert.True(stopped);

            var verificationConfig = Program.ReadConfig(configFile, true);
            var acknowledgementConfig = Program.ReadConfig(configFile, true);
            Assert.Empty(verificationConfig.Pools[0].Ports);
            Assert.Empty(acknowledgementConfig.Pools[0].Ports);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void RecoveryMode_StillRejectsCaseVariantStratumPorts()
    {
        var document = CreateRecoveryConfigDocument(true);
        var pool = Assert.IsType<JObject>(document["pools"]?[0]);
        pool.Add("Ports", pool["ports"]?.DeepClone() ?? new JObject());
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, document.ToString());

            var error = Assert.Throws<PoolStartupException>(() =>
                Program.ReadConfig(configFile, true));
            Assert.Contains(
                "Properties 'ports', 'Ports' differ only by case. " +
                "Path 'pools[0].Ports', line",
                error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void RecoveryMode_StillRejectsExactDuplicatesInsideStratumPorts()
    {
        var document = CreateRecoveryConfigDocument(true);
        var pool = Assert.IsType<JObject>(document["pools"]?[0]);
        pool["enableInternalStratum"] = true;
        pool["ports"] = JObject.Parse(
            "{\"3333\":{\"difficulty\":1}}");
        var rawConfig = document.ToString(Formatting.None)
            .Replace("\"3333\":{\"difficulty\":1}",
                "\"3333\":{\"difficulty\":1}," +
                "\"3333\":{\"difficulty\":2}",
                StringComparison.Ordinal);
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, rawConfig);

            var error = Assert.Throws<PoolStartupException>(() =>
                Program.ReadConfig(configFile, true));
            Assert.Contains("Property with the name '3333' already exists",
                error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Theory]
    [InlineData("[]", "The configuration root must be a JSON object.",
        "Line 1, position")]
    [InlineData("{", "Unexpected end of configuration file.",
        "Line 1, position")]
    [InlineData("{\n\"pools\":", "Configuration property 'pools' has no value.",
        "Path 'pools', line 2, position")]
    public void RecoveryLoader_StructuralErrorsIncludeLocation(string json,
        string expectedMessage, string expectedLocation)
    {
        using var reader = new JsonTextReader(new StringReader(json));

        var error = Assert.Throws<JsonSerializationException>(() =>
            Program.LoadConfigurationDocument(reader, true));

        Assert.Contains(expectedMessage, error.Message,
            StringComparison.Ordinal);
        Assert.Contains(expectedLocation, error.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void InactiveStratumListeners_IgnoreStaleAddressAndTlsSettings(
        bool poolEnabled, bool enableInternalStratum)
    {
        var config = CreateValidRecoveryConfig(new ApiConfig
        {
            Enabled = true,
            Port = 4000,
        });
        config.Pools[0].Enabled = poolEnabled;
        config.Pools[0].EnableInternalStratum = enableInternalStratum;
        config.Pools[0].Ports = new Dictionary<int, PoolEndpoint>
        {
            [3333] = new()
            {
                Difficulty = 1,
                ListenAddress = "inactive-stratum.example.com",
                Tls = true,
                TlsPfxFile = "missing-inactive-stratum.pfx",
            },
        };

        config.Validate();
    }

    [Fact]
    public void DisabledApi_ConfigFileAllowsStaleListenerValues()
    {
        var sourceConfig = CreateValidRecoveryConfig(new ApiConfig
        {
            Enabled = false,
            MetricsPort = 0,
        });
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile,
                SerializeConfig(sourceConfig));
            var config = Program.ReadAndValidateConfig(configFile, false);
            Assert.False(config.Api.Enabled);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public async Task RecoveryMode_OutOfClrRangeListenerPortDoesNotBlockImporter()
    {
        var document = CreateRecoveryConfigDocument(true);
        document["api"]["metricsPort"] = (long) int.MaxValue + 1;
        var configFile = Path.GetTempFileName();
        var recovered = false;
        var stopped = false;

        try
        {
            File.WriteAllText(configFile, document.ToString());
            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));

            var config = Program.ReadAndValidateConfig(configFile, true);
            Assert.Null(config.Api);
            await Program.RunRecoveryModeAsync(() =>
            {
                recovered = true;
                return Task.CompletedTask;
            }, () => stopped = true);
        }
        finally
        {
            File.Delete(configFile);
        }

        Assert.True(recovered);
        Assert.True(stopped);
    }

    [Fact]
    public void DisabledApi_OutOfClrRangeListenerPortDoesNotBlockStartup()
    {
        var document = CreateRecoveryConfigDocument(false);
        document["api"]["metricsPort"] = (long) int.MaxValue + 1;
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, document.ToString());

            var normalConfig = Program.ReadAndValidateConfig(configFile, false);
            var recoveryConfig = Program.ReadAndValidateConfig(configFile, true);

            Assert.False(normalConfig.Api.Enabled);
            Assert.Null(normalConfig.Api.MetricsPort);
            Assert.Null(recoveryConfig.Api);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Theory]
    [InlineData("api", "API", "API")]
    [InlineData("enabled", "Enabled", "api.Enabled")]
    [InlineData("metricsPort", "MetricsPort", "api.MetricsPort")]
    public void CaseVariantDuplicateProperties_AreRejectedDuringNormalStartup(
        string propertyName, string duplicateName, string expectedPath)
    {
        var document = CreateRecoveryConfigDocument(false);
        var api = Assert.IsType<JObject>(document["api"]);

        if(propertyName == "api")
            document.Add(duplicateName, api.DeepClone());
        else
            api.Add(duplicateName, api[propertyName]?.DeepClone() ??
                JValue.CreateNull());

        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, document.ToString());

            var exception = Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));
            Assert.Contains(
                $"Properties '{propertyName}', '{duplicateName}' " +
                $"differ only by case. Path '{expectedPath}', line ",
                exception.Message, StringComparison.Ordinal);
            Assert.Contains(", position ", exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void RecoveryMode_CaseVariantRootDuplicateWithoutLineInfoRetainsContainerPath()
    {
        var document = CreateRecoveryConfigDocument(false);
        document.Add("Pools",
            document["pools"]?.DeepClone() ?? new JArray());
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, document.ToString());

            var exception = Assert.Throws<PoolStartupException>(() =>
                Program.ReadConfig(configFile, true));
            Assert.Contains(
                "Properties 'pools', 'Pools' at '$' " +
                "differ only by case.",
                exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(" Path '", exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void RecoveryMode_IgnoresCaseVariantApiDuplicates()
    {
        var document = CreateRecoveryConfigDocument(false);
        var api = Assert.IsType<JObject>(document["api"]);
        api.Add("MetricsPort", (long) int.MaxValue + 1);
        document.Add("API", api.DeepClone());
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, document.ToString());

            var config = Program.ReadAndValidateConfig(configFile, true);

            Assert.Null(config.Api);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryOperations_IgnoreExactDuplicateApiProperties(
        bool duplicateRootApi)
    {
        var document = CreateRecoveryConfigDocument(true);
        var api = Assert.IsType<JObject>(document["api"]);
        string rawConfig;

        if(duplicateRootApi)
        {
            rawConfig = CreateRawConfigurationWithRootProperties(document,
                ("api", api.ToString(Formatting.None)),
                ("api", "{\"enabled\":false}"));
        }
        else
        {
            var apiJson = api.ToString(Formatting.None);
            apiJson = apiJson.Insert(apiJson.Length - 1,
                ",\"metricsPort\":2147483648");
            rawConfig = CreateRawConfigurationWithRootProperties(document,
                ("api", apiJson));
        }

        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, rawConfig);

            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));

            // -rs validates the remaining recovery configuration before invoking the importer.
            var importConfig = Program.ReadAndValidateConfig(configFile, true);
            Assert.Null(importConfig.Api);

            var recovered = false;
            var stopped = false;
            await Program.RunRecoveryModeAsync(() =>
            {
                recovered = true;
                return Task.CompletedTask;
            }, () => stopped = true);
            Assert.True(recovered);
            Assert.True(stopped);

            // Both recovery-state commands use the same non-validating recovery reader.
            var verificationConfig = Program.ReadConfig(configFile, true);
            var acknowledgementConfig = Program.ReadConfig(configFile, true);
            Assert.Null(verificationConfig.Api);
            Assert.Null(acknowledgementConfig.Api);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public async Task RecoveryOperations_SanitizeOptionalCoinTemplateMetadata()
    {
        const string customTemplate = "custom-coins.json";
        var document = CreateRecoveryConfigDocument(true);
        document["coinTemplates"] = new JArray(customTemplate,
            JValue.CreateNull(), 1, new JObject());
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, document.ToString());

            var startupError = Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));
            Assert.Contains("coinTemplates[1]", startupError.Message,
                StringComparison.OrdinalIgnoreCase);

            var importConfig = Program.ReadAndValidateConfig(configFile,
                true);
            Assert.Equal(new[] { customTemplate },
                importConfig.CoinTemplates);

            var recovered = false;
            var stopped = false;
            await Program.RunRecoveryModeAsync(() =>
            {
                recovered = true;
                return Task.CompletedTask;
            }, () => stopped = true);
            Assert.True(recovered);
            Assert.True(stopped);

            var verificationConfig = Program.ReadConfig(configFile, true);
            var acknowledgementConfig = Program.ReadConfig(configFile, true);
            Assert.Equal(new[] { customTemplate },
                verificationConfig.CoinTemplates);
            Assert.Equal(new[] { customTemplate },
                acknowledgementConfig.CoinTemplates);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("1")]
    [InlineData("\"invalid-scalar\"")]
    public void RecoveryMode_DiscardsMalformedCoinTemplateContainers(
        string coinTemplatesJson)
    {
        var document = CreateRecoveryConfigDocument(true);
        document["coinTemplates"] = JToken.Parse(coinTemplatesJson);
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, document.ToString());

            Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));
            var recoveryConfig = Program.ReadAndValidateConfig(configFile,
                true);
            Assert.Null(recoveryConfig.CoinTemplates);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryMode_StillRejectsAmbiguousCoinTemplateProperties(
        bool caseVariant)
    {
        var document = CreateRecoveryConfigDocument(true);
        document["coinTemplates"] = new JArray(JValue.CreateNull());
        string rawConfig;

        if(caseVariant)
        {
            document.Add("CoinTemplates", new JArray());
            rawConfig = document.ToString();
        }
        else
        {
            rawConfig = CreateRawConfigurationWithRootProperties(document,
                ("coinTemplates", "[null]"),
                ("coinTemplates", "[]"));
        }

        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, rawConfig);

            Assert.Throws<PoolStartupException>(() =>
                Program.ReadConfig(configFile, true));
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void RecoveryMode_StillRejectsExactDuplicatesOutsideApi()
    {
        var document = CreateRecoveryConfigDocument(true);
        var pools = document["pools"].ToString(Formatting.None);
        var rawConfig = $"{{\n\"pools\":{pools},\n\"pools\":{pools}\n}}";
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, rawConfig);

            var error = Assert.Throws<PoolStartupException>(() =>
                Program.ReadConfig(configFile, true));
            Assert.Contains(
                "Property with the name 'pools' already exists",
                error.Message, StringComparison.Ordinal);
            Assert.Contains("Path 'pools', line 3, position",
                error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("{}")]
    public void MalformedApiEnabled_ReportsConfigurationError(string tokenJson)
    {
        var document = CreateRecoveryConfigDocument(false);
        document["api"]["enabled"] = JToken.Parse(tokenJson);
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, document.ToString());

            var exception = Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));

            Assert.Contains("Configuration file error:", exception.Message,
                StringComparison.Ordinal);
            Assert.Contains("api.enabled", exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void FreeFormPayoutSchemeConfig_AllowsCaseVariantKeys()
    {
        var document = CreateRecoveryConfigDocument(false);
        document["pools"][0]["paymentProcessing"] = new JObject
        {
            ["enabled"] = false,
            ["minimumPayment"] = 0,
            ["payoutScheme"] = "PPLNS",
            ["payoutSchemeConfig"] = JObject.Parse(
                "{\"Window\":1,\"window\":2}"),
        };
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, document.ToString());

            var config = Program.ReadConfig(configFile);
            var payoutConfig = Assert.IsType<JObject>(
                config.Pools[0].PaymentProcessing.PayoutSchemeConfig);

            Assert.Equal(1, payoutConfig["Window"]?.Value<int>());
            Assert.Equal(2, payoutConfig["window"]?.Value<int>());
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void RecoveryMode_InvalidApiWhitelistDoesNotBlockImporter()
    {
        var document = CreateRecoveryConfigDocument(true);
        document["api"]["adminIpWhitelist"] = new JArray(
            JValue.CreateNull());
        var configFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configFile, document.ToString());

            var startupError = Assert.Throws<PoolStartupException>(() =>
                Program.ReadAndValidateConfig(configFile, false));
            Assert.Contains("api.adminIpWhitelist[0]", startupError.Message,
                StringComparison.OrdinalIgnoreCase);

            var recoveryConfig = Program.ReadAndValidateConfig(configFile,
                true);
            Assert.Null(recoveryConfig.Api);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void DisabledOrRelayOnlyPools_DoNotConflictWithApiListeners()
    {
        var config = new ClusterConfig
        {
            Api = new ApiConfig
            {
                Enabled = true,
                Port = 4000,
                AdminPort = 4001,
                MetricsPort = 4002,
            },
            Pools = new[]
            {
                new PoolConfig
                {
                    Enabled = false,
                    EnableInternalStratum = true,
                    Ports = new Dictionary<int, PoolEndpoint>
                    {
                        [4001] = new(),
                    },
                },
                new PoolConfig
                {
                    Enabled = true,
                    EnableInternalStratum = false,
                    Ports = new Dictionary<int, PoolEndpoint>
                    {
                        [4002] = new(),
                    },
                },
            },
        };

        Assert.Null(Program.FindApiListenerStratumPortConflict(config, false));
    }

    [Fact]
    public async Task DedicatedHttpListeners_EnforceCompleteRouteMatrix()
    {
        await using var host = await StartRouteTestHostWithRetryAsync();
        var ports = host.Ports;
        using var client = new HttpClient();

        await AssertStatusAsync(client, ports.PublicPort, "/api/pools",
            HttpStatusCode.OK);
        await AssertStatusAsync(client, ports.PublicPort, "/notifications",
            HttpStatusCode.OK);
        await AssertStatusAsync(client, ports.PublicPort, "/api/admin/status",
            HttpStatusCode.NotFound);
        await AssertStatusAsync(client, ports.PublicPort, "/metrics",
            HttpStatusCode.NotFound);

        await AssertStatusAsync(client, ports.AdminPort, "/api/admin/status",
            HttpStatusCode.OK);
        await AssertStatusAsync(client, ports.AdminPort, "/api/pools",
            HttpStatusCode.NotFound);
        await AssertStatusAsync(client, ports.AdminPort, "/notifications",
            HttpStatusCode.NotFound);
        await AssertStatusAsync(client, ports.AdminPort, "/metrics",
            HttpStatusCode.NotFound);

        await AssertStatusAsync(client, ports.MetricsPort, "/metrics",
            HttpStatusCode.OK);
        await AssertStatusAsync(client, ports.MetricsPort, "/api/pools",
            HttpStatusCode.NotFound);
        await AssertStatusAsync(client, ports.MetricsPort,
            "/api/admin/status", HttpStatusCode.NotFound);
        await AssertStatusAsync(client, ports.MetricsPort, "/notifications",
            HttpStatusCode.NotFound);

        using var webSocket = new ClientWebSocket();
        await webSocket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{ports.PublicPort}/notifications"),
            CancellationToken.None);
        Assert.True(webSocket.State is WebSocketState.Open or
            WebSocketState.CloseReceived);
    }

    [Fact]
    public async Task OmittedPorts_ServeLegacyRoutesOnSharedHttpListener()
    {
        await using var host = await StartRouteTestHostWithRetryAsync(true);
        var ports = host.Ports;
        var port = ports.PublicPort;
        using var client = new HttpClient();

        Assert.Single(ports.ListenerPorts);
        await AssertStatusAsync(client, port, "/api/pools", HttpStatusCode.OK);
        await AssertStatusAsync(client, port, "/api/admin/status",
            HttpStatusCode.OK);
        await AssertStatusAsync(client, port, "/metrics", HttpStatusCode.OK);
        await AssertStatusAsync(client, port, "/notifications",
            HttpStatusCode.OK);
    }

    [Fact]
    public async Task DedicatedTlsListeners_UseHttpsAndRetainRouteIsolation()
    {
        using var certificate = CreateServerCertificate();
        await using var host = await StartRouteTestHostWithRetryAsync(false,
            certificate);
        var ports = host.Ports;

        // Windows Schannel on the validation host cannot acquire credentials for
        // an ephemeral self-signed test certificate. Host startup above still proves
        // that all HTTPS listeners were configured; Linux CI performs the handshakes.
        if(OperatingSystem.IsWindows())
            return;

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var client = new HttpClient(handler);

        await AssertStatusAsync(client, ports.PublicPort, "/api/pools",
            HttpStatusCode.OK, true);
        await AssertStatusAsync(client, ports.AdminPort, "/api/admin/status",
            HttpStatusCode.OK, true);
        await AssertStatusAsync(client, ports.MetricsPort, "/metrics",
            HttpStatusCode.OK, true);
        await AssertStatusAsync(client, ports.PublicPort, "/metrics",
            HttpStatusCode.NotFound, true);
        await AssertStatusAsync(client, ports.AdminPort, "/api/pools",
            HttpStatusCode.NotFound, true);
        await AssertStatusAsync(client, ports.MetricsPort, "/api/pools",
            HttpStatusCode.NotFound, true);
    }

    [Fact]
    public void ListenerConfiguration_AppliesTlsSetupToEveryUniquePort()
    {
        var ports = new Program.ApiEndpointPorts(4000, 4001, 4002);
        var configuredPorts = new List<int>();
        var options = new KestrelServerOptions();

        Program.ConfigureApiListeners(options, IPAddress.Loopback, ports,
            listenOptions => configuredPorts.Add(listenOptions.IPEndPoint.Port));

        Assert.Equal(ports.ListenerPorts, configuredPorts);
    }

    [Theory]
    [InlineData("/api/pools", true)]
    [InlineData("/notifications", true)]
    [InlineData("/api/admin/status", false)]
    [InlineData("/API/ADMIN/stats/gc", false)]
    [InlineData("/metrics", false)]
    [InlineData("/METRICS", false)]
    [InlineData("/metrics/", false)]
    [InlineData("/metrics/custom", false)]
    [InlineData("/api/administrator", true)]
    [InlineData("/metrics-export", true)]
    public void PublicCorsMatching_IsCaseInsensitiveAndSegmentBounded(
        string path, bool expected) =>
        Assert.Equal(expected, Program.ShouldApplyPublicCors(path));

    [Theory]
    [InlineData("/metrics", true)]
    [InlineData("/METRICS/custom", true)]
    [InlineData("/metrics-export", false)]
    [InlineData("/api/pools", false)]
    public void MetricsMatching_IsCaseInsensitiveAndSegmentBounded(
        string path, bool expected) =>
        Assert.Equal(expected, Program.IsMetricsRequest(path));

    [Theory]
    [InlineData("/api/admin", true)]
    [InlineData("/api/admin/status", true)]
    [InlineData("/API/ADMIN/stats/gc", true)]
    [InlineData("/metrics", true)]
    [InlineData("/metrics/", true)]
    [InlineData("/METRICS/custom", true)]
    [InlineData("/api/administrator", false)]
    [InlineData("/metrics-export", false)]
    [InlineData("/api/pools", false)]
    public void ProtectedResourcePolicyMatching_IsCaseInsensitiveAndSegmentBounded(
        string path, bool expected) =>
        Assert.Equal(expected,
            ProtectedRouteResourcePolicyMiddleware.IsProtectedRequest(path));

    [Theory]
    [InlineData("GET", true)]
    [InlineData("get", false)]
    [InlineData("HEAD", true)]
    [InlineData("head", false)]
    [InlineData("OPTIONS", false)]
    [InlineData("POST", false)]
    [InlineData("PUT", false)]
    [InlineData("DELETE", false)]
    public void MetricsMethodPolicy_AllowsOnlyGetAndHead(string method,
        bool expected) =>
        Assert.Equal(expected,
            MetricsMethodPolicyMiddleware.IsAllowedMethod(method));

    [Fact]
    public async Task MetricsMethodPolicy_RejectsUnsupportedMethodBeforeExporter()
    {
        var nextCalled = false;
        var middleware = new MetricsMethodPolicyMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Options;
        context.Request.Path = "/metrics";

        await middleware.Invoke(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status405MethodNotAllowed,
            context.Response.StatusCode);
        Assert.Equal(MetricsMethodPolicyMiddleware.AllowedMethods,
            context.Response.Headers.Allow);
        Assert.Equal(0, context.Response.ContentLength);
    }

    [Theory]
    [InlineData("GET", "/api/admin", false)]
    [InlineData("POST", "/api/admin/stats/gc", false)]
    [InlineData("PUT", "/API/ADMIN/payment/processing/disable", false)]
    [InlineData("POST", "/api/administrator", false)]
    [InlineData("POST", "/api/administer", false)]
    [InlineData("GET", "/metrics", false)]
    [InlineData("HEAD", "/metrics", false)]
    [InlineData("get", "/metrics", false)]
    [InlineData("head", "/metrics", false)]
    [InlineData("POST", "/metrics", false)]
    [InlineData("GET", "/metrics/custom", false)]
    [InlineData("GET", "/notifications", true)]
    [InlineData("POST", "/notifications", true)]
    [InlineData("GET", "/notifications/client", false)]
    public void RateLimitDependencyWhitelist_ExemptsOnlyWebSocketNotifications(
        string method, string path, bool expected)
    {
        var processor = new TestRateLimitProcessor(new RateLimitOptions
        {
            EndpointWhitelist = Program.CreateIpRateLimitEndpointWhitelist(),
        });

        var actual = processor.IsWhitelisted(new ClientRequestIdentity
        {
            ClientIp = "203.0.113.10",
            HttpVerb = method,
            Path = path,
        });

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("GET", "/metrics", false)]
    [InlineData("HEAD", "/metrics", false)]
    [InlineData("GET", "/METRICS", false)]
    [InlineData("HEAD", "/metrics/", false)]
    [InlineData("get", "/metrics", true)]
    [InlineData("head", "/metrics", true)]
    [InlineData("Get", "/metrics", true)]
    [InlineData("HeAd", "/metrics", true)]
    [InlineData("OPTIONS", "/metrics", true)]
    [InlineData("POST", "/metrics", true)]
    [InlineData("GET", "/metrics/custom", true)]
    [InlineData("GET", "/api/pools", true)]
    public void RateLimitPipeline_ExemptsOnlyExactSupportedMetricsScrapes(
        string method, string path, bool expected) =>
        Assert.Equal(expected,
            Program.ShouldApplyIpRateLimiting(path, method));

    [Theory]
    [InlineData("/api/admin/status", "/api/admin", true)]
    [InlineData("/API/ADMIN/stats/gc", "/api/admin", true)]
    [InlineData("/Api/Admin/payment/processing/disable", "/api/admin", true)]
    [InlineData("/metrics", "/metrics", true)]
    [InlineData("/METRICS", "/metrics", true)]
    [InlineData("/api/administrator", "/api/admin", false)]
    [InlineData("/metrics-export", "/metrics", false)]
    public async Task WhitelistMatching_IsCaseInsensitiveAndSegmentBounded(
        string path, string protectedLocation, bool protectedRoute)
    {
        var nextCalled = false;
        var middleware = new IPAccessWhitelistMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new[] { protectedLocation },
            new[] { IPAddress.Loopback },
            false);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");

        await middleware.Invoke(context);

        Assert.Equal(!protectedRoute, nextCalled);
        Assert.Equal(protectedRoute ? StatusCodes.Status403Forbidden :
            StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("203.0.113.10", "::ffff:203.0.113.10", true)]
    [InlineData("::ffff:203.0.113.10", "203.0.113.10", true)]
    [InlineData("203.0.113.10", "198.51.100.20", false)]
    [InlineData("203.0.113.10", "2001:db8::10", false)]
    [InlineData("2001:db8::10", "2001:db8::11", false)]
    public async Task WhitelistMatching_NormalizesMappedClientAddresses(
        string whitelistAddress, string remoteAddress, bool expectedAuthorized)
    {
        var nextCalled = false;
        var middleware = new IPAccessWhitelistMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new[] { "/api/admin" },
            new[] { IPAddress.Parse(whitelistAddress) },
            false);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/admin/status";
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);

        await middleware.Invoke(context);

        Assert.Equal(expectedAuthorized, nextCalled);
        Assert.Equal(expectedAuthorized ? StatusCodes.Status200OK :
            StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task ApiPipeline_RejectsNonWhitelistedAdminClientBeforeAuthentication()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCors();
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        var nextCalled = false;
        var ports = new Program.ApiEndpointPorts(4000, 4000, 4000);

        Program.ConfigureApiPipeline(app, ports,
            new[] { "198.51.100.10" }, null,
            AdminApiCredential.Create(TestAdminToken), false,
            afterAccessControl: pipeline => pipeline.Run(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }));

        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        context.Connection.LocalPort = ports.AdminPort;
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        context.Request.Path = "/api/admin/status";
        context.Response.Body = new MemoryStream();

        await app.Build()(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden,
            context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey(
            "WWW-Authenticate"));
        AssertProtectedResponseHeaders(context.Response);
    }

    [Fact]
    public async Task ApiPipeline_RejectsNonWhitelistedMetricsClientBeforeMethodPolicy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCors();
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        var nextCalled = false;
        var ports = new Program.ApiEndpointPorts(4000, 4000, 4000);

        Program.ConfigureApiPipeline(app, ports, null,
            new[] { "198.51.100.10" },
            AdminApiCredential.Create(TestAdminToken), false,
            afterAccessControl: pipeline => pipeline.Run(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }));

        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        context.Connection.LocalPort = ports.MetricsPort;
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        context.Request.Method = HttpMethods.Options;
        context.Request.Path = "/metrics";
        context.Response.Body = new MemoryStream();

        await app.Build()(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden,
            context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Allow"));
        AssertProtectedResponseHeaders(context.Response);
    }

    [Fact]
    public async Task ApiPipeline_CorsPreflightShortCircuitsBeforeAfterAccessControlMiddleware()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCors();
        services.AddResponseCompression();
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        var endpointReached = false;
        var ports = new Program.ApiEndpointPorts(4000, 4000, 4000);

        Program.ConfigureApiPipeline(app, ports, null, null,
            AdminApiCredential.Create(TestAdminToken), false,
            afterAccessControl: pipeline =>
            {
                pipeline.UseResponseCompression();
                pipeline.Run(_ =>
                {
                    endpointReached = true;
                    return Task.CompletedTask;
                });
            });

        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        context.Connection.LocalPort = ports.PublicPort;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Method = HttpMethods.Options;
        context.Request.Path = "/api/pools";
        context.Request.Headers.Origin = "https://dashboard.example";
        context.Request.Headers.AccessControlRequestMethod = HttpMethods.Get;
        context.Request.Headers.AcceptEncoding = "gzip";
        context.Response.Body = new MemoryStream();

        await app.Build()(context);

        Assert.False(endpointReached);
        Assert.Equal(StatusCodes.Status204NoContent,
            context.Response.StatusCode);
        Assert.Equal("*", context.Response.Headers.AccessControlAllowOrigin);
        Assert.False(context.Response.Headers.ContainsKey("Content-Encoding"));
    }

    [Theory]
    [InlineData("get")]
    [InlineData("head")]
    [InlineData("Get")]
    [InlineData("HeAd")]
    public async Task ApiPipeline_RateLimitsRejectedMetricsMethodLookalikes(
        string method)
    {
        var rateLimiting = new ApiRateLimitConfig
        {
            // Do not exempt the loopback client used by this real Kestrel test.
            IpWhitelist = new[] { "192.0.2.1" },
            Rules = new[]
            {
                new RateLimitRule
                {
                    Endpoint = "*",
                    Period = "1m",
                    Limit = 1,
                },
            },
        };
        await using var host = await StartRouteTestHostWithRetryAsync(true,
            rateLimiting: rateLimiting);
        using var client = new HttpClient();
        // Exact supported tokens bypass throttling even when repeated. The
        // exporter accepts its canonical path with or without a trailing slash.
        foreach(var metricsPath in new[] { "/metrics", "/metrics/", "/METRICS" })
        {
            var metricsUri =
                $"http://127.0.0.1:{host.Ports.MetricsPort}{metricsPath}";
            for(var attempt = 0; attempt < 2; attempt++)
            {
                using var getResponse = await client.GetAsync(metricsUri);
                Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

                using var headRequest = new HttpRequestMessage(HttpMethod.Head,
                    metricsUri);
                using var headResponse = await client.SendAsync(headRequest);
                Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
            }
        }

        var rejected = await SendRawHttpRequestAsync(
            host.Ports.MetricsPort, method, "/metrics");
        Assert.StartsWith("HTTP/1.1 405 Method Not Allowed\r\n", rejected);
        AssertRawProtectedResponseHeaders(rejected);

        var throttled = await SendRawHttpRequestAsync(
            host.Ports.MetricsPort, method, "/metrics");
        Assert.StartsWith("HTTP/1.1 429 Too Many Requests\r\n", throttled);
        AssertRawProtectedResponseHeaders(throttled);
    }

    [Fact]
    public async Task ApiPipeline_ExceptionResponseRetainsProtectedHeaders()
    {
        await using var host = await StartRouteTestHostWithRetryAsync(true,
            enableExceptionHandling: true, throwApiException: true);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://127.0.0.1:{host.Ports.AdminPort}/api/admin/status");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestAdminToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertProtectedResponseHeaders(response);
    }

    private static Program.ApiEndpointPorts CreateEndpointPorts()
    {
        for(var attempt = 0; attempt < ListenerStartAttempts; attempt++)
        {
            var ports = Enumerable.Range(0, 3)
                .Select(_ => GetFreeTcpPort())
                .Distinct()
                .ToArray();
            if(ports.Length == 3)
                return new Program.ApiEndpointPorts(ports[0], ports[1],
                    ports[2]);
        }

        throw new InvalidOperationException(
            "Unable to reserve three distinct listener test ports");
    }

    private static ClusterConfig CreateValidRecoveryConfig(ApiConfig api) =>
        new()
        {
            Api = api,
            Logging = new ClusterLoggingConfig(),
            PaymentProcessing = new ClusterPaymentProcessingConfig(),
            Persistence = new PersistenceConfig
            {
                Postgres = new PostgresConfig
                {
                    Host = "127.0.0.1",
                    Port = 5432,
                    Database = "miningcore",
                    User = "miningcore",
                },
            },
            Pools = new[]
            {
                new PoolConfig
                {
                    Id = "recovery-pool",
                    Coin = "bitcoin",
                    Enabled = true,
                    EnableInternalStratum = false,
                    Address = "recovery-wallet",
                    Ports = new Dictionary<int, PoolEndpoint>(),
                    Daemons = new[]
                    {
                        new DaemonEndpointConfig
                        {
                            Host = "127.0.0.1",
                            Port = 1,
                        },
                    },
                },
            },
        };

    private static JObject CreateRecoveryConfigDocument(bool apiEnabled) =>
        JObject.Parse(SerializeConfig(CreateValidRecoveryConfig(new ApiConfig
        {
            Enabled = apiEnabled,
            Port = 4000,
        })));

    private static string CreateRawConfigurationWithRootProperties(
        JObject source, params (string Name, string Json)[] properties)
    {
        var remainder = (JObject) source.DeepClone();
        foreach(var propertyName in properties.Select(x => x.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach(var property in remainder.Properties().Where(property =>
                        property.Name.Equals(propertyName,
                            StringComparison.OrdinalIgnoreCase)).ToArray())
                property.Remove();
        }

        var injected = string.Join(",", properties.Select(property =>
            $"{JsonConvert.ToString(property.Name)}:{property.Json}"));
        var remainderJson = remainder.ToString(Formatting.None);
        var existing = remainderJson.Length > 2
            ? remainderJson[1..^1]
            : string.Empty;

        return $"{{{injected}{(existing.Length > 0 ? "," : string.Empty)}{existing}}}";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdministrativeRoutes_RequireBearerToken(bool shared)
    {
        await using var host = await StartRouteTestHostWithRetryAsync(shared);
        using var client = new HttpClient();
        var uri = $"http://127.0.0.1:{host.Ports.AdminPort}/api/admin/status";

        using var missing = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        AssertProtectedResponseHeaders(missing);
        Assert.Equal("Bearer", missing.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("no-store", missing.Headers.CacheControl?.ToString());

        using var wrongRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        wrongRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", new string('b', 64));
        using var wrong = await client.SendAsync(wrongRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        AssertProtectedResponseHeaders(wrong);

        using var validRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        validRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestAdminToken);
        using var valid = await client.SendAsync(validRequest);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        AssertProtectedResponseHeaders(valid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAdministrativeCredential_FailsClosedWithoutBlockingPublicApi(
        bool shared)
    {
        await using var host = await StartRouteTestHostWithRetryAsync(shared,
            null, false);
        using var client = new HttpClient();

        await AssertStatusAsync(client, host.Ports.PublicPort, "/api/pools",
            HttpStatusCode.OK);

        using var response = await client.GetAsync(
            $"http://127.0.0.1:{host.Ports.AdminPort}/api/admin/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertProtectedResponseHeaders(response);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorsAndProtectedResourcePolicies_WorkOnDedicatedAndSharedListeners(
        bool shared)
    {
        await using var host = await StartRouteTestHostWithRetryAsync(shared);
        using var client = new HttpClient();

        using var publicRequest = CreatePreflightRequest(
            $"http://127.0.0.1:{host.Ports.PublicPort}/api/pools");
        using var publicResponse = await client.SendAsync(publicRequest);
        Assert.Equal(HttpStatusCode.NoContent, publicResponse.StatusCode);
        Assert.Contains("*", publicResponse.Headers.GetValues(
            "Access-Control-Allow-Origin"));

        using var adminSuccessRequest = new HttpRequestMessage(HttpMethod.Get,
            $"http://127.0.0.1:{host.Ports.AdminPort}/api/admin/status");
        adminSuccessRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestAdminToken);
        using var adminSuccessResponse = await client.SendAsync(adminSuccessRequest);
        Assert.Equal(HttpStatusCode.OK, adminSuccessResponse.StatusCode);
        AssertProtectedResponseHeaders(adminSuccessResponse);

        using var adminRequest = CreatePreflightRequest(
            $"http://127.0.0.1:{host.Ports.AdminPort}/api/admin/status");
        using var adminResponse = await client.SendAsync(adminRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, adminResponse.StatusCode);
        AssertProtectedResponseHeaders(adminResponse);
        Assert.False(adminResponse.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(adminResponse.Headers.Contains("Access-Control-Allow-Headers"));

        using var metricsBrowserRequest = new HttpRequestMessage(HttpMethod.Get,
            $"http://127.0.0.1:{host.Ports.MetricsPort}/metrics");
        metricsBrowserRequest.Headers.Add("Origin", "https://dashboard.example");
        using var metricsBrowserResponse = await client.SendAsync(metricsBrowserRequest);
        Assert.Equal(HttpStatusCode.OK, metricsBrowserResponse.StatusCode);
        AssertProtectedResponseHeaders(metricsBrowserResponse);
        Assert.False(metricsBrowserResponse.Headers.Contains(
            "Access-Control-Allow-Origin"));

        using var metricsPreflightRequest = CreatePreflightRequest(
            $"http://127.0.0.1:{host.Ports.MetricsPort}/METRICS");
        using var metricsPreflightResponse = await client.SendAsync(
            metricsPreflightRequest);
        Assert.Equal(HttpStatusCode.MethodNotAllowed,
            metricsPreflightResponse.StatusCode);
        AssertProtectedResponseHeaders(metricsPreflightResponse);
        Assert.Equal(new[] { "GET", "HEAD" },
            GetAllowedMethods(metricsPreflightResponse));
        Assert.Empty(await metricsPreflightResponse.Content.ReadAsByteArrayAsync());
        Assert.False(metricsPreflightResponse.Headers.Contains(
            "Access-Control-Allow-Origin"));
        Assert.False(metricsPreflightResponse.Headers.Contains(
            "Access-Control-Allow-Headers"));

        using var scrapeResponse = await client.GetAsync(
            $"http://127.0.0.1:{host.Ports.MetricsPort}/metrics");
        Assert.Equal(HttpStatusCode.OK, scrapeResponse.StatusCode);
        AssertProtectedResponseHeaders(scrapeResponse);
        Assert.Equal("text/plain", scrapeResponse.Content.Headers.ContentType?.MediaType);
        var scrapeBody = await scrapeResponse.Content.ReadAsStringAsync();
        Assert.Contains(scrapeBody.Split('\n'), line =>
            line.TrimEnd('\r').Equals(
                "miningcore_listener_test_scrapes_total 1",
                StringComparison.Ordinal));

        using var headRequest = new HttpRequestMessage(HttpMethod.Head,
            $"http://127.0.0.1:{host.Ports.MetricsPort}/metrics");
        using var headResponse = await client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        AssertProtectedResponseHeaders(headResponse);
        Assert.Equal("text/plain", headResponse.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync());

        using var unsupportedRequest = new HttpRequestMessage(HttpMethod.Post,
            $"http://127.0.0.1:{host.Ports.MetricsPort}/metrics");
        using var unsupportedResponse = await client.SendAsync(unsupportedRequest);
        Assert.Equal(HttpStatusCode.MethodNotAllowed,
            unsupportedResponse.StatusCode);
        AssertProtectedResponseHeaders(unsupportedResponse);
        Assert.Equal(new[] { "GET", "HEAD" },
            GetAllowedMethods(unsupportedResponse));
        Assert.Empty(await unsupportedResponse.Content.ReadAsByteArrayAsync());

        foreach(var method in new[] { "get", "head" })
        {
            // HttpClient normalizes known method tokens to uppercase. Send the
            // request over a raw connection so Kestrel receives the lowercase
            // token that the RFC requires the application to distinguish.
            var lowerCaseResponse = await SendRawHttpRequestAsync(
                host.Ports.MetricsPort, method, "/metrics");
            var bodyStart = lowerCaseResponse.IndexOf("\r\n\r\n",
                StringComparison.Ordinal);

            Assert.StartsWith("HTTP/1.1 405 Method Not Allowed\r\n",
                lowerCaseResponse);
            Assert.Contains("\r\nAllow: GET, HEAD\r\n", lowerCaseResponse,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\r\nContent-Length: 0\r\n", lowerCaseResponse,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"\r\n{ProtectedRouteResourcePolicyMiddleware.HeaderName}: " +
                $"{ProtectedRouteResourcePolicyMiddleware.HeaderValue}\r\n",
                lowerCaseResponse, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\r\nCache-Control: no-store\r\n", lowerCaseResponse,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"\r\n{ProtectedRouteResourcePolicyMiddleware.ContentTypeOptionsHeaderName}: " +
                $"{ProtectedRouteResourcePolicyMiddleware.ContentTypeOptionsHeaderValue}\r\n",
                lowerCaseResponse, StringComparison.OrdinalIgnoreCase);
            Assert.True(bodyStart >= 0);
            Assert.Empty(lowerCaseResponse[(bodyStart + 4)..]);
        }

        if(!shared)
        {
            using var wrongListenerResponse = await client.GetAsync(
                $"http://127.0.0.1:{host.Ports.PublicPort}/metrics");
            Assert.Equal(HttpStatusCode.NotFound,
                wrongListenerResponse.StatusCode);
            AssertProtectedResponseHeaders(wrongListenerResponse);
        }

        using var lookalikeRequest = CreatePreflightRequest(
            $"http://127.0.0.1:{host.Ports.PublicPort}/metrics-export");
        using var lookalikeResponse = await client.SendAsync(lookalikeRequest);
        Assert.Equal(HttpStatusCode.NoContent, lookalikeResponse.StatusCode);
        Assert.False(lookalikeResponse.Headers.Contains(
            ProtectedRouteResourcePolicyMiddleware.HeaderName));
        Assert.False(lookalikeResponse.Headers.Contains(
            ProtectedRouteResourcePolicyMiddleware.ContentTypeOptionsHeaderName));
        Assert.Null(lookalikeResponse.Headers.CacheControl);
        Assert.Contains("*", lookalikeResponse.Headers.GetValues(
            "Access-Control-Allow-Origin"));
    }

    private static string SerializeConfig(ClusterConfig config) =>
        JsonConvert.SerializeObject(config, new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
        });

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint) listener.LocalEndpoint).Port;
    }

    private static int GetFreeTcpPort(IPAddress address, bool dualMode)
    {
        using var listener = new TcpListener(address, 0);
        if(address.AddressFamily == AddressFamily.InterNetworkV6)
            listener.Server.DualMode = dualMode;
        listener.Start();
        return ((IPEndPoint) listener.LocalEndpoint).Port;
    }

    private static async Task<(IHost Host, int Port)>
        StartBindingTestHostWithRetryAsync(IPAddress address)
    {
        Exception lastError = null;

        for(var attempt = 0; attempt < ListenerStartAttempts; attempt++)
        {
            var port = GetFreeTcpPort(address,
                address.Equals(IPAddress.IPv6Any));
            try
            {
                return (await StartBindingTestHostAsync(address, port), port);
            }
            catch(Exception ex) when(IsAddressInUse(ex))
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            "Unable to start the listener binding test after bounded port retries",
            lastError);
    }

    private static async Task<IHost> StartBindingTestHostAsync(
        IPAddress address, int port)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHostDefaults(builder => builder
                .UseKestrel(options => options.Listen(address, port))
                .Configure(app => app.Run(context =>
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    return Task.CompletedTask;
                })))
            .Build();

        try
        {
            await host.StartAsync();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static async Task<RunningRouteTestHost>
        StartRouteTestHostWithRetryAsync(bool shared = false,
            X509Certificate2 certificate = null,
            bool configureAdminCredential = true,
            ApiRateLimitConfig rateLimiting = null,
            bool enableExceptionHandling = false,
            bool throwApiException = false)
    {
        Exception lastError = null;

        for(var attempt = 0; attempt < ListenerStartAttempts; attempt++)
        {
            var ports = shared
                ? Program.ResolveApiEndpointPorts(new ApiConfig
                {
                    Port = GetFreeTcpPort(),
                })
                : CreateEndpointPorts();

            try
            {
                var host = await StartRouteTestHostAsync(ports, certificate,
                    configureAdminCredential, rateLimiting,
                    enableExceptionHandling, throwApiException);
                return new RunningRouteTestHost(host, ports);
            }
            catch(Exception ex) when(IsAddressInUse(ex))
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            "Unable to start the API listener test after bounded port retries",
            lastError);
    }

    private static async Task<IHost> StartRouteTestHostAsync(
        Program.ApiEndpointPorts ports, X509Certificate2 certificate = null,
        bool configureAdminCredential = true,
        ApiRateLimitConfig rateLimiting = null,
        bool enableExceptionHandling = false,
        bool throwApiException = false)
    {
        var adminCredential = AdminApiCredential.Create(
            configureAdminCredential ? TestAdminToken : null);
        // The complete suite exercises process-global production metrics in
        // parallel. Give this real exporter its own registry so unrelated
        // collectors cannot make an integration scrape intermittently fail.
        var registry = Metrics.NewCustomRegistry();
        Metrics.WithCustomRegistry(registry)
            .CreateCounter("miningcore_listener_test_scrapes_total",
                "Metric exposed so the isolated route-test registry serializes a scrape body")
            .Inc();
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHostDefaults(builder => builder
                .ConfigureServices(services =>
                {
                    services.AddCors();
                    if(rateLimiting != null)
                    {
                        Program.AddApiRateLimiting(services, new ApiConfig
                        {
                            RateLimiting = rateLimiting,
                        });
                    }
                })
                .UseKestrel(options => Program.ConfigureApiListeners(options,
                    IPAddress.Loopback, ports, listenOptions =>
                    {
                        if(certificate != null)
                            listenOptions.UseHttps(certificate);
                    }))
                .Configure(app =>
                {
                    Program.ConfigureApiPipeline(app, ports, null, null,
                        adminCredential, false,
                        new Program.ApiPipelineOptions(
                            EnableIpRateLimiting: rateLimiting != null,
                            EnableExceptionHandling: enableExceptionHandling),
                        afterAccessControl: pipeline =>
                        {
                            pipeline.UseWebSockets();
                            pipeline.UseMetricServer(
                                settings => settings.Registry = registry,
                                Program.MetricsRoutePrefix);
                            pipeline.Run(async context =>
                            {
                                if(throwApiException)
                                    throw new ApiException("test exception",
                                        HttpStatusCode.BadRequest);

                                if(context.Request.Path == "/notifications" &&
                                    context.WebSockets.IsWebSocketRequest)
                                {
                                    using var socket =
                                        await context.WebSockets.AcceptWebSocketAsync();
                                    await socket.CloseOutputAsync(
                                        WebSocketCloseStatus.NormalClosure,
                                        "listener test complete",
                                        context.RequestAborted);
                                    return;
                                }

                                if(context.Request.Path == "/api/pools" ||
                                    context.Request.Path == "/notifications" ||
                                    context.Request.Path == "/api/admin/status")
                                {
                                    if(context.Request.Path == "/api/admin/status")
                                    {
                                        // Attempt to weaken the policy downstream. The
                                        // first-in-pipeline OnStarting callback must
                                        // restore same-origin on the wire response.
                                        context.Response.Headers[
                                            ProtectedRouteResourcePolicyMiddleware.HeaderName] =
                                            "cross-origin";
                                        context.Response.Headers.CacheControl =
                                            "public, max-age=3600";
                                        context.Response.Headers[
                                            ProtectedRouteResourcePolicyMiddleware.ContentTypeOptionsHeaderName] =
                                            "off";
                                    }

                                    context.Response.StatusCode = StatusCodes.Status200OK;
                                    return;
                                }

                                context.Response.StatusCode =
                                    StatusCodes.Status404NotFound;
                            });
                        });
                }))
            .Build();

        try
        {
            await host.StartAsync();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static bool IsAddressInUse(Exception exception)
    {
        while(exception != null)
        {
            if(exception is AddressInUseException ||
                exception is SocketException
                {
                    SocketErrorCode: SocketError.AddressAlreadyInUse,
                })
                return true;

            exception = exception.InnerException;
        }

        return false;
    }

    private static void AssertRawProtectedResponseHeaders(string response)
    {
        Assert.Contains($"\r\n{ProtectedRouteResourcePolicyMiddleware.HeaderName}: " +
            $"{ProtectedRouteResourcePolicyMiddleware.HeaderValue}\r\n",
            response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\r\nCache-Control: no-store\r\n", response,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"\r\n{ProtectedRouteResourcePolicyMiddleware.ContentTypeOptionsHeaderName}: " +
            $"{ProtectedRouteResourcePolicyMiddleware.ContentTypeOptionsHeaderValue}\r\n",
            response, StringComparison.OrdinalIgnoreCase);
    }

    private const int ListenerStartAttempts = 5;

    private sealed class TestRateLimitProcessor(RateLimitOptions options) :
        RateLimitProcessor(options);

    private sealed class RunningRouteTestHost(IHost host,
        Program.ApiEndpointPorts ports) : IAsyncDisposable
    {
        public Program.ApiEndpointPorts Ports { get; } = ports;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await host.StopAsync();
            }
            finally
            {
                host.Dispose();
            }
        }
    }

    private static async Task AssertStatusAsync(HttpClient client, int port,
        string path, HttpStatusCode expected, bool tls = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http{(tls ? "s" : string.Empty)}://127.0.0.1:{port}{path}");
        if(AdminApiAuthenticationMiddleware.IsAdminRequest(path))
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", TestAdminToken);
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }

    private static void AssertProtectedResponseHeaders(
        HttpResponseMessage response)
    {
        Assert.Equal(
            new[] { ProtectedRouteResourcePolicyMiddleware.HeaderValue },
            response.Headers.GetValues(
                ProtectedRouteResourcePolicyMiddleware.HeaderName));
        Assert.Equal(ProtectedRouteResourcePolicyMiddleware.CacheControlValue,
            response.Headers.CacheControl?.ToString());
        Assert.Equal(
            new[]
            {
                ProtectedRouteResourcePolicyMiddleware.ContentTypeOptionsHeaderValue,
            },
            response.Headers.GetValues(
                ProtectedRouteResourcePolicyMiddleware.ContentTypeOptionsHeaderName));
    }

    private static void AssertProtectedResponseHeaders(HttpResponse response)
    {
        Assert.Equal(ProtectedRouteResourcePolicyMiddleware.HeaderValue,
            response.Headers[
                ProtectedRouteResourcePolicyMiddleware.HeaderName]);
        Assert.Equal(ProtectedRouteResourcePolicyMiddleware.CacheControlValue,
            response.Headers.CacheControl);
        Assert.Equal(
            ProtectedRouteResourcePolicyMiddleware.ContentTypeOptionsHeaderValue,
            response.Headers[
                ProtectedRouteResourcePolicyMiddleware.ContentTypeOptionsHeaderName]);
    }

    private static string[] GetAllowedMethods(HttpResponseMessage response)
    {
        if(!response.Headers.TryGetValues("Allow", out var values) &&
            !response.Content.Headers.TryGetValues("Allow", out values))
            return Array.Empty<string>();

        return values.SelectMany(value => value.Split(','))
            .Select(value => value.Trim())
            .ToArray();
    }

    private static HttpRequestMessage CreatePreflightRequest(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, uri);
        request.Headers.Add("Origin", "https://dashboard.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return request;
    }

    private static async Task<string> SendRawHttpRequestAsync(int port,
        string method, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            $"{method} {path} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{port}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(request, timeout.Token);
        await stream.FlushAsync(timeout.Token);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync(timeout.Token);
    }

    private const string TestAdminToken =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static X509Certificate2 CreateServerCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature |
                X509KeyUsageFlags.KeyEncipherment,
                false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));
    }
}
