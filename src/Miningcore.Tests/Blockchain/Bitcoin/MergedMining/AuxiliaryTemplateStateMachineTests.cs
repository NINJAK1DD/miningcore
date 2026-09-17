using System;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class AuxiliaryTemplateStateMachineTests
{
    [Fact]
    public void UnavailableLevel_IsReassertedWithoutStartingFallback()
    {
        var state = new AuxiliaryTemplateStateMachine();

        var first = state.ReportUnavailable();
        var repeated = state.ReportUnavailable();

        AssertTransition(first, false, false, false, false, null, null);
        AssertTransition(repeated, false, false, false, false, null, null);
    }

    [Fact]
    public void CachedFallback_IsEdgeTriggeredAndFreshRecoveryReassertsLevels()
    {
        var state = new AuxiliaryTemplateStateMachine();
        var installed = Template(220, 'a', 'b');

        var fallback = state.ObserveCachedTemplate(installed, true, "timeout");
        AssertTransition(fallback, true, true, true, false, installed, "timeout");
        Assert.Equal(AuxiliaryTemplateObservationKind.None,
            state.PendingObservationKind);

        var repeated = state.ObserveCachedTemplate(installed, true,
            "transport failure");
        AssertTransition(repeated, true, true, false, false, installed,
            "transport failure");

        state.ObserveFreshTemplate(installed, false);
        var recovery = state.NoJobRequired(installed);
        AssertTransition(recovery, true, false, false, true, installed, null);

        state.ObserveFreshTemplate(installed, false);
        var healthy = state.NoJobRequired(installed);
        AssertTransition(healthy, true, false, false, false, installed, null);
    }

    [Fact]
    public void StartupCachedFailure_DefersFallbackUntilFirstJobIsInstalled()
    {
        var state = new AuxiliaryTemplateStateMachine();
        var startup = Template(220, 'a', 'b');
        state.CacheStartupTemplate(startup);

        var observation = state.ObserveCachedTemplate(startup, false, "timeout");
        Assert.False(observation.ShouldPublish);
        Assert.Same(startup, state.StartupTemplate);

        var failed = state.JobInstallationFailed(null);
        AssertTransition(failed, false, false, false, false, null, null);

        state.ObserveUnrefreshedTemplate();
        var installed = state.JobInstalled(startup, true);
        AssertTransition(installed, true, true, true, false, startup, "timeout");
        Assert.Null(state.StartupTemplate);
    }

    [Fact]
    public void ChangedFreshStartupFailure_MarksLaterStartupInstallAsFallback()
    {
        var state = new AuxiliaryTemplateStateMachine();
        var startup = Template(220, 'a', 'b');
        var fresh = Template(221, 'c', 'a');
        state.CacheStartupTemplate(startup);

        state.ObserveFreshTemplate(fresh, true);
        var failed = state.JobInstallationFailed(null);
        AssertTransition(failed, false, false, false, false, null, null);

        state.ObserveUnrefreshedTemplate();
        var installed = state.JobInstalled(startup, true);
        AssertTransition(installed, true, true, true, false, startup,
            "fresh auxiliary template job initialization failed before the first job");
    }

    [Fact]
    public void ReconfirmedStartupIdentity_ClearsOlderFallbackProvenance()
    {
        var state = new AuxiliaryTemplateStateMachine();
        var startup = Template(220, 'a', 'b');
        var superseding = Template(221, 'c', 'd');
        state.CacheStartupTemplate(startup);

        state.ObserveFreshTemplate(superseding, true);
        _ = state.JobInstallationFailed(null);

        state.ObserveFreshTemplate(startup, true);
        var reconfirmedFailure = state.JobInstallationFailed(null);
        AssertTransition(reconfirmedFailure, false, false, false, false, null,
            null);

        state.ObserveUnrefreshedTemplate();
        var installed = state.JobInstalled(startup, true);
        AssertTransition(installed, true, false, false, false, startup, null);
    }

    [Fact]
    public void ChangedFreshIdentityFailure_FallsBackUntilReplacementIsInstalled()
    {
        var state = new AuxiliaryTemplateStateMachine();
        var installed = Template(220, 'a', 'b');
        var replacement = Template(221, 'c', 'a');

        state.ObserveFreshTemplate(replacement, false);
        var fallback = state.JobInstallationFailed(installed);
        AssertTransition(fallback, true, true, true, false, installed,
            "replacement job initialization failed");

        state.ObserveFreshTemplate(replacement, false);
        var repeated = state.JobInstallationFailed(installed);
        AssertTransition(repeated, true, true, false, false, installed,
            "replacement job initialization failed");

        state.ObserveFreshTemplate(replacement, false);
        var recovery = state.JobInstalled(replacement, false);
        AssertTransition(recovery, true, false, false, true, replacement, null);
    }

    [Fact]
    public void SameFreshIdentity_RecoversEvenWhenParentJobInstallationFails()
    {
        var state = new AuxiliaryTemplateStateMachine();
        var installed = Template(220, 'a', 'b');
        _ = state.ObserveCachedTemplate(installed, true, "timeout");
        var reconfirmed = Template(220, 'a', 'b');

        state.ObserveFreshTemplate(reconfirmed, false);
        var recovery = state.JobInstallationFailed(installed);

        AssertTransition(recovery, true, false, false, true, installed, null);
    }

    [Fact]
    public void AbandonedFreshObservation_DoesNotCorrectDegradedState()
    {
        var state = new AuxiliaryTemplateStateMachine();
        var installed = Template(220, 'a', 'b');
        var fresh = Template(221, 'c', 'a');
        _ = state.ObserveCachedTemplate(installed, true, "timeout");

        state.ObserveFreshTemplate(fresh, false);
        state.AbandonPendingObservation();
        var transition = state.JobInstallationFailed(installed);

        Assert.False(transition.ShouldPublish);
        Assert.True(state.Available);
        Assert.True(state.Degraded);
        Assert.Equal(AuxiliaryTemplateObservationKind.None,
            state.PendingObservationKind);
    }

    [Theory]
    [InlineData("installed")]
    [InlineData("failed")]
    [InlineData("not-required")]
    public void EveryTerminalTransition_ConsumesPendingObservationExactlyOnce(
        string terminal)
    {
        var state = new AuxiliaryTemplateStateMachine();
        var installed = Template(220, 'a', 'b');
        state.ObserveFreshTemplate(installed, false);

        var first = Complete(terminal, state, installed);
        var duplicate = Complete(terminal, state, installed);

        Assert.True(first.ShouldPublish);
        Assert.False(duplicate.ShouldPublish);
        Assert.Equal(AuxiliaryTemplateObservationKind.None,
            state.PendingObservationKind);
    }

    private static AuxiliaryTemplateStateTransition Complete(string terminal,
        AuxiliaryTemplateStateMachine state, AuxBlockTemplate installed) =>
        terminal switch
        {
            "installed" => state.JobInstalled(installed, false),
            "failed" => state.JobInstallationFailed(installed),
            "not-required" => state.NoJobRequired(installed),
            _ => throw new ArgumentOutOfRangeException(nameof(terminal)),
        };

    private static void AssertTransition(AuxiliaryTemplateStateTransition transition,
        bool available, bool degraded, bool fallbackStarted, bool recovered,
        AuxBlockTemplate template, string failure)
    {
        Assert.True(transition.ShouldPublish);
        Assert.Equal(available, transition.Available);
        Assert.Equal(degraded, transition.Degraded);
        Assert.Equal(fallbackStarted, transition.FallbackStarted);
        Assert.Equal(recovered, transition.Recovered);
        Assert.Same(template, transition.Template);
        Assert.Equal(failure, transition.Failure);
    }

    private static AuxBlockTemplate Template(uint height, char hash,
        char previousHash) => new()
        {
            Height = height,
            Hash = new string(hash, 64),
            PreviousBlockhash = new string(previousHash, 64),
            Bits = "207fffff",
        };
}
