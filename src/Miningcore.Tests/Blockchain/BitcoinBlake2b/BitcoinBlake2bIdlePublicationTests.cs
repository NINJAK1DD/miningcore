using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.VarDiff;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bDifficultyBudgetTests
{
    [Theory]
    [InlineData(true, "missing-job")]
    [InlineData(false, "missing-job")]
    [InlineData(true, "send-queue")]
    [InlineData(false, "send-queue")]
    [InlineData(true, "unexpected")]
    [InlineData(false, "unexpected")]
    [InlineData(true, "unexpected-cancellation")]
    [InlineData(false, "unexpected-cancellation")]
    [InlineData(true, "host-cancellation")]
    [InlineData(false, "host-cancellation")]
    public async Task VarDiffPublicationFailure_ClosesBeforeGateReleaseAndCannotResume(bool idle, string failure)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await Subscribe(wire);
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("idle-publication-test"));
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var options = new VarDiffConfig { MinDiff = 1e-9, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        config.Ports[wire.Connection.LocalEndpoint.Port].VarDiff = options;
        context.VarDiff = new VarDiffContext { Config = options, LastTs = clock.Now.ToUnixSeconds() - 5,
            LastRetarget = clock.Now.ToUnixSeconds() - 100 };
        var job = Assert.IsType<BitcoinBlake2bJob>(manager.GetJobForStratum());
        var responses = wire.Connection.ResponseSequence;
        var jobs = wire.JobsCreated;
        using var cancel = new CancellationTokenSource();
        var sendEntered = Signal();
        var releaseSend = Signal();
        if(failure == "missing-job")
            manager.SetCurrentJob(null);
        else if(failure == "send-queue")
        {
            wire.Connection.SendMessageOverride = async (_, ct) =>
            {
                sendEntered.TrySetResult();
                await releaseSend.Task.WaitAsync(ct);
            };
            await wire.Connection.NotifyAsync("test.queue", new object[0]);
            await sendEntered.Task.WaitAsync(BarrierTimeout);
            // The send loop is blocked in its current message, so these sixteen
            // items fill the real queue deterministically before set_difficulty.
            for(var i = 0; i < 16; i++)
                await wire.Connection.NotifyAsync("test.queue", new object[0]);
        }
        else
            wire.BeforeCreateJob = () =>
            {
                Assert.Equal(2e-9, context.Difficulty); // Failure follows mutation.
                if(failure == "host-cancellation")
                    cancel.Cancel();
                if(failure.Contains("cancellation"))
                    throw new OperationCanceledException(cancel.Token);
                throw new InvalidOperationException("job construction failed");
            };

        try
        {
            var error = await Record.ExceptionAsync(() => wire.RetargetVarDiffAsync(idle, cancel.Token));
            Assert.NotNull(error);
            if(failure == "missing-job") Assert.IsType<StratumException>(error);
            if(failure == "send-queue") Assert.IsType<IOException>(error);
            if(failure.Contains("cancellation")) Assert.IsType<OperationCanceledException>(error);
            if(failure == "unexpected") Assert.IsType<InvalidOperationException>(error);
            Assert.Equal(2e-9, context.Difficulty);
            Assert.False(context.HasPendingDifficulty);
            Assert.Empty(context.validJobs);

            // Restored work, another retarget and already-decoded requests must
            // all encounter the terminal latch. None may resurrect the session.
            wire.BeforeCreateJob = null;
            manager.SetCurrentJob(job);
            var validations = manager.AddressValidations;
            await wire.DispatchBufferedAsync("mining.authorize", "test.worker", Password(4e-9));
            await wire.DispatchBufferedAsync("mining.extranonce.subscribe");
            await wire.RetargetVarDiffAsync(idle);
            await wire.AnnounceJobAsync(job.GetJobParams(false));
            Assert.Equal(validations, manager.AddressValidations);
            Assert.Equal(responses, wire.Connection.ResponseSequence);
            Assert.Equal(jobs + (failure == "send-queue" ? 1 : 0), wire.JobsCreated);
            Assert.Empty(context.validJobs);
            var messages = await wire.ReadUntilDisconnectedAsync();
            Assert.Empty(messages);
            Assert.False(wire.Connection.IsAlive);
            Assert.False(wire.MiningFaulted);
            var expectedEvents = failure == "host-cancellation" ? 0 : 1;
            Assert.Equal(expectedEvents, target.Logs.Count(x => x.Contains("AssignmentPublicationFailure")));
            bus.Received(expectedEvents).SendMessage(Arg.Is<TelemetryEvent>(x =>
                x.Category == TelemetryCategory.StratumAdmission && x.Info == "publication-failure"), Arg.Any<string>());
        }
        finally { releaseSend.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VarDiffCancellationBeforeAcquisition_PreservesSession(bool idle)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var jobs = context.validJobs.ToArray();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wire.RetargetVarDiffAsync(idle, cancel.Token));
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.Equal(1e-9, context.Difficulty);
        await Fence(wire);
        await Accepted(wire, true, 2e-9); // The canceled waiter did not release an unowned gate or latch admission.
        Assert.True(wire.Connection.IsAlive);
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.StratumAdmission && x.Info == "publication-failure"), Arg.Any<string>());
    }
}
