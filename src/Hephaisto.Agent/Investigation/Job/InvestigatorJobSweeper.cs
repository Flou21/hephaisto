using k8s;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.Kubernetes;

namespace Hephaisto.Agent.Investigations.Jobs;

/// <summary>
/// Removes investigator Jobs that no run in this process owns (v0.12.0 F5).
/// </summary>
/// <remarks>
/// <para>
/// A session lives only in this process, so after an agent restart every investigator Job still
/// running is an orphan: its tool calls are refused, it cannot conclude anything, and it holds a
/// node's CPU and the subscription's rate limit until its deadline. The driver notices the refusals
/// and ends on its own; this makes sure of it rather than trusting it, on start and then on a slow
/// poll - which also catches a Job the loop failed to delete for any other reason.
/// </para>
/// <para>
/// No new state. The Job carries its investigation id as a label, and "does a live session exist
/// for it" is the whole question. A session is opened before its Job is created, so a Job without
/// one is never a Job that is merely starting.
/// </para>
/// </remarks>
public sealed class InvestigatorJobSweeper(
    KubernetesApi api,
    InvestigationJobSessions sessions,
    IOptionsMonitor<InvestigationJobOptions> jobOptions,
    IOptionsMonitor<CodeFixOptions> codeFixOptions,
    ILogger<InvestigatorJobSweeper> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (jobOptions.CurrentValue.Enabled)
            {
                try
                {
                    await SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not sweep orphaned investigator Jobs; retrying in {Interval}", Interval);
                }
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        var ns = codeFixOptions.CurrentValue.Namespace;
        var jobs = await api.Batch.ListNamespacedJobAsync(
                ns, labelSelector: $"app.kubernetes.io/name={InvestigateJobSpec.AppLabel}", cancellationToken: ct)
            .ConfigureAwait(false);

        var swept = 0;

        foreach (var job in jobs.Items)
        {
            var finished = (job.Status?.Succeeded ?? 0) > 0 || (job.Status?.Failed ?? 0) > 0;

            if (finished)
                continue;

            var labels = job.Metadata.Labels ?? new Dictionary<string, string>();

            if (labels.TryGetValue(InvestigateJobSpec.InvestigationLabel, out var raw)
                && Guid.TryParse(raw, out var investigationId)
                && sessions.IsLive(investigationId))
            {
                continue;
            }

            logger.LogWarning(
                "Deleting investigator Job {Job}: no run in this agent owns it (an agent restart, or a Job its run could not remove)",
                job.Metadata.Name);

            await api.Batch.DeleteNamespacedJobAsync(
                    job.Metadata.Name, ns, propagationPolicy: "Background", cancellationToken: ct)
                .ConfigureAwait(false);

            swept++;
        }

        return swept;
    }
}
