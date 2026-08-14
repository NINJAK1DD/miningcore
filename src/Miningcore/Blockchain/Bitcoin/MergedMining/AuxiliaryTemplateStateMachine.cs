using Miningcore.Blockchain.Bitcoin.DaemonResponses;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

internal enum AuxiliaryTemplateObservationKind
{
    None,
    Fresh,
    Cached,
}

internal readonly record struct AuxiliaryTemplateStateTransition(
    bool ShouldPublish,
    bool Available,
    bool Degraded,
    bool FallbackStarted,
    bool Recovered,
    AuxBlockTemplate Template,
    string Failure)
{
    public static AuxiliaryTemplateStateTransition None => default;
}

internal sealed class AuxiliaryTemplateStateMachine
{
    private const string ReplacementFailure =
        "replacement job initialization failed";
    private const string InitialFreshFailure =
        "fresh auxiliary template job initialization failed before the first job";

    private AuxiliaryTemplateObservation pendingObservation;
    private string startupFallbackFailure;

    public AuxBlockTemplate StartupTemplate { get; private set; }
    public bool Available { get; private set; }
    public bool Degraded { get; private set; }
    internal AuxiliaryTemplateObservationKind PendingObservationKind =>
        pendingObservation.Kind;

    public void CacheStartupTemplate(AuxBlockTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        StartupTemplate = template;
        startupFallbackFailure = null;
        pendingObservation = default;
    }

    public AuxiliaryTemplateStateTransition ReportUnavailable()
    {
        pendingObservation = default;
        return TransitionTo(false, false, null, null);
    }

    public void ObserveUnrefreshedTemplate()
    {
        pendingObservation = default;
    }

    public AuxiliaryTemplateStateTransition ObserveCachedTemplate(
        AuxBlockTemplate template, bool hasInstalledJob, string failure)
    {
        ArgumentNullException.ThrowIfNull(template);
        pendingObservation = new AuxiliaryTemplateObservation(
            AuxiliaryTemplateObservationKind.Cached, template, failure,
            RequiresInstallationCommit: !hasInstalledJob);

        if(hasInstalledJob)
            return TransitionTo(true, true, template, failure);

        // The startup template has not powered an installed job yet. Preserve why it
        // became fallback, but defer availability and the degraded episode until job
        // construction proves that the cached template is usable.
        startupFallbackFailure = failure;
        return AuxiliaryTemplateStateTransition.None;
    }

    public void ObserveFreshTemplate(AuxBlockTemplate template,
        bool hasInstalledJob)
    {
        ArgumentNullException.ThrowIfNull(template);
        pendingObservation = new AuxiliaryTemplateObservation(
            AuxiliaryTemplateObservationKind.Fresh, template, null,
            RequiresInstallationCommit: true);

        if(!hasInstalledJob && StartupTemplate != null &&
            ClassifyChange(StartupTemplate, template) == AuxiliaryTemplateChange.None)
        {
            // A fresh response reconfirmed the startup identity. A later parent-only
            // installation of that identity is healthy even if an older attempt to
            // supersede it failed.
            startupFallbackFailure = null;
        }
    }

    public AuxiliaryTemplateStateTransition JobInstallationFailed(
        AuxBlockTemplate installedTemplate)
    {
        var observation = TakePendingObservation();

        if(observation.Kind == AuxiliaryTemplateObservationKind.Fresh &&
            installedTemplate != null)
        {
            return ClassifyChange(installedTemplate, observation.Template) !=
                AuxiliaryTemplateChange.None
                ? TransitionTo(true, true, installedTemplate, ReplacementFailure)
                : TransitionTo(true, false, installedTemplate, null);
        }

        if(observation.RequiresInstallationCommit && installedTemplate == null)
        {
            if(observation.Kind == AuxiliaryTemplateObservationKind.Fresh &&
                StartupTemplate != null &&
                ClassifyChange(StartupTemplate, observation.Template) !=
                AuxiliaryTemplateChange.None)
            {
                // A newer identity could not replace the uninstalled startup cache.
                // If a parent-only event later installs the older cache, expose it as
                // fallback rather than healthy.
                startupFallbackFailure = InitialFreshFailure;
            }

            return TransitionTo(false, false, null, null);
        }

        return AuxiliaryTemplateStateTransition.None;
    }

    public AuxiliaryTemplateStateTransition JobInstalled(
        AuxBlockTemplate installedTemplate, bool firstJob)
    {
        ArgumentNullException.ThrowIfNull(installedTemplate);
        var observation = TakePendingObservation();
        AuxiliaryTemplateStateTransition transition;

        if(observation.Kind == AuxiliaryTemplateObservationKind.Fresh)
            transition = TransitionTo(true, false, installedTemplate, null);
        else if(observation.Kind == AuxiliaryTemplateObservationKind.Cached &&
            observation.RequiresInstallationCommit)
        {
            transition = TransitionTo(true, true, installedTemplate,
                observation.Failure);
        }
        else if(firstJob && ReferenceEquals(installedTemplate, StartupTemplate))
        {
            transition = startupFallbackFailure != null
                ? TransitionTo(true, true, installedTemplate,
                    startupFallbackFailure)
                : TransitionTo(true, false, installedTemplate, null);
        }
        else
            transition = AuxiliaryTemplateStateTransition.None;

        if(firstJob)
            ClearStartupTemplate();

        return transition;
    }

    public AuxiliaryTemplateStateTransition NoJobRequired(
        AuxBlockTemplate installedTemplate)
    {
        var observation = TakePendingObservation();
        return observation.Kind == AuxiliaryTemplateObservationKind.Fresh
            ? TransitionTo(true, false, installedTemplate, null)
            : AuxiliaryTemplateStateTransition.None;
    }

    public void AbandonPendingObservation()
    {
        pendingObservation = default;
    }

    internal static AuxiliaryTemplateChange ClassifyChange(
        AuxBlockTemplate previous, AuxBlockTemplate current)
    {
        if(current == null)
            return AuxiliaryTemplateChange.None;

        if(previous == null || previous.Height != current.Height ||
            !string.Equals(previous.PreviousBlockhash, current.PreviousBlockhash,
                StringComparison.OrdinalIgnoreCase))
            return AuxiliaryTemplateChange.ChainTip;

        return !string.Equals(previous.Hash, current.Hash,
            StringComparison.OrdinalIgnoreCase)
            ? AuxiliaryTemplateChange.Template
            : AuxiliaryTemplateChange.None;
    }

    private AuxiliaryTemplateStateTransition TransitionTo(bool available,
        bool degraded, AuxBlockTemplate template, string failure)
    {
        var transition = new AuxiliaryTemplateStateTransition(
            ShouldPublish: true,
            Available: available,
            Degraded: degraded,
            FallbackStarted: degraded && !Degraded,
            Recovered: !degraded && Degraded,
            Template: template,
            Failure: failure);

        Available = available;
        Degraded = degraded;
        return transition;
    }

    private AuxiliaryTemplateObservation TakePendingObservation()
    {
        var result = pendingObservation;
        pendingObservation = default;
        return result;
    }

    private void ClearStartupTemplate()
    {
        StartupTemplate = null;
        startupFallbackFailure = null;
    }

    private readonly record struct AuxiliaryTemplateObservation(
        AuxiliaryTemplateObservationKind Kind,
        AuxBlockTemplate Template,
        string Failure,
        bool RequiresInstallationCommit);
}
