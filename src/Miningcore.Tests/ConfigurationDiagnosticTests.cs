using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests;

public class ConfigurationDiagnosticTests
{
    private const string Secret = "ISSUE144_SYNTHETIC_SECRET";

    [Theory]
    [InlineData("-dc")]
    [InlineData("--dumpconfig")]
    public async Task PublicDump_LoadsFileAndExcludesSecretsFromAllOutput(string option)
    {
        var document = Fixture();
        var result = await RunDump(document.ToString(), option);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(string.Empty, result.Logs);
        Assert.DoesNotContain(Secret, result.Output);
        var output = JObject.Parse(result.Output);
        Assert.Equal(1, output["diagnosticFormatVersion"]);
        var config = (JObject) output["configuration"];
        Assert.Equal(5432, config["persistence"]["postgres"]["port"]);
        Assert.True(config["api"]["tls"]["enabled"].Value<bool>());
        Assert.Equal(600, config["paymentProcessing"]["interval"]);
        Assert.Equal(document["pools"].Count(), config["pools"].Count());
        Assert.Null(config["persistence"]["postgres"]["password"]);
        Assert.Null(config["pools"][0]["paymentProcessing"]["payoutSchemeConfig"]);
        Assert.Null(config["pools"][0]["paymentProcessing"]["walletPassword"]);
        Assert.Null(config["pools"][0]["daemons"][0]["apiKey"]);
        Assert.All(config.DescendantsAndSelf().OfType<JValue>()
                .Where(value => value.Type == JTokenType.String),
            value => Assert.Contains(value.Value<string>(),
                new[] { "PPLNS", "PROP", "SOLO", "PPS", "PPBS", "PPLNSBF", "Integrated", "IpTables", "[omitted]" }));
    }

    [Theory]
    [InlineData("syntax")]
    [InlineData("schema")]
    [InlineData("duplicate")]
    [InlineData("missing-file")]
    [InlineData("missing-option")]
    [InlineData("unknown-option")]
    [InlineData("invalid-flag-value")]
    public async Task PublicDump_FailuresDoNotEchoInputPathsOrExceptions(string failure)
    {
        var document = Fixture();
        var content = document.ToString();
        switch(failure)
        {
            case "syntax": content = "{\"" + Secret + "\": \"unterminated"; break;
            case "schema": document["api"]["port"] = Secret; content = document.ToString(); break;
            case "duplicate": document["pools"][0]["ID"] = Secret; content = document.ToString(); break;
        }
        var result = await RunDump(content,
            failure == "invalid-flag-value" ? "--dumpconfig=" + Secret : "--dumpconfig", failure);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal(string.Empty, result.Logs);
        Assert.Contains("Configuration dump failed.", result.Error);
        Assert.DoesNotContain(Secret, result.Error);
        Assert.DoesNotContain("Exception", result.Error);
    }

    [Fact]
    public void Projection_DoesNotMutateRuntimeConfigurationOrSerialization()
    {
        var settings = ConfigurationJson.CreateSerializerSettings();
        var config = JsonConvert.DeserializeObject<ClusterConfig>(Fixture().ToString(), settings);
        var before = JsonConvert.SerializeObject(config, settings);
        var extensions = config.Pools[0].Extra;
        var payout = config.Pools[0].PaymentProcessing.PayoutSchemeConfig;
        var output = Program.SerializeParsedConfig(config);
        Assert.DoesNotContain(Secret, output);
        Assert.Equal(before, JsonConvert.SerializeObject(config, settings));
        Assert.Contains(Secret, before);
        Assert.Same(extensions, config.Pools[0].Extra);
        Assert.Same(payout, config.Pools[0].PaymentProcessing.PayoutSchemeConfig);
        Assert.Equal(output, Program.SerializeParsedConfig(config));
    }

    [Fact]
    public void Projection_NeverTraversesUnknownObjectsOrRuntimeTemplates()
    {
        var bomb = new SerializationBomb();
        var config = new ClusterConfig
        {
            Pools = new[]
            {
                new PoolConfig
                {
                    Extra = new Dictionary<string, object> { [Secret] = bomb, ["enabled"] = bomb },
                    Template = new BitcoinTemplate { Extra = new Dictionary<string, object> { [Secret] = bomb } },
                    Daemons = new[] { new DaemonEndpointConfig { Extra = new Dictionary<string, object> { ["port"] = bomb } } },
                    PaymentProcessing = new PoolPaymentProcessingConfig
                    {
                        Extra = new Dictionary<string, object> { ["minimumPayment"] = bomb },
                        PayoutSchemeConfig = new JObject { [Secret] = new JArray(Secret, 1234, true) },
                    },
                },
            },
        };
        Assert.DoesNotContain(Secret, Program.SerializeParsedConfig(config));
        config.Pools = new PoolConfig[] { new UnknownPool() };
        Assert.Equal("[omitted]", JObject.Parse(Program.SerializeParsedConfig(config))
            ["configuration"]["pools"][0].Value<string>());
        Assert.Equal(JTokenType.Null, JObject.Parse(Program.SerializeParsedConfig(null))["configuration"].Type);
    }

