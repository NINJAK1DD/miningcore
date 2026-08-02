using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
using Share = Miningcore.Blockchain.Share;

namespace Miningcore.Tests.Mining;

[Collection(ShareRecoveryLoggingCollection.Name)]
public class ShareRecoveryPathOwnershipTests
{
    [Fact]
    public async Task Ownership_IgnoresDifferentPoolPortAcrossProcessesAndReleasesAfterKill()
    {
        var directory = CreateDirectory();
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state-a");
        var readyFilename = Path.Combine(directory, "ready");
        using var child = StartHolder(recoveryFilename, stateDirectory,
            readyFilename, disableManagedFileLocking: true);

        try
        {
            await WaitForFileAsync(readyFilename, child);
            using var contender = new ShareRecoveryPathOwnership(new ClusterConfig
            {
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = Path.Combine(directory, "state-b"),
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
        var stateDirectory = Path.Combine(directory, "state-a");
        var readyFilename = Path.Combine(directory, "ready");
        await File.WriteAllTextAsync(recoveryFilename, "not inspected");
        using var child = StartHolder(recoveryFilename, stateDirectory,
            readyFilename, disableManagedFileLocking: true);

        try
        {
            await WaitForFileAsync(readyFilename, child);
            var importStateDirectory = Path.Combine(directory, "state-b");
            var recorder = CreateRecorder(recoveryFilename,
                importStateDirectory);
            var importState = new ShareRecoveryImportState(recoveryFilename,
                importStateDirectory);

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
                ShareRecoveryStateDirectory = Path.Combine(directory, "other-state"),
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

    [Fact]
    public async Task Ownership_ConflictsThroughSymlinkedParentAcrossProcesses()
    {
        var directory = CreateDirectory();
        var realDirectory = Path.Combine(directory, "real");
        var linkedDirectory = Path.Combine(directory, "linked");
        Directory.CreateDirectory(realDirectory);

        try
        {
            Directory.CreateSymbolicLink(linkedDirectory, realDirectory);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(directory, true);
            return;
        }

        var recoveryFilename = Path.Combine(realDirectory,
            "recovered-shares.txt");
        var aliasFilename = Path.Combine(linkedDirectory,
            "recovered-shares.txt");
        var readyFilename = Path.Combine(directory, "ready");
        using var child = StartHolder(recoveryFilename,
            Path.Combine(directory, "state-a"), readyFilename,
            disableManagedFileLocking: true);

        try
        {
            await WaitForFileAsync(readyFilename, child);
            using var contender = new ShareRecoveryPathOwnership(new ClusterConfig
            {
                ShareRecoveryFile = aliasFilename,
                ShareRecoveryStateDirectory = Path.Combine(directory, "state-b"),
            });

            Assert.Throws<IOException>(contender.Acquire);
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
    public async Task Ownership_RejectsHardLinkedJournalAliasesAcrossProcesses()
    {
        var directory = CreateDirectory();
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var aliasFilename = Path.Combine(directory, "recovered-shares-alias.txt");
        var readyFilename = Path.Combine(directory, "ready");
        File.WriteAllText(recoveryFilename, "legacy journal");
        CreateHardLink(recoveryFilename, aliasFilename);
        using var child = StartHolder(recoveryFilename,
            Path.Combine(directory, "state"), readyFilename,
            disableManagedFileLocking: true);

        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, child.ExitCode);
            Assert.False(File.Exists(readyFilename));
            var error = await child.StandardError.ReadToEndAsync();
            Assert.Contains("Hard-linked recovery journals are not supported",
                error);
        }
        finally
        {
            if(!child.HasExited)
                child.Kill(true);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Ownership_RejectsSymbolicLinkJournal()
    {
        var directory = CreateDirectory();
        var target = Path.Combine(directory, "target.txt");
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        File.WriteAllText(target, "legacy journal");

        try
        {
            File.CreateSymbolicLink(recoveryFilename, target);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(directory, true);
            return;
        }

        try
        {
            using var ownership = new ShareRecoveryPathOwnership(new ClusterConfig
            {
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
            });

            Assert.Throws<InvalidDataException>(ownership.Acquire);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task JournalMutation_RequiresExplicitOwnership()
    {
        var directory = CreateDirectory();
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        using var ownership = new ShareRecoveryPathOwnership(config);
        var recorder = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            config, new MessageBus(), Substitute.For<IShareRecoveryFailureHandler>(),
            Substitute.For<IMiningFailStopCoordinator>(), ownership);

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                recorder.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "must-own" },
                }));
            Assert.Contains("ownership is not held", error.Message);
            Assert.False(File.Exists(recoveryFilename));
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

    private static void CreateHardLink(string existingFilename,
        string newFilename)
    {
        var success = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(newFilename, existingFilename, IntPtr.Zero)
            : link(existingFilename, newFilename) == 0;
        if(!success)
            throw new IOException(
                $"Unable to create hard-link test fixture (error {Marshal.GetLastPInvokeError()})");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW",
        SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newFilename,
        string existingFilename, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int link(string existingFilename, string newFilename);
}
