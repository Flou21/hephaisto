using Hephaisto.Core.Domain;

namespace Hephaisto.Core.Classification;

/// <summary>
/// How much a <see cref="SignalKind"/> claims to know about a failure, so that an incident
/// correlating several signals can be labelled by the one that knows most.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of backlog #70, and the finding there is not the one the symptom
/// suggests. The symptom was two fixtures classified as <see cref="SignalKind.ReadinessFlapping"/>
/// when they were an unschedulable pod and a bad image tag. The cause is that
/// <c>IncidentTriage.Attach</c> folds every later signal into the open incident while updating
/// only <c>LastSignalAt</c> and <c>Severity</c> - so an incident's kind is permanently whichever
/// signal happened to win the race to open it, and under load a different one wins.
/// </para>
/// <para>
/// <b>The severity precedent is the argument for this.</b> <c>Attach</c> already promotes
/// severity when a later signal reports worse, with a comment saying a warning that later turns
/// critical must not stay filed as a warning. Exactly the same is true of the kind, and it is
/// worse: <see cref="SignalKind"/> selects the runbook, so a stale kind hands the model
/// instructions written for a different failure.
/// </para>
/// <para>
/// <b>Ranking, not an ordering of severity.</b> A high rank means the signal identifies a
/// specific mechanism - the kernel OOM-killed it, the image tag does not exist, a Secret key is
/// missing. A low rank means the signal reports a state that many mechanisms produce. This is
/// not about which problem is worse: <see cref="SignalKind.NodePressure"/> is more serious than
/// <see cref="SignalKind.ConfigError"/> and less specific, and severity is tracked separately
/// and already handled.
/// </para>
/// <para>
/// <b>Upgrade only.</b> A rank never decreases an incident's kind. Once something has said
/// "the image tag does not exist", a later "the pod is not ready" adds nothing and must not
/// overwrite it.
/// </para>
/// </remarks>
public static class SignalKindSpecificity
{
    /// <summary>
    /// A rank in [0, 3]. Higher identifies a more specific mechanism.
    /// </summary>
    public static int Of(SignalKind kind) => kind switch
    {
        // 0 - says only that something is wrong.
        SignalKind.Unknown => 0,

        // 1 - a state with many possible mechanisms behind it. Each of these is a real
        // finding on its own and a poor label for an incident that has a better one.
        SignalKind.PodNotReady
            or SignalKind.ReadinessFlapping
            or SignalKind.TargetDown
            or SignalKind.ReplicaMismatch
            or SignalKind.RestartStorm => 1,

        // 2 - names the mechanism. These are what a runbook can actually act on.
        SignalKind.CrashLoopBackOff
            or SignalKind.OomKilled
            or SignalKind.ImagePullBackOff
            or SignalKind.Unschedulable
            or SignalKind.ConfigError
            or SignalKind.JobFailed
            or SignalKind.NodePressure
            or SignalKind.PvcNearlyFull
            or SignalKind.HighErrorRate
            or SignalKind.HighLatency => 2,

        // Self-monitoring kinds are ranked with the specific ones, but ShouldReplace never
        // compares them against a workload kind at all - see IsAboutHephaistoItself.
        SignalKind.ObservabilityDegraded
            or SignalKind.BudgetExhausted
            or SignalKind.Watchdog => 2,

        // A new member with no rank is treated as specific rather than generic. A kind
        // somebody bothered to add is more likely to name a mechanism than to be another way
        // of saying "not ready", and the failure mode of guessing high (an incident keeps a
        // label that is too precise) is milder than guessing low (a specific finding is
        // silently overwritten by a generic one).
        _ => 2,
    };

    /// <summary>
    /// A kind that describes Hephaisto's own health rather than a workload's.
    /// </summary>
    /// <remarks>
    /// These are incomparable with workload kinds in both directions. An incident about the
    /// alert path being broken is not the same incident as a workload that is unhealthy, and
    /// relabelling either into the other loses the one nobody else will report. In practice
    /// they rarely meet - the correlation key is namespace and owner, and these target
    /// Hephaisto - but "rarely" is not a property to rest a relabelling rule on.
    /// </remarks>
    public static bool IsAboutHephaistoItself(SignalKind kind) =>
        kind is SignalKind.ObservabilityDegraded
            or SignalKind.BudgetExhausted
            or SignalKind.Watchdog;

    /// <summary>
    /// Whether <paramref name="incoming"/> should replace <paramref name="current"/> as an
    /// incident's kind.
    /// </summary>
    /// <remarks>
    /// Strictly greater, so the first signal to reach a given rank keeps the label. Two
    /// equally specific kinds arriving in either order must produce the same incident, or the
    /// race this replaces has simply moved rather than been fixed.
    /// </remarks>
    public static bool ShouldReplace(SignalKind current, SignalKind incoming)
    {
        if (IsAboutHephaistoItself(current) != IsAboutHephaistoItself(incoming))
        {
            return false;
        }

        return Of(incoming) > Of(current);
    }
}
