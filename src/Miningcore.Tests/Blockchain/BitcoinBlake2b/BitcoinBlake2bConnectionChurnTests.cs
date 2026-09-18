using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Miningcore.Configuration;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Tests.Stratum;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bDifficultyBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealListener_ReconnectChurn_MeasuresAndBoundsFreshWorkerJobs(bool enforce)
    {
        const int attempts = 256;
        // Optional external-client measurement mode. The ordinary CI theory below remains
        // self-contained; the opt-in script separates client CPU/allocations from this process.
        var measurement = Environment.GetEnvironmentVariable("MININGCORE_TEST_CHURN_MEASUREMENT");
        if(measurement != null && enforce != bool.Parse(Environment.GetEnvironmentVariable("MININGCORE_TEST_CHURN_ENFORCE")))
            return;
        var (config, manager, clock, bus) = Fixture();
        // Stable external measurement scenario names. Tests asserting static metric
        // values use unique pool IDs in StratumAdmissionTests.Server for isolation.
        config.Id = enforce ? "churn-limited" : "churn-generous";
        config.ConnectionAdmission = new StratumAdmissionConfig
        {
            Burst = 1000, BurstPerAddress = enforce ? 8 : 1000,
            IdleExpirySeconds = 1000,
            MaxTrackedAddresses = 110000,
        };
        using var scope = container.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(Substitute.For<IBlockRepository>());
            builder.RegisterInstance(Substitute.For<IShareRepository>());
        });
        var pool = new BitcoinBlake2bWireSession.TestPool(scope, clock, bus,
            container.Resolve<RecyclableMemoryStreamManager>(), new ManualTimeProvider());
        pool.AdmissionTimeProvider = new ManualTimeProvider();
        var socket = StratumServer.CreateBoundSocket(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = new StratumEndpoint((IPEndPoint) socket.LocalEndPoint, new PoolEndpoint { Difficulty = 1e-9 });
        config.Ports = new Dictionary<int, PoolEndpoint> { [endpoint.IPEndPoint.Port] = endpoint.PoolEndpoint };
        pool.Configure(config, new ClusterConfig());
        pool.SetLogger(new NLog.NullLogger(NLog.LogManager.LogFactory));
        pool.SetManager(manager);
        using var reservation = new StratumListenerReservation(config.Id, endpoint, socket);
        reservation.Activate();
        using var stop = new CancellationTokenSource();
        var run = pool.RunAsync(stop.Token, reservation);
        var accepted = 0;
        var peakTasks = 0;
        var allocated = GC.GetTotalAllocatedBytes(true);
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if(measurement != null)
            {
                await File.WriteAllTextAsync(measurement + ".ready.tmp", endpoint.IPEndPoint.Port.ToString());
                File.Move(measurement + ".ready.tmp", measurement + ".ready");
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                while(!File.Exists(measurement + ".stop"))
                {
                    peakTasks = Math.Max(peakTasks, pool.TrackedConnectionTaskCount);
                    await Task.Delay(25, deadline.Token);
                }
                await StratumAdmissionTests.Until(() => pool.TrackedConnectionTaskCount == 0 &&
                    pool.ConnectionAdmission.Snapshot.Active == 0);
                stopwatch.Stop();
                var result = JObject.FromObject(new
                {
                    policy = enforce ? "limited" : "generous", attempts,
                    jobs = pool.JobsCreated, elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
                    serverProcessCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    serverProcessAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
                    sampledPeakTasks = peakTasks, retainedConnections = pool.ConnectionCount,
                    retainedTasks = pool.TrackedConnectionTaskCount,
                    retainedAddresses = pool.ConnectionAdmission.Snapshot.Addresses,
                });
                Assert.Equal((enforce ? 8 : attempts) * 9, pool.JobsCreated);
                Assert.Equal(0, pool.ConnectionCount);
                Assert.Equal((0, 1), pool.ConnectionAdmission.Snapshot);
                await File.WriteAllTextAsync(measurement + ".result.json", result.ToString());
                return;
            }
            for(var i = 0; i < attempts; i++)
            {
                using(var client = new TcpClient(AddressFamily.InterNetwork))
                {
                    try
                    {
                        await client.ConnectAsync(endpoint.IPEndPoint).WaitAsync(StratumAdmissionTests.Timeout);
                        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 1024, true);
                        await StratumAdmissionTests.Send(client, "{\"id\":1,\"method\":\"mining.subscribe\",\"params\":[\"churn-test\"]}");
                        var line = await reader.ReadLineAsync().WaitAsync(StratumAdmissionTests.Timeout);
                        if(line == null) continue;
                        Assert.Equal(1, JObject.Parse(line)["id"].Value<int>());
                        await ReadNotification(reader, "mining.set_difficulty");
                        await ReadNotification(reader, "mining.notify");
                        // Spend the new connection's entire difficulty negotiation allowance.
                        for(var j = 0; j < 8; j++)
                        {
                            await StratumAdmissionTests.Send(client, $"{{\"id\":{j + 2},\"method\":\"mining.suggest_difficulty\",\"params\":[{j + 2}e-9]}}");
                            Assert.True(JObject.Parse(await reader.ReadLineAsync().WaitAsync(StratumAdmissionTests.Timeout))["result"].Value<bool>());
                            await ReadNotification(reader, "mining.set_difficulty");
                            await ReadNotification(reader, "mining.notify");
                        }
                        accepted++;
                        peakTasks = Math.Max(peakTasks, pool.TrackedConnectionTaskCount);
                    }
                    catch(IOException ex) when(enforce && ex.InnerException is SocketException) { }
                    catch(SocketException ex) when(enforce && ex.SocketErrorCode is SocketError.ConnectionReset or
                        SocketError.ConnectionAborted or SocketError.Shutdown) { }
                }
                await StratumAdmissionTests.Until(() => pool.TrackedConnectionTaskCount == 0 && pool.ConnectionAdmission.Snapshot.Active == 0);
            }
            stopwatch.Stop();
            output.WriteLine($"Policy={(enforce ? "limited" : "generous")}; attempts={attempts}; admitted={accepted}; " +
                $"jobs={pool.JobsCreated}; elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}; " +
                $"processCpuMs={(process.TotalProcessorTime - cpu).TotalMilliseconds:F2}; " +
                $"processAllocatedBytes={GC.GetTotalAllocatedBytes(true) - allocated}; peakTasks={peakTasks}; " +
                $"retainedConnections={pool.ConnectionCount}; retainedTasks={pool.TrackedConnectionTaskCount}; " +
                $"retainedAddresses={pool.ConnectionAdmission.Snapshot.Addresses}");
            Assert.Equal(enforce ? 8 : attempts, accepted);
            Assert.Equal(accepted * 9, pool.JobsCreated);
            Assert.Equal(0, pool.ConnectionCount);
            Assert.Equal(0, pool.TrackedConnectionTaskCount);
            Assert.Equal((0, 1), pool.ConnectionAdmission.Snapshot);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(StratumAdmissionTests.Timeout);
        }
    }

    private static async Task ReadNotification(StreamReader reader, string method) =>
        Assert.Equal(method, JObject.Parse(await reader.ReadLineAsync().WaitAsync(StratumAdmissionTests.Timeout))["method"].Value<string>());
}
