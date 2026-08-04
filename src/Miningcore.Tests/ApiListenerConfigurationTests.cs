using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miningcore.Api.Middlewares;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Stratum;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
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
        api.Port = GetFreeTcpPort();
        ports = Program.ResolveApiEndpointPorts(api);
        using var host = await StartRouteTestHostAsync(ports);
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
            warnings.Any(message => message.Contains("/api/admin")));
        Assert.Equal(!metricsPort.HasValue,
            warnings.Any(message => message.Contains("/metrics")));
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
        var port = GetFreeTcpPort(apiAddress,
            apiAddress.Equals(IPAddress.IPv6Any));

        Assert.True(Program.ListenAddressesOverlap(apiAddress,
            stratumAddress));

        using var host = await StartBindingTestHostAsync(apiAddress, port);
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

            var config = Program.ReadAndValidateConfig(configFile, true);
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
            Assert.Null(config.Api.MetricsPort);
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
            Assert.False(recoveryConfig.Api.Enabled);
            Assert.Null(recoveryConfig.Api.MetricsPort);
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
        var ports = CreateEndpointPorts();
        using var host = await StartRouteTestHostAsync(ports);
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
        var port = GetFreeTcpPort();
        var ports = Program.ResolveApiEndpointPorts(new ApiConfig
        {
            Port = port,
        });
        using var host = await StartRouteTestHostAsync(ports);
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
        var ports = CreateEndpointPorts();
        using var certificate = CreateServerCertificate();
        using var host = await StartRouteTestHostAsync(ports, certificate);

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

    private static Program.ApiEndpointPorts CreateEndpointPorts()
    {
        var ports = Enumerable.Range(0, 3)
            .Select(_ => GetFreeTcpPort())
            .Distinct()
            .ToArray();
        if(ports.Length != 3)
            return CreateEndpointPorts();

        return new Program.ApiEndpointPorts(ports[0], ports[1], ports[2]);
    }

    private static ClusterConfig CreateValidRecoveryConfig(ApiConfig api) =>
        new()
        {
            Api = api,
            Logging = new ClusterLoggingConfig(),
            PaymentProcessing = new ClusterPaymentProcessingConfig(),
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

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> StartRouteTestHostAsync(
        Program.ApiEndpointPorts ports, X509Certificate2 certificate = null)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHostDefaults(builder => builder
                .UseKestrel(options => Program.ConfigureApiListeners(options,
                    IPAddress.Loopback, ports, listenOptions =>
                    {
                        if(certificate != null)
                            listenOptions.UseHttps(certificate);
                    }))
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        if(!Program.IsApiRequestAllowed(
                            context.Connection.LocalPort,
                            context.Request.Path, ports))
                        {
                            context.Response.StatusCode =
                                StatusCodes.Status404NotFound;
                            return;
                        }

                        await next();
                    });
                    app.UseWebSockets();
                    app.Run(async context =>
                    {
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
                            context.Request.Path == "/api/admin/status" ||
                            context.Request.Path == "/metrics")
                        {
                            context.Response.StatusCode = StatusCodes.Status200OK;
                            return;
                        }

                        context.Response.StatusCode =
                            StatusCodes.Status404NotFound;
                    });
                }))
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task AssertStatusAsync(HttpClient client, int port,
        string path, HttpStatusCode expected, bool tls = false)
    {
        using var response = await client.GetAsync(
            $"http{(tls ? "s" : string.Empty)}://127.0.0.1:{port}{path}");
        Assert.Equal(expected, response.StatusCode);
    }

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
