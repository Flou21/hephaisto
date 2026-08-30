using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Web;

public static class IncidentEndpoints
{
    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/incidents");

        // The literal route is registered before the parameterised one and the parameter
        // carries a :guid constraint, so "search" can never be matched as an incident id -
        // belt and braces, because getting this wrong turns the search page into a 404 that
        // only reproduces when someone types a query.
        group.MapGet("/search", SearchAsync).WithName("SearchIncidents");
        group.MapGet("", ListAsync).WithName("ListIncidents");
        group.MapGet("/{id:guid}", GetAsync).WithName("GetIncident");
        group.MapPost("/{id:guid}/feedback", SubmitFeedbackAsync).WithName("SubmitIncidentFeedback");
        group.MapPost("/{id:guid}/reinvestigate", ReinvestigateAsync).WithName("ReinvestigateIncident");

        // Approval. Two routes rather than one with a boolean, so a truncated or mistyped body
        // cannot turn a denial into an approval - the verb is in the path, where it is visible
        // in an access log and cannot be defaulted.
        group.MapPost("/{id:guid}/actions/{actionId:guid}/approve", ApproveAsync).WithName("ApproveAction");
        group.MapPost("/{id:guid}/actions/{actionId:guid}/deny", DenyAsync).WithName("DenyAction");

        // Outside the incident group: a blob is addressed by its own id, and the step that
        // points at it may well have outlived it.
        app.MapGet("/api/evidence-blobs/{id:guid}", GetBlobAsync).WithName("GetEvidenceBlob");

