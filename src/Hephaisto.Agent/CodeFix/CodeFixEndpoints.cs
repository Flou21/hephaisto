using Microsoft.AspNetCore.Http.HttpResults;
using Hephaisto.Agent.Web;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.CodeFix;

public sealed record CodeFixDecisionRequest(string? DecidedBy, string? Reason);

public sealed record CodeFixRequestBody(string? RequestedBy);

public sealed record CodeFixDecisionResponse(string Outcome, string Message, CodeFixAttemptView? Attempt);

/// <summary>
/// <c>/api/codefixes</c> and <c>/api/incidents/{id}/codefix</c>. Reading is open to the console's
/// readers; asking for a code fix and deciding on one need the approver policy, exactly like
/// approving a cluster action.
/// </summary>
public static class CodeFixEndpoints
{
    public static IEndpointRouteBuilder MapCodeFixEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/codefixes", ListAsync).WithName("ListCodeFixes");
        app.MapGet("/api/codefixes/counts", CountsAsync).WithName("CountCodeFixes");
        app.MapGet("/api/codefixes/mode", ModeAsync).WithName("CodeFixMode");

        var incident = app.MapGroup("/api/incidents/{id:guid}/codefix");

        incident.MapGet("", ForIncidentAsync).WithName("GetIncidentCodeFix");

        // The manual door: a human asks for a code fix on an escalated incident.
        incident.MapPost("", RequestAsync)
            .WithName("RequestCodeFix")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        incident.MapPost("/{attemptId:guid}/approve", (Guid id, Guid attemptId, CodeFixDecisionRequest body, HttpContext http, CodeFixCoordinator c, CodeFixQueries q, CancellationToken ct)
                => DecideAsync(body, http, (actor, source, authenticated, reason) => c.DecideAsync(id, attemptId, true, actor, source, authenticated, reason, ct)))
            .WithName("ApproveCodeFix")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        incident.MapPost("/{attemptId:guid}/deny", (Guid id, Guid attemptId, CodeFixDecisionRequest body, HttpContext http, CodeFixCoordinator c, CodeFixQueries q, CancellationToken ct)
                => DecideAsync(body, http, (actor, source, authenticated, reason) => c.DecideAsync(id, attemptId, false, actor, source, authenticated, reason, ct)))
            .WithName("DenyCodeFix")
            .RequireAuthorization(AuthenticationExtensions.ApprovePolicy);

        return app;
    }

    private static async Task<Ok<IReadOnlyList<CodeFixAttemptView>>> ListAsync(
        string? state, int? limit, CodeFixQueries queries, CancellationToken ct)
    {
        CodeFixState? parsed = Enum.TryParse<CodeFixState>(state, ignoreCase: true, out var s) && !int.TryParse(state, out _) ? s : null;

        return TypedResults.Ok(await queries.ListAsync(parsed, limit ?? 100, ct));
    }

    private static async Task<Ok<CodeFixCounts>> CountsAsync(CodeFixQueries queries, CancellationToken ct) =>
        TypedResults.Ok(await queries.CountsAsync(ct));

    private static async Task<Ok<CodeFixModeView>> ModeAsync(CodeFixQueries queries, CancellationToken ct) =>
        TypedResults.Ok(await queries.ModeAsync(ct));

    private static async Task<Ok<IncidentCodeFixView>> ForIncidentAsync(Guid id, CodeFixQueries queries, CancellationToken ct) =>
        TypedResults.Ok(await queries.ForIncidentAsync(id, ct));

    private static async Task<Results<Ok<CodeFixDecisionResponse>, Conflict<CodeFixDecisionResponse>, ValidationProblem>> RequestAsync(
        Guid id, CodeFixRequestBody? body, HttpContext http, CodeFixCoordinator coordinator, CancellationToken ct)
    {
        var actor = ActorResolution.Resolve(http.User, body?.RequestedBy);

        if (string.IsNullOrWhiteSpace(actor))
            return Missing("requestedBy");

        var (verdict, attempt, refusal) = await coordinator.RequestAsync(id, actor, ct);

        return attempt is null
            ? TypedResults.Conflict(new CodeFixDecisionResponse(
                verdict is null ? "refused" : "declined", refusal ?? "not eligible", null))
            : TypedResults.Ok(new CodeFixDecisionResponse("started", attempt.State.ToString(), CodeFixQueries.View(attempt)));
    }

    /// <summary>
    /// One answer to a plan, whoever's attempt it is: who is deciding, from the token before the
    /// body, and the door's outcome as a status. <paramref name="decide"/> is the coordinator's
    /// door for an incident's attempt or for a work item's (<c>/api/workitems/{id}/codefix</c>).
    /// </summary>
    internal static async Task<Results<Ok<CodeFixDecisionResponse>, NotFound, Conflict<CodeFixDecisionResponse>, ForbidHttpResult, ValidationProblem>> DecideAsync(
        CodeFixDecisionRequest? body,
        HttpContext http,
        Func<string, ApprovalSource, bool, string?, Task<CodeFixDecisionResult>> decide)
    {
        var authenticated = http.User?.Identity?.IsAuthenticated == true;
        var actor = ActorResolution.Resolve(http.User, body?.DecidedBy);

        if (string.IsNullOrWhiteSpace(actor))
            return Missing("decidedBy");

        var result = await decide(actor, authenticated ? ApprovalSource.Oidc : ApprovalSource.Api, authenticated, body?.Reason);

        var response = new CodeFixDecisionResponse(
            result.Outcome.ToString(), result.Message, result.Attempt is null ? null : CodeFixQueries.View(result.Attempt));

        return result.Outcome switch
        {
            CodeFixDecisionOutcome.Done => TypedResults.Ok(response),
            CodeFixDecisionOutcome.NotFound => TypedResults.NotFound(),
            CodeFixDecisionOutcome.Forbidden when authenticated => TypedResults.Forbid(),
            _ => TypedResults.Conflict(response),
        };
    }

    private static ValidationProblem Missing(string field) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [$"{field} is required when nobody is signed in; the audit trail names a person, not a request."],
        });
}
