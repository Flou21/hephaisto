using Microsoft.AspNetCore.Http.HttpResults;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.WorkItems;

/// <summary>
/// <c>/api/workitems</c>: what Hephaisto was handed. Read only, behind the console's read policy
/// like <c>/api/codefixes</c> - a work item begins and ends on the issue, not here.
/// </summary>
public static class WorkItemEndpoints
{
    public static IEndpointRouteBuilder MapWorkItemEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/workitems", ListAsync).WithName("ListWorkItems");
        app.MapGet("/api/workitems/{id:guid}", GetAsync).WithName("GetWorkItem");

        return app;
    }

    /// <summary>
    /// <c>?state=Taken|Done|Cancelled|any</c>, default <c>Taken</c>: what the agent holds now.
    /// History is asked for, as on <c>/api/incidents</c>.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<WorkItemView>>, ValidationProblem>> ListAsync(
        string? state, int? limit, WorkItemQueries queries, CancellationToken ct)
    {
        WorkItemState? parsed = WorkItemState.Taken;

        if (string.Equals(state, "any", StringComparison.OrdinalIgnoreCase))
        {
            parsed = null;
        }
        else if (!string.IsNullOrWhiteSpace(state))
        {
            // A number parses as an enum; "1" is not a state anybody meant.
            if (!Enum.TryParse<WorkItemState>(state, ignoreCase: true, out var s) || int.TryParse(state, out _))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["state"] = [$"'{state}' is not a WorkItemState. Use one of: {string.Join(", ", Enum.GetNames<WorkItemState>())}, or 'any'."],
                });
            }

            parsed = s;
        }

        return TypedResults.Ok(await queries.ListAsync(parsed, limit ?? 100, ct));
    }

    private static async Task<Results<Ok<WorkItemView>, NotFound>> GetAsync(Guid id, WorkItemQueries queries, CancellationToken ct) =>
        await queries.GetAsync(id, ct) is { } item ? TypedResults.Ok(item) : TypedResults.NotFound();
}
