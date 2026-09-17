using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Extensions;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bDifficultyBudgetTests
{
    public static IEnumerable<object[]> RequestPublicationFailures()
    {
        foreach(var method in new[] { "mining.subscribe", "mining.configure", "mining.suggest_difficulty", "mining.authorize" })
            foreach(var failure in new[] { "send-queue", "response-queue", "unexpected", "unexpected-cancellation", "host-cancellation" })
                yield return new object[] { method, failure };
    }

    [Theory]
    [MemberData(nameof(RequestPublicationFailures))]
    public async Task RequestPublicationFailure_InvalidatesAssignmentAndCannotResume(string method, string failure)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        if(method != "mining.subscribe")
            await Subscribe(wire);
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}${exception:format=tostring}" };
        var queueRecovery = new PublicationQueueRecoveryTarget();
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, target);
        logging.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, queueRecovery);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("request-publication-test"));
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var job = Assert.IsType<BitcoinBlake2bJob>(manager.GetJobForStratum());
        var responses = wire.Connection.ResponseSequence;
        var jobs = wire.JobsCreated;
        using var cancel = new CancellationTokenSource();
        var sendEntered = Signal();
        var releaseSend = Signal();
        var queueFailure = failure.EndsWith("queue", StringComparison.Ordinal);
        if(queueFailure)
        {
            wire.Connection.SendMessageOverride = async (_, ct) =>
            {
                sendEntered.TrySetResult();
                await releaseSend.Task.WaitAsync(ct);
            };
            await wire.Connection.NotifyAsync("test.queue", Array.Empty<object>());
            await sendEntered.Task.WaitAsync(BarrierTimeout);
            var queue = (BufferBlock<object>) typeof(StratumConnection)
                .GetField("sendQueue", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(wire.Connection);
            // Fifteen entries let the response fill the queue, then set_difficulty
            // fails. Sixteen make the response itself fail (configure already mutated).
            for(var i = 0; i < (failure == "send-queue" ? 15 : 16); i++)
                await wire.Connection.NotifyAsync("test.queue", Array.Empty<object>());
            // Reproduce a sender freeing one slot during error logging. Against
            // swallowed suggestion failures, this permits an unmatched notify.
            queueRecovery.Recover = () => queue.TryReceive(out _);
        }
        else
            wire.BeforeCreateJob = () =>
            {
                Assert.Equal(method == "mining.subscribe" ? 1e-9 : 2e-9, context.Difficulty);
                if(failure == "host-cancellation")
                    cancel.Cancel();
                if(failure.Contains("cancellation"))
                    throw new OperationCanceledException("sensitive-exception-marker", cancel.Token);
                throw new InvalidOperationException("sensitive-exception-marker");
            };

        try
        {
            var error = await Record.ExceptionAsync(() => wire.DispatchBufferedAsync(cancel.Token, method, PublicationParameters(method)));
            if(queueFailure) Assert.IsType<IOException>(error);
            else if(failure.Contains("cancellation")) Assert.IsType<OperationCanceledException>(error);
            else Assert.IsType<InvalidOperationException>(error);
            Assert.Empty(context.validJobs);
            Assert.Equal(jobs, wire.JobsCreated); // No unmatched work was even constructed.
            Assert.Equal(responses + 1, wire.Connection.ResponseSequence);
            if(queueFailure) Assert.Equal(1, queueRecovery.Recoveries);

            wire.BeforeCreateJob = null;
            var validations = manager.AddressValidations;
            await wire.DispatchBufferedAsync("mining.authorize", "test.worker", Password(4e-9));
            await wire.DispatchBufferedAsync("mining.extranonce.subscribe");
            await wire.RetargetVarDiffAsync(false);
            await wire.AnnounceJobAsync(job.GetJobParams(false));
            Assert.Equal(validations, manager.AddressValidations);
            Assert.Equal(responses + 1, wire.Connection.ResponseSequence);
            Assert.Equal(jobs, wire.JobsCreated);
            Assert.Empty(context.validJobs);
            var messages = await wire.ReadUntilDisconnectedAsync();
            Assert.DoesNotContain(messages, x => x["method"]?.ToString() == "mining.notify");
            Assert.False(wire.Connection.IsAlive);
            Assert.False(wire.MiningFaulted);
            var expectedEvents = failure == "host-cancellation" ? 0 : 1;
            Assert.Equal(expectedEvents, target.Logs.Count(x => x.Contains("AssignmentPublicationFailure")));
            if(expectedEvents != 0)
                AssertPublicationCause(target, queueFailure ? "io" : failure.Contains("cancellation") ? "cancelled" : "invalid-operation");
            bus.Received(expectedEvents).SendMessage(Arg.Is<TelemetryEvent>(x =>
                x.Category == TelemetryCategory.StratumAdmission && x.Info == "publication-failure"), Arg.Any<string>());
        }
        finally { releaseSend.TrySetResult(); }
    }

    // A synchronous queue consumer models the otherwise nondeterministic race
    // without changing production transport or replacing its bounded send queue.
    private sealed class PublicationQueueRecoveryTarget : NLog.Targets.Target
    {
        internal Func<bool> Recover;
        internal int Recoveries;
        protected override void Write(NLog.LogEventInfo logEvent)
        {
            if(Recover != null && (logEvent.FormattedMessage.Contains("AssignmentPublicationFailure") ||
                logEvent.FormattedMessage.Contains("BitcoinPool.OnSuggestDifficultyAsync")))
            {
                if(Recover()) Recoveries++;
                Recover = null;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtocolFailureAfterMutationBeforeResponse_IsTerminal(bool disableVarDiff)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        context.VarDiff = new Miningcore.VarDiff.VarDiffContext();
        var responses = wire.Connection.ResponseSequence;
        wire.BeforeConfigure = () =>
        {
            if(disableVarDiff) context.VarDiff = null;
            else context.SetDifficulty(2e-9);
            throw new StratumException(StratumError.Other, "sensitive-exception-marker");
        };
        await wire.DispatchBufferedAsync("mining.configure", PublicationParameters("mining.configure"));
        Assert.Empty(context.validJobs);
        Assert.Equal(responses, wire.Connection.ResponseSequence);
        Assert.Empty(await wire.ReadUntilDisconnectedAsync());
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.StratumAdmission && x.Info == "publication-failure"), Arg.Any<string>());
    }

    [Theory]
    [InlineData("mining.subscribe")]
    [InlineData("mining.configure")]
    [InlineData("mining.suggest_difficulty")]
    [InlineData("mining.authorize")]
    public async Task RequestCancellationBeforeAcquisition_PreservesSession(string method)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        if(method != "mining.subscribe") await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var jobs = context.validJobs.ToArray();
        var responses = wire.Connection.ResponseSequence;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            wire.DispatchBufferedAsync(cancel.Token, method, PublicationParameters(method)));
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.Equal(responses, wire.Connection.ResponseSequence);
        Assert.Equal(1e-9, context.Difficulty);
        if(method == "mining.subscribe") await Subscribe(wire);
        await Accepted(wire, true, 2e-9);
        await Fence(wire);
        Assert.True(wire.Connection.IsAlive);
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.StratumAdmission && x.Info == "publication-failure"), Arg.Any<string>());
    }
}
