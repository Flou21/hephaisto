using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Pipeline;

/// <summary>
/// The seam between "an incident is worth investigating" and the LLM loop that does it.
/// </summary>
/// <remarks>
/// Kept as an interface so the ingest pipeline has no compile-time dependency on the model
/// stack at all. That is not tidiness: it means detection, dedup, correlation and the UI keep
/// working when the LLM is unavailable, out of budget, or deliberately switched off. An agent
/// that stops noticing problems because its language model is down is worse than useless -
/// it is a monitoring system that fails silently.
/// </remarks>
public interface IIncidentInvestigator
{
    Task InvestigateAsync(Guid incidentId, CancellationToken ct);
}

/// <summary>
/// Used until the investigation stream is wired, and whenever the LLM is intentionally off.
/// Escalating is the honest response: a human is told there is a problem and that nothing
/// diagnosed it, which is exactly what happened.
/// </summary>
/// <remarks>
/// <b>It did not escalate</b> until 2026-08-30 (backlog #14). The name said so, the doc comment
/// above said so, and the body logged a warning and returned - so the incident was left in
/// exactly the state the caller found it, nothing transitioned, and nobody was told. Latent,
/// because it is registered with <c>TryAdd</c> and only reachable if the LLM stack was never
/// registered, which no shipped configuration does.
///
/// It stopped being harmless in v0.3.0. Escalation is now the thing that reaches a person, so a
/// fallback investigator that silently does nothing is the exact failure this release exists to
/// remove - and the one install that reaches it is the one running with no model, where every
/// incident depends on it.
/// </remarks>
internal sealed class EscalateOnlyInvestigator(
    IncidentTriage triage,
    ILogger<EscalateOnlyInvestigator> logger) : IIncidentInvestigator
{
    public async Task InvestigateAsync(Guid incidentId, CancellationToken ct)
    {
        logger.LogWarning(
            "Incident {IncidentId} was not investigated: no IIncidentInvestigator is registered. "
                + "Escalating it, because an undiagnosed problem is still a problem.",
            incidentId);

        // InvestigationFailed rather than NoPlanProduced: no plan was produced because no
        // investigation ran at all, and the distinction is what tells a reader whether to look
        // for a bad diagnosis or a missing model.
        await triage
            .EscalateAsync(incidentId, EscalationReason.InvestigationFailed, ct)
            .ConfigureAwait(false);
    }
}