    [Fact]
    public void Projection_PreservesReviewedStructureAndBoundsScalarOutput()
    {
        var config = new ClusterConfig
        {
            Logging = new ClusterLoggingConfig { GPDRCompliant = true },
            Banning = new ClusterBanningConfig { Manager = (BanManagerKind) int.MaxValue },
            Pools = new[]
            {
                null,
                new PoolConfig
                {
                    Ports = new Dictionary<int, PoolEndpoint>
                    {
                        [3334] = null,
                        [3333] = new() { Difficulty = double.NaN, VarDiff = new VarDiffConfig { MinDiff = 16 } },
                    },
                    Daemons = Array.Empty<DaemonEndpointConfig>(),
                },
            },
        };
        var output = JObject.Parse(Program.SerializeParsedConfig(config))["configuration"];
        Assert.True(output["logging"]["gpdrCompliant"].Value<bool>());
        Assert.Equal("[omitted]", output["banning"]["manager"]);
        Assert.Equal(JTokenType.Null, output["pools"][0].Type);
        var ports = output["pools"][1]["ports"];
        Assert.Equal("[omitted]", ports["3333"]["difficulty"]);
        Assert.Equal(16, ports["3333"]["varDiff"]["minDiff"].Value<double>());
        Assert.Equal(JTokenType.Null, ports["3334"].Type);
        Assert.Empty(output["pools"][1]["daemons"]);
    }

    [Fact]
    public async Task PublicHelp_DescribesDiagnosticCompatibilityBoundary()
    {
        var result = await RunDump(null, "--help", "help");
        Assert.Equal(0, result.ExitCode);
        var help = Regex.Replace(result.Output, @"\s+", " ");
        Assert.Contains("lossy diagnostic projection", help);
        Assert.Contains("not a configuration export", help);
    }

    private sealed class SerializationBomb
    {
        public string Value => throw new InvalidOperationException(Secret);
        public override string ToString() => throw new InvalidOperationException(Secret);
    }

    private sealed class UnknownPool : PoolConfig
    {
        public string NewCredential => throw new InvalidOperationException(Secret);
    }

    private static JObject Fixture()
    {
        var document = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config.example.json")));
        var tls = new JObject { ["enabled"] = true, ["tlsPfxFile"] = Secret, ["tlsPfxPassword"] = Secret };
        document["api"]["tls"] = tls;
        foreach(var name in new[] { "tlsCert", "tlsKey", "tlsPassword" })
            document["persistence"]["postgres"][name] = Secret;
        document["coinTemplates"] = new JArray(Secret);
        document["shareRelay"] = new JObject { ["publishUrl"] = Secret, ["sharedEncryptionKey"] = Secret, ["connect"] = true };
        document["shareRelays"] = new JArray(new JObject { ["url"] = Secret, ["sharedEncryptionKey"] = Secret });
        document["shareRecoveryFile"] = Secret;
        document["shareRecoveryStateDirectory"] = Secret;
        foreach(var pool in document["pools"].Children<JObject>())
        {
            pool["pubKey"] = Secret;
            pool[Secret] = new JObject { ["enabled"] = new JArray(Secret, 987654321, true) };
            pool["paymentProcessing"]["payoutSchemeConfig"] = new JObject { [Secret] = Secret };
            pool["paymentProcessing"]["walletPassword"] = Secret;
            pool["paymentProcessing"]["walletPrivateKey"] = Secret;
            pool["paymentProcessing"][Secret] = new JArray(new JObject { ["port"] = Secret });
            foreach(var port in pool["ports"].Children<JProperty>())
            {
                port.Value["tlsPfxFile"] = Secret;
                port.Value["tlsPfxPassword"] = Secret;
            }
            foreach(var daemon in pool["daemons"].Children<JObject>())
            {
                daemon["apiKey"] = Secret;
                daemon["httpPath"] = Secret;
                daemon[Secret] = new JObject { ["innocent"] = Secret };
            }
        }
        // Strings with innocuous names (host, IDs, URLs, addresses, filenames,
        // extension keys, etc.) are sensitive too. Only bounded enums survive.
        foreach(var value in document.Descendants().OfType<JValue>().ToArray())
            if(value.Type == JTokenType.String && value.Parent is JProperty property &&
               property.Name is not ("manager" or "payoutScheme"))
                value.Value = Secret + "\r\n\u2028";
        return document;
    }

    private static async Task<(int ExitCode, string Output, string Error, string Logs)> RunDump(
        string content, string option, string mode = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "miningcore-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var filename = Path.Combine(directory, Secret + ".json");
        var logfile = Path.Combine(directory, "captured.log");
        try
        {
            if(content != null && mode != "missing-file")
                await File.WriteAllTextAsync(filename, content);
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory,
            };
            foreach(var argument in new[]
            {
                "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.runtimeconfig.json"),
                "--depsfile", Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.deps.json"),
                Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.ProcessHost.dll"), "config-dump", logfile, option,
            })
                start.ArgumentList.Add(argument);
            if(mode is not ("missing-option" or "help"))
            {
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(filename);
            }
            if(mode == "unknown-option")
                start.ArgumentList.Add("--" + Secret);
            start.Environment["MININGCORE_ADMIN_API_TOKEN"] = Secret;
            start.Environment["PGPASSWORD"] = Secret;
            start.Environment["PGSSLKEY"] = Secret;
            using var process = Process.Start(start);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                if(!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync();
                }
            }
            return (process.ExitCode, await stdout, await stderr,
                File.Exists(logfile) ? await File.ReadAllTextAsync(logfile) : string.Empty);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
