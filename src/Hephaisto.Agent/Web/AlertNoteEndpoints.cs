using Microsoft.AspNetCore.Mvc;

namespace Hephaisto.Agent.Web;

/// <summary>
/// What people learned about an alert name (#145): read it, add to it, rewrite it.
/// </summary>
/// <remarks>
/// <para>
/// Two levels on purpose. Anybody who can read the console may read a note and <b>add</b> a line
/// to it - the person who was paged at night is exactly who knows what was done, and making them
/// find an approver to write it down is how it does not get written down. <b>Rewriting</b> the
/// curated body decides what the team believes about an alert, and what the model is shown
/// beside its runbook, so it sits with approval like closing an incident does.
/// </para>
/// <para>
/// The name is in the path, URL-encoded. Alert names are identifiers in practice, but nothing
/// forces them to be, so the lookup takes whatever the path decodes to and
/// <see cref="Core.Domain.AlertNote.NormaliseName"/> decides whether it can key a note.
/// </para>
/// </remarks>
public static class AlertNoteEndpoints
{
    public static IEndpointRouteBuilder MapAlertNoteEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/alerts/{name}/note");

        group.MapGet("", GetAsync).WithName("GetAlertNote");

        group.MapPost("/entries", AddEntryAsync).WithName("AddAlertNoteEntry");

        group.MapPut("", SaveAsync)
            .WithName("SaveAlertNote")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        return app;
    }

    private static async Task<IResult> GetAsync(string name, IncidentQueries queries, CancellationToken ct)
    {
        var note = await queries.GetAlertNoteAsync(name, ct);

        return note is null
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"'{name}' is not an alert name."],
            })
            : TypedResults.Ok(note);
    }

    private static async Task<IResult> AddEntryAsync(
        string name,
        [FromBody] AddAlertNoteEntryRequest request,
        HttpContext http,
        IncidentQueries queries,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);

        // The token wins outright when there is one - see ActorResolution.
        var actor = ActorResolution.Resolve(http.User, request.Author);

        if (string.IsNullOrWhiteSpace(actor))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["author"] = ["Say who is writing this - attribution, not authentication."],
            });
        }

        return Render(await queries.AddAlertNoteEntryAsync(name, request.Text, request.IncidentId, actor, ct));
    }

    private static async Task<IResult> SaveAsync(
        string name,
        [FromBody] SaveAlertNoteRequest request,
        HttpContext http,
        IncidentQueries queries,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);

        var actor = ActorResolution.Resolve(http.User, request.UpdatedBy);

        if (string.IsNullOrWhiteSpace(actor))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["updatedBy"] = ["Say who is writing this - attribution, not authentication."],
            });
        }

        return Render(await queries.SaveAlertNoteBodyAsync(name, request.Body, actor, ct));
    }

    private static IResult Render(AlertNoteResult result) => result.Outcome switch
    {
        AlertNoteOutcome.Applied => TypedResults.Ok(result.Note),
        AlertNoteOutcome.Invalid => TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["note"] = [result.Detail ?? "invalid"],
        }),
        AlertNoteOutcome.ForbiddenActor => TypedResults.Conflict(result),
        _ => TypedResults.Json(result, statusCode: 500),
    };
}
