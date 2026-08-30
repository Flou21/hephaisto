using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Hephaisto.Agent.Web;

/// <summary>
/// The one write the kill switch exposes: clearing the runaway latch.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is deliberately no endpoint that sets the mode.</b> The mode is a Helm value; it
/// reaches the pod as an environment variable and a projected ConfigMap, so changing it is a
/// reviewed commit and a rollout. An HTTP route that could raise autonomy would be a second,
/// unreviewed source of truth for the most consequential setting in the system - and on an
/// unauthenticated surface, it would be the most dangerous route in the process.
/// </para>
/// <para>
/// Re-arming is different in kind, not just degree. It cannot name a mode and it cannot lift
/// the agent above the ceiling the deployment already grants: it removes a restriction the
/// agent placed on itself. The worst it can do is return the agent to the state its own
/// configuration says it should be in, which a rollout would do anyway.
/// </para>
/// </remarks>
public static class ModeEndpoints
{
    public static IEndpointRouteBuilder MapModeEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/mode/re-arm", ReArmAsync).WithName("ReArmAgent");

        return app;
    }

    private static async Task<Results<Ok<ReArmResponse>, Conflict<ReArmResponse>, ValidationProblem>> ReArmAsync(
        ReArmRequest request,
        IncidentQueries queries,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Attribution, not authentication - the same trade the feedback and reinvestigate
        // routes already make, and the same one ApprovedBy makes. It is worth demanding
        // anyway: the audit row for "autonomy came back" should name somebody.
        if (string.IsNullOrWhiteSpace(request.Actor))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["actor"] = ["Say who is clearing the latch. It goes on the audit row verbatim."],
            });
        }

        var result = await queries.ReArmAsync(request.Actor, ct);

        var response = new ReArmResponse
        {
            Outcome = result.Outcome.ToString(),
            Detail = result.Detail,
            EffectiveMode = result.EffectiveMode?.ToString(),
        };

        // 409 for "nothing was latched": the request was well-formed and the server declined
        // to pretend it did something. A 200 here would make a no-op indistinguishable from a
        // clear in any log or script reading this.
        return result.Accepted
            ? TypedResults.Ok(response)
            : TypedResults.Conflict(response);
    }
}

public sealed record ReArmRequest
{
    /// <summary>Who is clearing it. Free text, recorded verbatim on the audit row.</summary>
    [Required]
    [StringLength(120, MinimumLength = 1)]
    public required string Actor { get; init; }
}

public sealed record ReArmResponse
{
    public required string Outcome { get; init; }

    public string? Detail { get; init; }

    /// <summary>What the agent is running as now the latch is gone - the deployment's ceiling.</summary>
    public string? EffectiveMode { get; init; }
}
