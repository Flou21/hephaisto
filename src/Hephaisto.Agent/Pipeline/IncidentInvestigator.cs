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
internal sealed class EscalateOnlyInvestigator(
    ILogger<EscalateOnlyInvestigator> logger) : IIncidentInvestigator
{
    public Task InvestigateAsync(Guid incidentId, CancellationToken ct)
    {
        logger.LogWarning(
            "Incident {IncidentId} was not investigated: no IIncidentInvestigator is registered.",
            incidentId);

        return Task.CompletedTask;
    }
}
