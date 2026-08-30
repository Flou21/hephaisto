using k8s;
using k8s.Models;
using Hephaisto.Agent.Kubernetes;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// Writes what Hephaisto did back onto the object it did it to, as a Kubernetes Event.
/// </summary>
/// <remarks>
/// <para>
/// The audit trail lives in Postgres and is the authoritative record. This is not that. It
/// exists because an on-call engineer looking at a pod that restarted three minutes ago runs
/// <c>kubectl describe pod</c>, and what they need to see there is "hephaisto restarted this,
/// and here is why" - not an empty event list and a mystery. Anything that makes them go and
/// find a web UI first has already failed them.
/// </para>
/// <para>
/// Best effort, always. The action has already happened by the time this runs; a failure to
/// annotate must never turn a successful remediation into a failed one, and the durable record
/// is somewhere else entirely. Every failure here is logged and swallowed - the same shape as
/// GrafanaAnnotator, and for the same reason.
/// </para>
/// <para>
/// <c>create</c> on events is the only verb in the write Role that changes nothing about how
/// the cluster runs, which is why it can be granted alongside the destructive ones without
/// widening the blast radius at all.
/// </para>
/// </remarks>
public sealed class ActionEventMirror(
    KubernetesApi api,
    IClock clock,
    ILogger<ActionEventMirror> logger)
{
    private const string Component = "hephaisto";

    public async Task MirrorAsync(AgentAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);

        var target = action.Target;
        var now = clock.UtcNow;

        try
        {
            var @event = new Corev1Event
            {
                Metadata = new V1ObjectMeta
                {
                    // GenerateName, because two actions on one object within the same second
                    // are entirely possible and a name collision would lose the second one.
                    GenerateName = "hephaisto-",
                    NamespaceProperty = target.Namespace,
                },
                InvolvedObject = new V1ObjectReference
                {
                    Kind = target.Kind,
                    Name = target.Name,
                    NamespaceProperty = target.Namespace,
                    Uid = target.Uid,
                    ApiVersion = ApiVersionFor(target.Kind),
                },

                // CamelCase, like every other reason in the API, so it reads as one of them in
                // a describe output rather than as something bolted on.
                Reason = $"Hephaisto{action.Type}",
                Message = Message(action),

                // Normal, not Warning. The fault was the warning; this is the response to it,
                // and colouring it as a problem would train people to ignore the wrong line.
                Type = "Normal",
                Action = action.Type.ToString(),
                ReportingComponent = Component,
                ReportingInstance = Environment.MachineName,
                Source = new V1EventSource { Component = Component },
                EventTime = now.UtcDateTime,
                FirstTimestamp = now.UtcDateTime,
                LastTimestamp = now.UtcDateTime,
                Count = 1,
            };

            await api.Core
                .CreateNamespacedEventAsync(@event, target.Namespace, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Could not write a Kubernetes Event for {Action} on {Workload}.",
                action.Type, target.WorkloadKey);
        }
    }

    /// <summary>
    /// One line, in the voice of the thing that did it, naming the incident.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It carries the predicted effect rather than the diagnosis, because that is what is on
    /// the action and it is the more useful half here anyway: it tells the reader what to
    /// check next. The full reasoning is a database row away and the id to find it is in the
    /// line.
    /// </para>
    /// <para>
    /// An id rather than a URL. This agent does not know its own external address, and an
    /// event carrying a link that resolves to nothing on the reader's network is worse than
    /// one carrying something they can search for.
    /// </para>
    /// </remarks>
    private static string Message(AgentAction action)
    {
        var expected = string.IsNullOrWhiteSpace(action.PredictedEffect)
            ? string.Empty
            : $" Expected: {action.PredictedEffect.Trim()}";

        var approver = string.IsNullOrWhiteSpace(action.ApprovedBy)
            ? "automatically"
            : $"approved by {action.ApprovedBy}";

        return $"hephaisto performed {action.Type} on {action.Target.Kind}/{action.Target.Name}, "
            + $"{approver}, for incident {action.IncidentId}.{expected}";
    }

    private static string ApiVersionFor(string kind) => kind switch
    {
        "Deployment" or "StatefulSet" or "DaemonSet" or "ReplicaSet" => "apps/v1",
        "Job" or "CronJob" => "batch/v1",
        _ => "v1",
    };
}
