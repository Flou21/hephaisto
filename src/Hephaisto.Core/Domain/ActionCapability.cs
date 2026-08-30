namespace Hephaisto.Core.Domain;

/// <summary>
/// Which members of <see cref="ActionType"/> this build can actually carry out.
/// </summary>
/// <remarks>
/// <para>
/// <b>One source of truth for two readers.</b> The executor needs it to fail closed before
/// making a call, and the planning prompt needs it so the model is not invited to propose
/// something that will be refused after a human has approved it. Those two used to disagree by
/// omission: <c>ActionExecutor.CanPerform</c> refused four action types, and the vocabulary
/// handed to the model listed all ten with no indication that any of them were unavailable.
/// </para>
/// <para>
/// The consequence was not theoretical. An operator could be shown an approval request for a
/// <c>RollbackDeployment</c>, approve it, and watch it fail at execution with
/// <c>outcome=unsupported</c> - a poor experience even though it is a safe one, and one that
/// spends the scarcest thing in an incident, which is a human's attention.
/// </para>
/// <para>
/// Being absent here is a statement about this build, not about the action. It is separate from
/// <see cref="IsPermanentlyDenied"/>, which is a statement about the action itself and will not
/// change.
/// </para>
/// </remarks>
public static class ActionCapability
{
    /// <summary>
    /// True when <c>ActionExecutor</c> has a typed implementation for this action.
    /// </summary>
    /// <remarks>
    /// <see cref="ActionType.SilenceAlert"/> is implemented and is <b>additionally</b> conditional
    /// at runtime on Alertmanager being configured - an install without it refuses the action
    /// before making a call rather than after failing one. That runtime half stays in the
    /// executor, because it is not a fact about the build.
    /// </remarks>
    public static bool IsImplemented(ActionType type) => type switch
    {
        ActionType.RestartPod
            or ActionType.RolloutRestart
            or ActionType.ScaleWorkload
            or ActionType.DeleteStuckJob
            or ActionType.DeleteFailedJobPods
            or ActionType.SilenceAlert => true,

        _ => false,
    };

    /// <summary>
    /// Actions that will never be performed, whatever a plan says and whoever approves it.
    /// </summary>
    /// <remarks>
    /// Listed in the vocabulary rather than hidden from it, so a plan naming one is recorded and
    /// refused with a reason instead of failing to deserialise into an unknown value and
    /// producing "no plan" with no explanation.
    /// </remarks>
    public static bool IsPermanentlyDenied(ActionType type) =>
        type is ActionType.DeletePvc or ActionType.DeleteWorkload;
}
