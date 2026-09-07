using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Api.Extensions;
using Miningcore.Blockchain;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Time;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bStartupTests
{
    [Fact]
    public void IsolatedFault_ReportsSecondaryFailureWithoutRepeatingPrimaryNotification()
    {
        var bus = Substitute.For<IMessageBus>();
        using var dependencies = Scope();
        using var scope = dependencies.BeginLifetimeScope(builder => builder.RegisterInstance(bus));
        var pool = ResolvePool(scope);
        pool.Configure(LifecycleConfig(), new ClusterConfig { Logging = new ClusterLoggingConfig() });
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}|${exception:format=message}" };
        var config = new NLog.Config.LoggingConfiguration();
        config.AddRuleForAllLevels(target);
        logs.Configuration = config;
        typeof(StratumServer).GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pool, logs.GetLogger("secondary-isolation-failure-test"));
        var callback = typeof(BitcoinBlake2bPool).GetMethod("HandleBlake2bPipelineFailure", BindingFlags.Instance | BindingFlags.NonPublic)!;

        callback.Invoke(pool, new object[] { new PoolStartupException("primary contract failure") });
        for(var i = 1; i <= 5; i++)
            callback.Invoke(pool, new object[] { new IOException($"secondary teardown failure {i}") });

        var diagnostics = target.Logs.Where(x => x.Contains("|RPC consumer diagnostic ")).ToArray();
        Assert.Equal(6, diagnostics.Length);
        Assert.StartsWith("Error|", diagnostics[0]);
        Assert.Contains("\"operation\":\"BitcoinBlake2bPool.FaultPool\"", diagnostics[0]);
        Assert.DoesNotContain("primary contract failure", diagnostics[0]);
        Assert.Contains(target.Logs, x => x.Contains("pool faulted; operator restart required"));
        Assert.Equal(3, target.Logs.Count(x => x.Contains("Info reports; subsequent failures use Debug")));
        for(var i = 1; i <= 5; i++)
        {
            Assert.StartsWith($"{(i <= 3 ? "Info" : "Debug")}|RPC consumer diagnostic ", diagnostics[i]);
            Assert.Contains("\"failure\":\"io\"", diagnostics[i]);
            Assert.DoesNotContain($"secondary teardown failure {i}", diagnostics[i]);
        }
        bus.Received(1).SendMessage(Arg.Any<PoolStatusNotification>(), Arg.Any<string>());
        bus.Received(1).SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
        Assert.Equal("faulted", pool.MiningState);
        Assert.True(pool.MiningFaulted);
        Assert.Null(pool.TryAcquireOperation());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedFault_ClosesOnlyItsListenerAndKeepsSupervisorAlive(bool duringStartup)
    {
        var bus = new MessageBus();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = bus.Listen<PoolStatusNotification>().Subscribe(status =>
        {
            if(status.Status == PoolStatus.Online) started.TrySetResult();
            else faulted.TrySetResult();
        });
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ => now);
        var manager = new LifecycleManager(container, clock);
        if(duringStartup) manager.Drift = "version";
        var failStop = Substitute.For<IMiningFailStopCoordinator>();
        using var dependencies = Scope();
        using var scope = dependencies.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(manager).As<BitcoinBlake2bJobManager>();
            builder.RegisterInstance(bus).As<IMessageBus>();
            builder.RegisterInstance(failStop);
        });
        var pool = ResolvePool(scope);
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint) portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        var config = LifecycleConfig();
        config.Enabled = true;
        config.Ports = new() { [port] = new PoolEndpoint { ListenAddress = "127.0.0.1", Difficulty = 1e-9 } };
        pool.Configure(config, new ClusterConfig { Logging = new ClusterLoggingConfig() });
        using var reservations = await new StratumListenerReservationCoordinator().ReserveAllAsync(new[] { config });
        pool.AttachStratumListenerReservations(reservations);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var process = new ProcessStatus();
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingStopped = false;
        var stopRequested = false;
        var lifetime = Program.SupervisePoolLifetimesAsync(new[]
        {
            new KeyValuePair<string, Func<CancellationToken, Task>>(config.Id, pool.RunAsync),
            new KeyValuePair<string, Func<CancellationToken, Task>>("healthy-sibling", async token =>
            {
                siblingStarted.TrySetResult();
                await PoolBase.WaitForShutdownAsync(token);
                siblingStopped = true;
            }),
        }, stop.Token, process, () => { stopRequested = true; stop.Cancel(); });
        try
        {
            await siblingStarted.Task.WaitAsync(stop.Token);
            using var rejectedStop = new CancellationTokenSource();
            rejectedStop.Cancel();
            var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.RunAsync(rejectedStop.Token));
            Assert.Contains("can only be started once", duplicate.Message);
            if(!duringStartup)
            {
                await started.Task.WaitAsync(stop.Token);
                Assert.Equal("online", pool.MiningState);
                Assert.False(pool.MiningFaulted);
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, stop.Token);
                manager.Drift = "version";
                now = now.AddMinutes(1); // expire the attestation cache
            }
            await faulted.Task.WaitAsync(stop.Token);
            Assert.Equal("faulted", pool.MiningState);
            Assert.Null(pool.TryAcquireOperation());
            Assert.False(lifetime.IsCompleted);
            Assert.False(stopRequested);
            Assert.False(siblingStopped);
            Assert.Equal(0, process.ExitCode);
            failStop.DidNotReceive().BeginFailStop(Arg.Any<int>());
            Assert.Equal("faulted", config.ToPoolInfo(AutoMapperFactory.CreateMapper(), null, pool).MiningState);
            if(duringStartup) Assert.False(started.Task.IsCompleted);

            // Wait for asynchronous local cleanup, then prove the real listening
            // socket was released while the process supervisor remains alive.
            while(true)
            {
                try
                {
                    using var rebound = StratumServer.CreateBoundSocket(new IPEndPoint(IPAddress.Loopback, port));
                    break;
                }
                catch(SocketException) { await Task.Delay(10, stop.Token); }
            }
            Assert.False(lifetime.IsCompleted);
        }
        finally
        {
            stop.Cancel();
            await lifetime.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(siblingStopped);
        Assert.Equal("stopping", pool.MiningState);
        Assert.True(pool.MiningFaulted);
        Assert.True(config.ToPoolInfo(AutoMapperFactory.CreateMapper(), null, pool).MiningFaulted);
        Assert.Equal(0, process.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConnectionDrainTimeout_OnlySuppressesGlobalFailureAfterLocalAdmissionCloses(bool isolated)
    {
        var failStop = Substitute.For<IMiningFailStopCoordinator>();
        using var dependencies = Scope();
        using var scope = dependencies.BeginLifetimeScope(builder => builder.RegisterInstance(failStop));
        var pool = ResolvePool(scope);
        pool.Configure(LifecycleConfig(), new ClusterConfig { Logging = new ClusterLoggingConfig() });
        var gate = (PoolOperationGate) typeof(BitcoinBlake2bPool)
            .GetField("operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool);
        if(isolated) gate.Close();

        typeof(BitcoinBlake2bPool).GetMethod("OnConnectionDrainTimeout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pool, new object[] { 1 });

        failStop.Received(isolated ? 0 : 1).BeginFailStop(ProcessExitCodes.GeneralFailure);
        Assert.Equal(!isolated, (bool) typeof(BitcoinBlake2bPool)
            .GetProperty("IsConnectionAdmissionOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool));
    }

    [Fact]
    public async Task IsolationDrain_ReportsOutstandingOperationsWithoutAbandoningThem()
    {
        using var scope = Scope();
        var pool = ResolvePool(scope);
        pool.Configure(LifecycleConfig(), new ClusterConfig { Logging = new ClusterLoggingConfig() });
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}" };
        var config = new NLog.Config.LoggingConfiguration();
        config.AddRuleForAllLevels(target);
        logs.Configuration = config;
        typeof(StratumServer).GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pool, logs.GetLogger("isolation-drain-test"));
        var gate = (PoolOperationGate) typeof(BitcoinBlake2bPool)
            .GetField("operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool);
        using var owned = gate.TryAcquire();
        using var nested = gate.TryAcquire();
        gate.Close();
        pool.DrainReportInterval = TimeSpan.FromMilliseconds(25);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var drain = (Task) typeof(BitcoinBlake2bPool)
            .GetMethod("DrainOperationsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pool, new object[] { deadline.Token });
        try
        {
            while(target.Logs.Count < 2) await Task.Delay(10, deadline.Token);
            Assert.Equal("draining", pool.MiningState);
            Assert.False(drain.IsCompleted);
            Assert.All(target.Logs, line => Assert.Contains("draining 2 outstanding admission lease(s)", line));
            Assert.All(target.Logs, line => Assert.Contains("nested leases are counted separately", line));
            nested.Dispose();
            Assert.Equal("draining", pool.MiningState);
        }
        finally { nested.Dispose(); owned.Dispose(); await drain.WaitAsync(deadline.Token); }
        Assert.Equal("faulted", pool.MiningState);
        Assert.Contains(target.Logs, line => line.Contains("drain completed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolationDrain_QuietUntilReportIntervalAndNoWarningForFastDrain(bool outstanding)
    {
        using var scope = Scope();
        var pool = ResolvePool(scope);
        pool.Configure(LifecycleConfig(), new ClusterConfig { Logging = new ClusterLoggingConfig() });
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var config = new NLog.Config.LoggingConfiguration();
        config.AddRuleForAllLevels(target);
        logs.Configuration = config;
        typeof(StratumServer).GetField("logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pool, logs.GetLogger("fast-isolation-drain-test"));
        var gate = (PoolOperationGate) typeof(BitcoinBlake2bPool)
            .GetField("operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool);
        using var owned = outstanding ? gate.TryAcquire() : null;
        gate.Close();
        // No timing race: this interval exceeds the test's cancellation deadline.
        pool.DrainReportInterval = TimeSpan.FromMinutes(1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var drain = (Task) typeof(BitcoinBlake2bPool)
            .GetMethod("DrainOperationsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pool, new object[] { deadline.Token });
        try
        {
            if(outstanding)
            {
                Assert.False(drain.IsCompleted);
                Assert.Empty(target.Logs);
            }
        }
        finally { owned?.Dispose(); await drain.WaitAsync(deadline.Token); }
        Assert.StartsWith("Info|Bitcoin BLAKE2b isolation drain completed", Assert.Single(target.Logs));
        Assert.True(pool.MiningFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedFault_RetainsOwnedSubmissionButDoesNotBypassGlobalFinancialStop(bool financialStop)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var process = new ProcessStatus();
        var host = Substitute.For<IHostApplicationLifetime>();
        host.When(x => x.StopApplication()).Do(_ => stop.Cancel());
        using var global = new MiningFailStopCoordinator(process, host);
        var bus = new MessageBus(global);
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var status = bus.Listen<PoolStatusNotification>().Subscribe(x =>
        {
            if(x.Status == PoolStatus.Online) online.TrySetResult();
        });
        var published = new TaskCompletionSource<Share>(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shares = bus.Listen<Share>().Subscribe(share =>
        {
            share.SetPersistenceAdmission(persisted.Task);
            published.TrySetResult(share);
        });
        var clock = new StandardClock();
        var manager = new LifecycleManager(container, clock)
        {
            SubmitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            SubmitRelease = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var dependencies = Scope();
        using var scope = dependencies.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(manager).As<BitcoinBlake2bJobManager>();
            builder.RegisterInstance(bus).As<IMessageBus>();
            builder.RegisterInstance(global).As<IMiningFailStopCoordinator>();
        });
        var pool = ResolvePool(scope);
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint) portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        var config = LifecycleConfig();
        config.Enabled = true;
        config.Banning = new PoolShareBasedBanningConfig();
        config.Ports = new() { [port] = new PoolEndpoint { ListenAddress = "127.0.0.1", Difficulty = 1e-9 } };
        manager.SubmittedShare = new Share { PoolId = config.Id, Miner = config.Address, Difficulty = 1e-9,
            Created = DateTime.UtcNow, IsBlockCandidate = true, BlockHeight = 20, BlockHash = new string('a', 64) };
        pool.Configure(config, new ClusterConfig { Logging = new ClusterLoggingConfig() });
        pool.ConnectionDrainTimeout = TimeSpan.FromMilliseconds(100);
        using var reservations = await new StratumListenerReservationCoordinator().ReserveAllAsync(new[] { config });
        pool.AttachStratumListenerReservations(reservations);
        var lifetime = pool.RunAsync(stop.Token);
        try
        {
            await online.Task.WaitAsync(stop.Token);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, stop.Token);
            using var reader = new StreamReader(client.GetStream());
            using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            async Task Request(int id, string method, params string[] parameters)
            {
                await writer.WriteLineAsync(JsonConvert.SerializeObject(new { id, method, @params = parameters }));
                while(true)
                {
                    var response = JObject.Parse(await reader.ReadLineAsync(stop.Token));
                    if(response["id"]?.Value<int?>() != id) continue;
                    Assert.True(response["error"] == null || response["error"].Type == JTokenType.Null);
                    return;
                }
            }
            await Request(1, "mining.subscribe", "isolation-test");
            await Request(2, "mining.authorize", config.Address, "x");
            await writer.WriteLineAsync(JsonConvert.SerializeObject(new { id = 3, method = "mining.submit",
                @params = new[] { config.Address, "job", new string('0',16), new string('0',16), new string('0',16) } }));
            await manager.SubmitEntered.Task.WaitAsync(stop.Token);
            // Inject the same terminal callback as Jobs.OnError while the real
            // TCP dispatcher owns a pending manager submission.
            typeof(BitcoinBlake2bPool).GetMethod("HandleBlake2bPipelineFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pool, new object[] { new PoolStartupException("test daemon contradiction") });
            await Task.Delay(250, stop.Token); // deliberately exhaust local connection drain
            Assert.Equal("draining", pool.MiningState);
            Assert.False(manager.SubmitToken.IsCancellationRequested);
            Assert.False(global.IsFailStopRequested);
            Assert.False(lifetime.IsCompleted);
            Assert.Null(pool.TryAcquireOperation());
            if(financialStop)
            {
                global.BeginFailStop(ProcessExitCodes.GeneralFailure);
                await lifetime.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, process.ExitCode);
                Assert.False(published.Task.IsCompleted);
            }
            else
            {
                manager.SubmitRelease.TrySetResult();
                Assert.Same(manager.SubmittedShare, await published.Task.WaitAsync(stop.Token));
                Assert.False(manager.SubmittedShare.PersistenceAdmission.IsCompleted);
                persisted.TrySetResult();
                await manager.SubmittedShare.PersistenceAdmission.WaitAsync(stop.Token);
                Assert.False(global.IsFailStopRequested);
                Assert.False(lifetime.IsCompleted);
                Assert.Equal(0, process.ExitCode);
            }
        }
        finally
        {
            manager.SubmitRelease.TrySetResult();
            persisted.TrySetResult();
            stop.Cancel();
            try { await lifetime.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        }
    }
}
