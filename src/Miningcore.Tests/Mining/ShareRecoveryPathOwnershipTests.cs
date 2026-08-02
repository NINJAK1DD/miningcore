using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
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
    public void NestedOwnershipReleaseRetainsProcessLockUntilFinalRelease()
    {
        var directory = CreateDirectory();
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        using var ownership = new ShareRecoveryPathOwnership(recoveryFilename);

        try
        {
            ownership.Acquire();
            ownership.Acquire();
            ownership.Release();

            Assert.True(ownership.IsHeld);
            ownership.EnsureJournalPathIsExclusive();

            ownership.Release();
            Assert.False(ownership.IsHeld);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LinuxLinkFallbackMovesWithoutReplacement()
    {
        if(!OperatingSystem.IsLinux())
            return;

        var directory = CreateDirectory();
        var source = Path.Combine(directory, "source");
        var destination = Path.Combine(directory, "destination");
        File.WriteAllText(source, "recovery evidence");

        try
        {
            using var identity = RecoveryDirectoryIdentity.OpenFollowingPath(
                directory);
            identity.MoveEntryUsingLinkFallback("source", "destination",
                "test-forced fallback");

            Assert.False(File.Exists(source));
            Assert.Equal("recovery evidence", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LinuxLinkFallbackRefusesExistingDestination()
    {
        if(!OperatingSystem.IsLinux())
            return;

        var directory = CreateDirectory();
        var source = Path.Combine(directory, "source");
        var destination = Path.Combine(directory, "destination");
        File.WriteAllText(source, "new evidence");
        File.WriteAllText(destination, "existing evidence");

        try
        {
            using var identity = RecoveryDirectoryIdentity.OpenFollowingPath(
                directory);
            var error = Assert.Throws<IOException>(() =>
                identity.MoveEntryUsingLinkFallback("source", "destination",
                    "test-forced fallback"));

            Assert.Contains("without replacement", error.Message);
            Assert.Equal("new evidence", File.ReadAllText(source));
            Assert.Equal("existing evidence", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(22)]
    [InlineData(38)]
    [InlineData(95)]
    public void UnsupportedRenameErrorsSelectLinkFallback(int error) =>
        Assert.True(RecoveryDirectoryIdentity.IsRenameNoReplaceUnsupported(error));

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
    public async Task AcknowledgementCommand_CannotRaceNativeOwnerWhenManagedLockingIsDisabled()
    {
        var directory = CreateDirectory();
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var readyFilename = Path.Combine(directory, "ready");
        var configFilename = Path.Combine(directory, "config.json");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = stateDirectory,
        };
        var fatalState = new ShareRecoveryFatalState(config,
            new ProcessStatus(), stateDirectory);
        fatalState.MarkFatal(1, new[] { "ltc-solo" },
            new IOException("database unavailable"),
            new IOException("journal unavailable"));
        await File.WriteAllTextAsync(configFilename,
            JsonConvert.SerializeObject(new
            {
                shareRecoveryFile = recoveryFilename,
                shareRecoveryStateDirectory = stateDirectory,
                pools = Array.Empty<object>(),
            }));
        using var holder = StartHolder(recoveryFilename, stateDirectory,
            readyFilename, disableManagedFileLocking: true);

        try
        {
            await WaitForFileAsync(readyFilename, holder);
            using(var blocked = StartAcknowledgement(configFilename,
                      disableManagedFileLocking: true))
            {
                await blocked.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(15));
                var standardError = await blocked.StandardError.ReadToEndAsync();
                var standardOutput = await blocked.StandardOutput.ReadToEndAsync();
                Assert.True(blocked.ExitCode ==
                    ProcessExitCodes.UnreconciledShareDurabilityLoss,
                    $"Expected acknowledgement exit 74 but received {blocked.ExitCode}. " +
                    $"stdout: {standardOutput} stderr: {standardError}");
                Assert.Contains("ACKNOWLEDGEMENT REFUSED:", standardError);
                Assert.True(File.Exists(fatalState.FatalStateFilename));
            }

            holder.Kill(true);
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            using var accepted = StartAcknowledgement(configFilename,
                disableManagedFileLocking: true);
            await accepted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, accepted.ExitCode);
            Assert.False(File.Exists(fatalState.FatalStateFilename));
        }
        finally
        {
            if(!holder.HasExited)
                holder.Kill(true);
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotEqual(0, child.ExitCode);
            Assert.False(File.Exists(readyFilename));
            var error = await child.StandardError.ReadToEndAsync();
            Assert.Contains("Hard-linked recovery boundary files are not supported",
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
    public void Ownership_RejectsSymbolicLinkOwnerFile()
    {
        var directory = CreateDirectory();
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        using var ownership = new ShareRecoveryPathOwnership(new ClusterConfig
        {
            ShareRecoveryFile = recoveryFilename,
        });
        var target = Path.Combine(directory, "owner-target");
        File.WriteAllText(target, "must-not-own");

        try
        {
            try
            {
                File.CreateSymbolicLink(ownership.OwnershipFilename, target);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            var error = Assert.Throws<InvalidDataException>(ownership.Acquire);
            Assert.True(error.Message.Contains("regular file",
                    StringComparison.OrdinalIgnoreCase) ||
                error.Message.Contains("without following links",
                    StringComparison.OrdinalIgnoreCase));
            Assert.False(ownership.IsHeld);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Ownership_RejectsHardLinkedOwnerFile()
    {
        var directory = CreateDirectory();
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        using var ownership = new ShareRecoveryPathOwnership(new ClusterConfig
        {
            ShareRecoveryFile = recoveryFilename,
        });
        var target = Path.Combine(directory, "owner-target");
        File.WriteAllText(target, "must-not-own");
        CreateHardLink(target, ownership.OwnershipFilename);

        try
        {
            var error = Assert.Throws<InvalidDataException>(ownership.Acquire);
            Assert.Contains("Hard-linked recovery boundary files are not supported",
                error.Message);
            Assert.False(ownership.IsHeld);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Ownership_RejectsRetargetedParentSymlink()
    {
        var directory = CreateDirectory();
        var first = Path.Combine(directory, "first");
        var second = Path.Combine(directory, "second");
        var linked = Path.Combine(directory, "current");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(linked, first);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            using var ownership = new ShareRecoveryPathOwnership(new ClusterConfig
            {
                ShareRecoveryFile = Path.Combine(linked,
                    "recovered-shares.txt"),
            });
            ownership.Acquire();

            Directory.Delete(linked);
            Directory.CreateSymbolicLink(linked, second);

            var error = Assert.Throws<InvalidDataException>(
                ownership.EnsureJournalPathIsExclusive);
            Assert.Contains("replaced or retargeted", error.Message);

            using var secondOwner = new ShareRecoveryPathOwnership(
                new ClusterConfig
                {
                    ShareRecoveryFile = Path.Combine(linked,
                        "recovered-shares.txt"),
                });
            secondOwner.Acquire();
            Assert.True(secondOwner.IsHeld);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("append")]
    [InlineData("before-temporary-create")]
    [InlineData("before-publish")]
    public async Task JournalMutation_ParentRetargetCannotRedirectWrite(
        string phase)
    {
        var directory = CreateDirectory();
        var first = Path.Combine(directory, "first");
        var second = Path.Combine(directory, "second");
        var linked = Path.Combine(directory, "current");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            Directory.CreateSymbolicLink(linked, first);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(directory, true);
            return;
        }
        var recoveryFilename = Path.Combine(linked, "recovered-shares.txt");
        var (recorder, ownership, _, _) = CreateOwnedRecorder(recoveryFilename,
            Path.Combine(directory, "state"));

        try
        {
            ownership.Acquire();
            if(phase == "append")
                await recorder.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "original" },
                });

            void Retarget()
            {
                Directory.Delete(linked);
                Directory.CreateSymbolicLink(linked, second);
            }

            var retargeted = false;
            ownership.DirectoryOperationCheckpoint = operation =>
            {
                if(retargeted)
                    return;
                var shouldRetarget = phase == "append"
                    ? operation == "open:recovered-shares.txt"
                    : phase == "before-temporary-create"
                        ? operation.StartsWith("open:.recovered-shares.txt.",
                              StringComparison.Ordinal) &&
                          operation.EndsWith(".tmp", StringComparison.Ordinal)
                        : operation.StartsWith("move:.recovered-shares.txt.",
                            StringComparison.Ordinal);
                if(!shouldRetarget)
                    return;
                retargeted = true;
                Retarget();
            };

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                recorder.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "redirected" },
                }));

            Assert.Contains("replaced or retargeted", error.Message);
            Assert.False(File.Exists(Path.Combine(second,
                "recovered-shares.txt")));
            if(phase == "before-temporary-create")
                Assert.False(File.Exists(Path.Combine(first,
                    "recovered-shares.txt")));
            if(phase == "before-publish")
                Assert.True(File.Exists(Path.Combine(first,
                    "recovered-shares.txt")));
        }
        finally
        {
            ownership.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task StartupValidation_ParentRetargetCannotRedirectRead()
    {
        var directory = CreateDirectory();
        var first = Path.Combine(directory, "first");
        var second = Path.Combine(directory, "second");
        var linked = Path.Combine(directory, "current");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            Directory.CreateSymbolicLink(linked, first);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(directory, true);
            return;
        }
        var recoveryFilename = Path.Combine(linked, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var (recorder, ownership, config, _) = CreateOwnedRecorder(
            recoveryFilename, stateDirectory);

        try
        {
            ownership.Acquire();
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "startup" },
            });
            ownership.DirectoryOperationCheckpoint = operation =>
            {
                if(operation != "open:recovered-shares.txt")
                    return;
                ownership.DirectoryOperationCheckpoint = _ => { };
                Directory.Delete(linked);
                Directory.CreateSymbolicLink(linked, second);
            };
            var status = new ProcessStatus();
            var state = new ShareRecoveryFatalState(config, status,
                stateDirectory, ownership);

            Assert.Throws<PoolStartupException>(state.EnsureStartupAllowed);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                status.ExitCode);
            Assert.False(File.Exists(Path.Combine(second,
                "recovered-shares.txt")));
        }
        finally
        {
            ownership.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoveryImport_ParentRetargetCannotRedirectReadOrWriteDatabase()
    {
        var directory = CreateDirectory();
        var first = Path.Combine(directory, "first");
        var second = Path.Combine(directory, "second");
        var linked = Path.Combine(directory, "current");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            Directory.CreateSymbolicLink(linked, first);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(directory, true);
            return;
        }
        var recoveryFilename = Path.Combine(linked, "recovered-shares.txt");
        var (recorder, ownership, _, repository) = CreateOwnedRecorder(
            recoveryFilename, Path.Combine(directory, "state"));

        try
        {
            ownership.Acquire();
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "import" },
            });
            ownership.DirectoryOperationCheckpoint = operation =>
            {
                if(operation != "open:recovered-shares.txt")
                    return;
                ownership.DirectoryOperationCheckpoint = _ => { };
                Directory.Delete(linked);
                Directory.CreateSymbolicLink(linked, second);
            };

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                recorder.RecoverSharesAsync(recoveryFilename));
            await repository.DidNotReceiveWithAnyArgs()
                .TryRegisterRecoveryImportAsync(default, default, default,
                    default, default, default);
            Assert.False(File.Exists(Path.Combine(second,
                "recovered-shares.txt")));
        }
        finally
        {
            ownership.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LinuxOwnership_RejectsReplacedParentDirectory()
    {
        if(!OperatingSystem.IsLinux())
            return;

        var directory = CreateDirectory();
        var parent = Path.Combine(directory, "recovery");
        var displaced = Path.Combine(directory, "recovery-displaced");
        Directory.CreateDirectory(parent);

        try
        {
            using var ownership = new ShareRecoveryPathOwnership(new ClusterConfig
            {
                ShareRecoveryFile = Path.Combine(parent,
                    "recovered-shares.txt"),
            });
            ownership.Acquire();

            Directory.Move(parent, displaced);
            Directory.CreateDirectory(parent);

            var error = Assert.Throws<InvalidDataException>(
                ownership.EnsureJournalPathIsExclusive);
            Assert.Contains("replaced or retargeted", error.Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LinuxJournalValidation_RejectsFifoWithoutBlocking()
    {
        if(!OperatingSystem.IsLinux())
            return;

        var directory = CreateDirectory();
        var fifo = Path.Combine(directory, "recovered-shares.txt");
        if(mkfifo(fifo, Convert.ToUInt32("600", 8)) != 0)
            throw new IOException(
                $"Unable to create FIFO fixture (error {Marshal.GetLastPInvokeError()})");

        try
        {
            var validation = Task.Run(() => Assert.Throws<InvalidDataException>(
                () => RecoveryJournalPathSafety
                    .EnsureSinglePhysicalNameIfExists(fifo)));
            var error = await validation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("regular file", error.Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LinuxJournalValidation_RejectsDirectoryAndUnixSocket()
    {
        if(!OperatingSystem.IsLinux())
            return;

        var directory = CreateDirectory();
        var journalDirectory = Path.Combine(directory, "journal-directory");
        var socketFilename = Path.Combine(directory, "journal.socket");
        Directory.CreateDirectory(journalDirectory);

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                RecoveryJournalPathSafety.EnsureSinglePhysicalNameIfExists(
                    journalDirectory));

            using var socket = new Socket(AddressFamily.Unix,
                SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketFilename));
            Assert.Throws<InvalidDataException>(() =>
                RecoveryJournalPathSafety.EnsureSinglePhysicalNameIfExists(
                    socketFilename));
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

    private static (ShareRecorder Recorder,
        ShareRecoveryPathOwnership Ownership, ClusterConfig Config,
        IShareRepository ShareRepository) CreateOwnedRecorder(
        string recoveryFilename, string stateDirectory)
    {
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = stateDirectory,
        };
        var ownership = new ShareRecoveryPathOwnership(config);
        var repository = Substitute.For<IShareRepository>();
        var recorder = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            repository, Substitute.For<IBlockRepository>(), config,
            new MessageBus(), Substitute.For<IShareRecoveryFailureHandler>(),
            Substitute.For<IMiningFailStopCoordinator>(), ownership);
        return (recorder, ownership, config, repository);
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

    private static Process StartAcknowledgement(string configFilename,
        bool disableManagedFileLocking)
    {
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
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,
            "Miningcore.dll"));
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(configFilename);
        start.ArgumentList.Add(ShareRecoveryFatalState.AcknowledgeOption);
        if(disableManagedFileLocking)
            start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";

        return Process.Start(start) ??
            throw new InvalidOperationException(
                "Unable to start acknowledgement test process");
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

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int mkfifo(string pathname, uint mode);
}
