using Microsoft.AspNetCore.Http.HttpResults;

namespace Watchtower.Agent.Web;

public static class StatusEndpoints
{
    /// <summary>
    /// <c>GET /api/status</c> - mode, open incident count, LLM budget utilisation and
    /// watchdog freshness.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <c>/healthz</c>, which answers "should Kubernetes restart me". This one
    /// answers "is the agent doing its job", and those diverge in exactly the interesting
    /// case: a pod whose budget is exhausted and whose alert path is dead is perfectly
    /// healthy by every liveness probe and is not watching anything.
    /// </para>
    /// <para>
    /// It always returns 200. An operator reaching for this during an outage needs the
    /// numbers, not a status code that makes their own tooling hide them.
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/status", GetStatusAsync).WithName("GetAgentStatus");

        return app;
    }

    private static async Task<Ok<AgentStatusView>> GetStatusAsync(
        IncidentQueries queries,
        CancellationToken ct) =>
        TypedResults.Ok(await queries.GetStatusAsync(ct));
}
