using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Rpc;
using Miningcore.Time;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bStartupTests
{
    private sealed class StartupAttestationManager(IComponentContext context) : BitcoinBlake2bJobManager(
        context, Substitute.For<IMasterClock>(), Substitute.For<IMessageBus>(), new BitcoinBlake2bExtraNonceProvider())
    {
        internal string Fault;
        internal int FailuresRemaining;
        internal int Attempts;
        internal int HeaderReads;
        internal readonly List<int> Delays = new();
        internal CancellationTokenSource CancelOnDelay;

        protected override Task<RpcResponse<JObject>> ReadDaemonAttestationAsync(string method, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(method == "getnetworkinfo") Attempts++;
            var response = method switch
            {
                "getnetworkinfo" => new JObject { ["version"] = Fault == "version" ? 290402 : 290403,
                    ["subversion"] = "/Satoshi:29.4.3/Knots:20260508/" },
                "getblockchaininfo" => new JObject { ["chain"] = Fault == "chain-drift" && Attempts > 1 ? "main" : "regtest",
                    ["blocks"] = 19, ["bestblockhash"] = new string('c', 64) },
                "getdeploymentinfo" => MaturityInfo(19, Fault != "deployment"),
                _ => throw new InvalidOperationException(method),
            };
            return Task.FromResult(new RpcResponse<JObject>(response));
        }

        protected override Task<RpcResponse<JObject>> GetAttestationHeaderAsync(string hash, CancellationToken ct)
        {
            HeaderReads++;
            if(FailuresRemaining-- > 0)
            {
                if(Fault == "timeout") throw new TimeoutException("private transport detail");
                if(Fault == "rpc") return Task.FromResult(new RpcResponse<JObject>(null, new JsonRpcError(-500, "private transport detail", null)));
                return Task.FromResult(new RpcResponse<JObject>(new JObject { ["hash"] = hash, ["height"] = 19, ["confirmations"] = -1 }));
            }
            return Task.FromResult(new RpcResponse<JObject>(new JObject
                { ["hash"] = Fault == "header-contract" ? new string('e', 64) : hash, ["height"] = 19, ["confirmations"] = 1 }));
        }

        protected override Task WaitStartupAttestationRetryAsync(int attempt, CancellationToken ct)
        {
            Delays.Add(attempt);
            CancelOnDelay?.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private StartupAttestationManager StartupManager(string fault, int failures = 0)
    {
        var manager = new StartupAttestationManager(container) { Fault = fault, FailuresRemaining = failures };
        manager.Configure(LifecycleConfig(), new ClusterConfig());
        return manager;
    }

    [Theory]
    [InlineData("orphan")]
    [InlineData("rpc")]
    [InlineData("timeout")]
    public async Task StartupAttestation_TransientFailureRereadsWholeSnapshot(string fault)
    {
        var manager = StartupManager(fault, 1);
        await manager.AttestStartupAsync(CancellationToken.None);
        Assert.Equal(2, manager.Attempts);
        Assert.Equal(2, manager.HeaderReads);
        Assert.Equal(new[] { 1 }, manager.Delays);
    }

    [Fact]
    public async Task StartupAttestation_PersistentFailureIsBounded()
    {
        var manager = StartupManager("rpc", int.MaxValue);
        var error = await Assert.ThrowsAsync<PoolStartupException>(() => manager.AttestStartupAsync(CancellationToken.None));
        Assert.Equal(3, manager.Attempts);
        Assert.Equal(new[] { 1, 2 }, manager.Delays);
        Assert.DoesNotContain("private transport detail", error.Message);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("deployment")]
    [InlineData("header-contract")]
    public async Task StartupAttestation_ContradictorySuccessFailsImmediately(string fault)
    {
        var manager = StartupManager(fault);
        await Assert.ThrowsAsync<PoolStartupException>(() => manager.AttestStartupAsync(CancellationToken.None));
        Assert.Equal(1, manager.Attempts);
        Assert.Empty(manager.Delays);
    }

    [Fact]
    public async Task StartupAttestation_RetryCannotSwitchChain()
    {
        var manager = StartupManager("chain-drift", 1);
        await Assert.ThrowsAsync<PoolStartupException>(() => manager.AttestStartupAsync(CancellationToken.None));
        Assert.Equal(2, manager.Attempts);
        Assert.Equal(1, manager.HeaderReads);
    }

    [Fact]
    public async Task StartupAttestation_CancellationStopsRetry()
    {
        using var stop = new CancellationTokenSource();
        var manager = StartupManager("rpc", 1);
        manager.CancelOnDelay = stop;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.AttestStartupAsync(stop.Token));
        Assert.Equal(1, manager.Attempts);
    }
}