        return app;
    }

    /// <summary>
    /// <c>GET /api/evidence-blobs/{id}</c> - the untruncated result behind an investigation
    /// step, which is what <c>evidence://step/{id}</c> resolves to.
    /// </summary>
    /// <remarks>
    /// A 404 here is an expected outcome rather than an error: blobs are retained for 30 days
    /// and the step log is kept, so a pointer into an expired blob is the designed state.
    /// The step's own <c>ResultDigest</c> - which is what the model actually saw and what
    /// grounding was checked against - is still on the incident page.
    /// </remarks>
    private static async Task<Results<Ok<EvidenceBlobView>, NotFound>> GetBlobAsync(
        Guid id,
        IncidentQueries queries,
        CancellationToken ct)
    {
        var blob = await queries.GetBlobAsync(id, int.MaxValue, ct);

        return blob is null ? TypedResults.NotFound() : TypedResults.Ok(blob);
    }

    /// <summary>
    /// <c>GET /api/incidents?state=&amp;kind=&amp;namespace=&amp;limit=</c>
    /// </summary>
    /// <remarks>
    /// No <c>state</c> means open incidents only, not everything. The default view of an
    /// incident console is "what is wrong now"; defaulting to all history would make the
    /// first page of a busy cluster useless and get slower every week.
    /// </remarks>
    private static async Task<Results<Ok<IReadOnlyList<IncidentListItem>>, ValidationProblem>> ListAsync(
        IncidentQueries queries,
        CancellationToken ct,
        [FromQuery] string? state = null,
        [FromQuery] string? kind = null,
        [FromQuery(Name = "namespace")] string? ns = null,
        [FromQuery] int? limit = null)
    {
        var errors = new Dictionary<string, string[]>();

        IncidentState? parsedState = null;

        if (!string.IsNullOrWhiteSpace(state) && !string.Equals(state, "open", StringComparison.OrdinalIgnoreCase))
        {
            if (Enum.TryParse<IncidentState>(state, ignoreCase: true, out var s))
            {
                parsedState = s;
            }
            else
            {
                errors["state"] = [$"'{state}' is not an IncidentState. Use one of: {Names<IncidentState>()}, or 'open'."];
            }
        }

        SignalKind? parsedKind = null;

        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (Enum.TryParse<SignalKind>(kind, ignoreCase: true, out var k))
            {
                parsedKind = k;
            }
            else
            {
                errors["kind"] = [$"'{kind}' is not a SignalKind. Use one of: {Names<SignalKind>()}."];
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await queries.ListAsync(
            new IncidentListQuery
            {
                State = parsedState,
                OpenOnly = parsedState is null,
                Kind = parsedKind,
                Namespace = ns,
                Limit = limit ?? 100,
            },
            ct);

        return TypedResults.Ok(result);
    }

    /// <summary>
    /// <c>GET /api/incidents/{id}</c> - the whole incident: signals, transitions, every
    /// investigation with its steps, findings, evidence and plan, actions and feedback.
    /// </summary>
    private static async Task<Results<Ok<IncidentDetailView>, NotFound>> GetAsync(
        Guid id,
        IncidentQueries queries,
        CancellationToken ct)
    {
        var incident = await queries.GetDetailAsync(id, ct);

        return incident is null ? TypedResults.NotFound() : TypedResults.Ok(incident);
    }

    /// <summary>
    /// <c>GET /api/incidents/search?q=</c> - hybrid retrieval over incident digests.
    /// </summary>
    /// <remarks>
    /// All three arms - full text, vector similarity, trigram - so an SRE query gets its
    /// literals from the lexical and trigram arms and its paraphrase from the vector one. The
    /// response carries which arms actually ran, because an arm that ran and matched nothing is
    /// otherwise indistinguishable from an arm that never ran.
    /// </remarks>
    private static async Task<Ok<IncidentSearchResult>> SearchAsync(
        IncidentQueries queries,
        CancellationToken ct,
        [FromQuery] string? q = null,
        [FromQuery(Name = "namespace")] string? ns = null,
        [FromQuery] bool resolvedOnly = false,
        [FromQuery] int limit = 25)
    {
        var filter = new SearchFilter
        {
            Namespaces = string.IsNullOrWhiteSpace(ns) ? null : [ns.Trim()],
            ResolvedOnly = resolvedOnly,
        };

        return TypedResults.Ok(await queries.SearchAsync(q ?? string.Empty, filter, limit, ct));
    }

    /// <summary>
    /// <c>POST /api/incidents/{id}/feedback</c>
    /// </summary>
    private static async Task<Results<Ok<FeedbackView>, NotFound, ValidationProblem>> SubmitFeedbackAsync(
        Guid id,
        [FromBody] FeedbackRequest request,
        IncidentQueries queries,
        CancellationToken ct)
    {
        // SubmittedBy is required and non-empty. It is attribution, not authentication -
        // nothing verifies it - but an unattributed verdict is worse than useless: the false
        // positive rate is the only quality number here that is not self-assessed, and one
        // whose entries cannot be traced to a person cannot be questioned or corrected.
        if (string.IsNullOrWhiteSpace(request.SubmittedBy))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["submittedBy"] = ["Required. Who is submitting this verdict - attribution, not authentication."],
            });
        }

        var feedback = await queries.AddFeedbackAsync(
            id,
            request.Helpful,
            request.RootCauseCorrect,
            request.FalsePositive,
            request.Comment,
            request.SubmittedBy,
            ct);

        return feedback is null ? TypedResults.NotFound() : TypedResults.Ok(feedback);
    }

    /// <summary>
    /// <c>POST /api/incidents/{id}/reinvestigate</c> - put an undiagnosed incident back on the
    /// investigation queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 202 rather than 200: the work is queued, not done. The caller polls the incident, or
    /// watches it live - the investigation takes minutes.
    /// </para>
    /// <para>
    /// The rejections map onto distinct codes on purpose. 409 for "already running" and "not a
    /// state you can retry from" is a conflict with the incident's current state and will
    /// resolve on its own; 503 for a saturated queue or an Off kill switch is the system
    /// declining to start work right now. A single 400 for all four would make the button in
    /// the console unable to say anything useful.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ReinvestigateAsync(
        Guid id,
        [FromBody] ReinvestigateRequest request,
        IncidentQueries queries,
        CancellationToken ct)
    {
        // Attribution, not authentication - the same contract as feedback. A retry spends real
        // tokens, so an anonymous one is an anonymous line on the invoice.
        if (string.IsNullOrWhiteSpace(request.RequestedBy))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["requestedBy"] = ["Required. Who is asking for another attempt - attribution, not authentication."],
            });
        }

        var result = await queries.RequestReinvestigationAsync(id, request.RequestedBy, ct);

        return result.Outcome switch
        {
            ReinvestigateOutcome.Queued => TypedResults.Accepted((string?)null, result),
            ReinvestigateOutcome.NotFound => TypedResults.NotFound(),
            ReinvestigateOutcome.AlreadyRunning => TypedResults.Conflict(result),
            ReinvestigateOutcome.IllegalState => TypedResults.Conflict(result),
            ReinvestigateOutcome.QueueFull => TypedResults.Json(result, statusCode: 503),
            ReinvestigateOutcome.Disabled => TypedResults.Json(result, statusCode: 503),
            _ => TypedResults.Json(result, statusCode: 500),
        };
    }

    private static Task<Results<Ok<ApprovalResult>, NotFound, Conflict<ApprovalResult>, ValidationProblem>>
        ApproveAsync(Guid id, Guid actionId, ApprovalRequest request, IncidentQueries queries, CancellationToken ct) =>
        DecideAsync(id, actionId, approve: true, request, queries, ct);

    private static Task<Results<Ok<ApprovalResult>, NotFound, Conflict<ApprovalResult>, ValidationProblem>>
        DenyAsync(Guid id, Guid actionId, ApprovalRequest request, IncidentQueries queries, CancellationToken ct) =>
        DecideAsync(id, actionId, approve: false, request, queries, ct);

    private static async Task<Results<Ok<ApprovalResult>, NotFound, Conflict<ApprovalResult>, ValidationProblem>>
        DecideAsync(
            Guid id,
            Guid actionId,
            bool approve,
            ApprovalRequest request,
            IncidentQueries queries,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DecidedBy))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["decidedBy"] =
                [
                    "Say who is approving this. It is written to approved_by verbatim and is the "
                    + "only record of who authorised a change to the cluster.",
                ],
            });
        }

        // Api, not Ui, and set here rather than taken from the body: a caller must not be able
        // to describe its own approval as having come from somewhere else.
        var result = await queries.DecideActionAsync(
            id, actionId, approve, request.DecidedBy, ApprovalSource.Api, ct);

        return result.Outcome switch
        {
            ApprovalOutcome.Executed => TypedResults.Ok(result),
            ApprovalOutcome.Denied => TypedResults.Ok(result),
            ApprovalOutcome.NotFound => TypedResults.NotFound(),
            ApprovalOutcome.NotAwaitingApproval => TypedResults.Conflict(result),
            ApprovalOutcome.ForbiddenActor => TypedResults.Conflict(result),

            // Approved by a human, then refused by admission or failed against the API. A 200
            // would report a change that did not happen; a 404 would lose the approval that
            // did. Conflict, with the reason.
            _ => TypedResults.Conflict(result),
        };
    }

    private static string Names<T>() where T : struct, Enum => string.Join(", ", Enum.GetNames<T>());
}

