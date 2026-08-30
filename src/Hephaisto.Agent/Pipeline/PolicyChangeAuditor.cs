using System.Text.Json;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// Writes an audit row whenever the policy configuration changes under a running agent.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PolicyOptions"/> is bound through <c>IOptionsMonitor</c> and hot-reloads from
/// the ConfigMap, which means the rules the agent enforces can change without a restart, a
/// deploy event, or anything at all in the incident record. Its own doc comment used to
/// promise that every reload wrote an audit row; nothing did, and the comment was corrected
/// rather than deleted so that this could close the gap instead of the gap closing itself.
/// </para>
/// <para>
/// A silent policy change is indistinguishable from an attack. Widening
/// <c>AllowedNamespaces</c> or adding a type to <c>AutoEnabledActionTypes</c> is the single
/// most consequential edit anyone can make to this system, and the audit trail is where a
/// reader reconstructs what the agent was allowed to do at the moment it did something.
/// </para>
/// </remarks>
public sealed class PolicyChangeAuditor(
    IOptionsMonitor<PolicyOptions> options,
    IServiceScopeFactory scopes,
    IClock clock,
    ILogger<PolicyChangeAuditor> logger) : IHostedService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IDisposable? subscription;
    private string? last;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        last = Fingerprint(options.CurrentValue);
        subscription = options.OnChange(OnChanged);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        subscription?.Dispose();
        subscription = null;

        return Task.CompletedTask;
    }

    private void OnChanged(PolicyOptions current)
    {
        var fingerprint = Fingerprint(current);

        // OnChange fires more than once for a single file write - the provider watches the
        // directory and a ConfigMap projection is a symlink swap, so two or three callbacks
        // for one edit is normal. Comparing the rendered value rather than counting callbacks
        // means the audit trail gets one row per actual change.
        if (fingerprint == last)
        {
            return;
        }

        var previous = last;
        last = fingerprint;

        logger.LogWarning(
            "Policy configuration changed under a running agent. Previous: {Previous}. Now: {Current}.",
            previous, fingerprint);

        // Fire and forget, deliberately: OnChange is invoked on the file-watcher's thread and
        // blocking it would stall every other options consumer in the process. Losing the row
        // to a database blip is survivable - the change is in the log line above either way -
        // and wedging configuration reload to guarantee it would not be.
        _ = WriteAsync(previous, fingerprint);
    }

    private async Task WriteAsync(string? previous, string current)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();

            await scope.ServiceProvider.GetRequiredService<IAuditRepository>()
                .AppendAsync(
                    new AuditEvent
                    {
                        At = clock.UtcNow,
                        Type = "policy.changed",
                        Actor = "configmap",
                        Summary = "policy configuration reloaded",
                        Detail = JsonSerializer.Serialize(new { previous, current }, Json),
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record the policy change in the audit trail.");
        }
    }

    /// <summary>
    /// The parts of the policy whose change is worth a row, rendered stably.
    /// </summary>
    /// <remarks>
    /// Sorted, because a set and a dictionary have no inherent order and an audit row per
    /// enumeration order would be noise indistinguishable from a real edit. Everything that
    /// widens what the agent may do is here; the numeric caps come along because tightening
    /// them is also a change someone may need to explain later.
    /// </remarks>
    private static string Fingerprint(PolicyOptions o) =>
        JsonSerializer.Serialize(
            new
            {
                allowedNamespaces = o.AllowedNamespaces.Order(StringComparer.Ordinal),
                autoEnabled = o.AutoEnabledActionTypes.Select(t => t.ToString()).Order(StringComparer.Ordinal),
                protectedNamespaces = o.ProtectedNamespaces.Order(StringComparer.Ordinal),
                protectedLabels = o.ProtectedLabels.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key}={kv.Value}"),
                requiredNamespaceLabel = o.RequiredNamespaceLabel,
                maxPodsPerAction = o.MaxPodsPerAction,
                maxWorkloadFraction = o.MaxWorkloadFraction,
                maxActionsPerIncident = o.MaxActionsPerIncident,
                maxActionsPerWorkloadPerHour = o.MaxActionsPerWorkloadPerHour,
                maxActionsPerHour = o.MaxActionsPerHour,
                maxActionsPerDay = o.MaxActionsPerDay,
                workloadCooldown = o.WorkloadCooldown,
                minPodAgeBeforeAction = o.MinPodAgeBeforeAction,
                clusterUnhealthyCeiling = o.ClusterUnhealthyCeiling,
                maintenanceWindows = o.MaintenanceWindows.Select(w => w.Describe()).Order(StringComparer.Ordinal),
            },
            Json);
}
