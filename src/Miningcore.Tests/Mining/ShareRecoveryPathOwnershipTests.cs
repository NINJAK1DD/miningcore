using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Mining;

[Collection(ShareRecoveryLoggingCollection.Name)]
public class ShareRecoveryPathOwnershipTests
{
    [Fact]
    public async Task Ownership_IgnoresDifferentPoolPortAcrossProcessesAndReleasesAfterKill()
    {
        var directory = CreateDirectory();
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var readyFilename = Path.Combine(directory, "ready");
        using var child = StartHolder(recoveryFilename, stateDirectory,
            readyFilename, disableManagedFileLocking: true);

        try
        {
            await WaitForFileAsync(readyFilename, child);
            using var contender = new ShareRecoveryPathOwnership(new ClusterConfig
            {
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = stateDirectory,
                Pools = new[]
                {
                    new PoolConfig
                    {
                        Id = "different-port",
                        Ports = new Dictionary<int, PoolEndpoint>
                        {
                            [4444] = new(),
                        },
                    },
                },
            });

            var conflict = Assert.Throws<IOException>(contender.Acquire);
            Assert.Contains("Another Miningcore process owns recovery journal",
                conflict.Message);

            child.Kill(true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            contender.Acquire();
            Assert.True(contender.IsHeld);
        }
        finally
        {
            if(!child.HasExited)
                child.Kill(true);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoveryImport_CannotInspectOrMutateJournalOwnedByAnotherProcess()
    {
        var directory = CreateDirectory();
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var readyFilename = Path.Combine(directory, "ready");
        await File.WriteAllTextAsync(recoveryFilename, "not inspected");
        using var child = StartHolder(recoveryFilename, stateDirectory,
            readyFilename, disableManagedFileLocking: false);

        try
        {
            await WaitForFileAsync(readyFilename, child);
            var recorder = CreateRecorder(recoveryFilename, stateDirectory);
            var importState = new ShareRecoveryImportState(recoveryFilename,
                stateDirectory);

            await Assert.ThrowsAsync<IOException>(() =>
                recorder.RecoverSharesAsync(recoveryFilename));

            Assert.False(File.Exists(importState.Filename));
            Assert.Equal("not inspected",
                await File.ReadAllTextAsync(recoveryFilename));
        }
        finally
        {
            if(!child.HasExited)
                child.Kill(true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ShareRecorder_HoldsOwnershipThroughStopAndFinalDrain()
    {
        var directory = CreateDirectory();
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var recorder = CreateRecorder(recoveryFilename, stateDirectory);
        using var contender = new ShareRecoveryPathOwnership(new ClusterConfig
        {
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = stateDirectory,
        });

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            Assert.Throws<IOException>(contender.Acquire);

            await recorder.StopAsync(CancellationToken.None);

            contender.Acquire();
            Assert.True(contender.IsHeld);
        }
        finally
        {
            try
            {
                await recorder.StopAsync(CancellationToken.None);
            }
            catch
            {
            }
            contender.Release();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SameRecoveryPathProducesSameOwnershipKeyAcrossConfigurations()
    {
        var directory = CreateDirectory();
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");

        try
        {
            using var first = new ShareRecoveryPathOwnership(new ClusterConfig
            {
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = stateDirectory,
                Pools = new[] { new PoolConfig { Id = "first" } },
            });
            using var second = new ShareRecoveryPathOwnership(new ClusterConfig
            {
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = stateDirectory,
                Pools = new[] { new PoolConfig { Id = "second" } },
            });

            Assert.Equal(first.OwnershipFilename, second.OwnershipFilename);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static ShareRecorder CreateRecorder(string recoveryFilename,
        string stateDirectory)
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        return new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = stateDirectory,
            }, new MessageBus());
    }

    private static Process StartHolder(string recoveryFilename,
        string stateDirectory, string readyFilename,
        bool disableManagedFileLocking)
    {
        var helper = Path.Combine(AppContext.BaseDirectory,
            "Miningcore.Tests.ProcessHost.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,
            "Miningcore.Tests.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,
            "Miningcore.Tests.deps.json"));
        start.ArgumentList.Add(helper);
        start.ArgumentList.Add("hold");
        start.ArgumentList.Add(recoveryFilename);
        start.ArgumentList.Add(stateDirectory);
        start.ArgumentList.Add(readyFilename);
        if(disableManagedFileLocking)
            start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";

        return Process.Start(start) ??
            throw new InvalidOperationException("Unable to start ownership test process");
    }

    private static async Task WaitForFileAsync(string filename, Process child)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while(!File.Exists(filename))
        {
            if(child.HasExited)
                throw new InvalidOperationException(
                    $"Ownership test process exited {child.ExitCode}: " +
                    await child.StandardError.ReadToEndAsync(timeout.Token));
            await Task.Delay(25, timeout.Token);
        }
    }

    private static string CreateDirectory()
    {
        var result = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(result);
        return result;
    }
}