/// <summary>A human approving or denying one proposed action.</summary>
public sealed record ApprovalRequest
{
    /// <summary>
    /// Required and non-empty. Attribution, not authentication, until OIDC lands - and the
    /// risk to watch is habituation rather than impersonation.
    /// </summary>
    public required string DecidedBy { get; init; }
}

/// <summary>A human's verdict on an incident.</summary>
public sealed record FeedbackRequest
{
    /// <summary>Was the investigation useful and correct overall.</summary>
    public required bool Helpful { get; init; }

    /// <summary>Null when the reader has no opinion. Deliberately separate from
    /// <see cref="Helpful"/>: a useful investigation can still name the wrong cause, and
    /// collapsing the two loses the distinction that makes the answer worth collecting.</summary>
    public bool? RootCauseCorrect { get; init; }

    /// <summary>The incident should never have opened at all.</summary>
    public bool FalsePositive { get; init; }

    public string? Comment { get; init; }

    /// <summary>Required and non-empty. Validated in the handler rather than by an attribute
    /// so the message can say what it is for.</summary>
    public required string SubmittedBy { get; init; }
}

/// <summary>A human asking for another investigation attempt.</summary>
public sealed record ReinvestigateRequest
{
    /// <summary>Required and non-empty. See the handler for why.</summary>
    public required string RequestedBy { get; init; }
}
