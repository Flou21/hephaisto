using Hephaisto.Agent.Mcp.Answers;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Safety;
using ModelContextProtocol;

namespace Hephaisto.Agent.Mcp;

/// <summary>
/// The six changes the MCP endpoint may make, and not one more (#157, F4).
/// </summary>
/// <remarks>
/// <para>
/// Acknowledge, assign, close, a line on an alert's note, feedback, re-investigate - each through
/// the console's own method on <see cref="IncidentQueries"/>, so the forbidden-actor rule, the
/// audit row and the live update are the ones every other door has. Nothing here approves,
/// denies, re-arms or sets a mode; a unit test holds this class to exactly these six.
/// </para>
/// <para>
/// <b>Who it is recorded as.</b> The caller's actor, always. A shared token (a gateway's) stands
/// for nobody in particular, so it records itself - <c>mcp/litellm</c> - and <c>onBehalfOf</c> is
/// kept as a claim beside it, never as the actor. Acknowledging stops the paging, so a shared
/// token must at least name the person who asked. A person's token that names somebody else is
/// refused rather than ignored: the model would tell its user one thing while the record says
/// another.
/// </para>
/// <para>
/// The rights are checked here as well as by the tools' policies, so a tool registered without
/// its attribute is still refused.
/// </para>
/// </remarks>
public sealed class McpIncidentActions(IncidentQueries queries, McpIncidentReader reader, IHttpContextAccessor http)
{
    public async Task<WriteResult> AcknowledgeAsync(McpCaller caller, Guid incidentId, string? onBehalfOf, CancellationToken ct)
    {
        var (actor, origin) = Authorise(caller, onBehalfOf, approver: false, claimRequired: true, "Acknowledging");
        var result = await queries.AcknowledgeIncidentAsync(incidentId, actor, ct, origin).ConfigureAwait(false);

        return await AnswerAsync(result, incidentId, actor, origin, "acknowledged", ct).ConfigureAwait(false);
    }

    public async Task<WriteResult> AssignAsync(McpCaller caller, Guid incidentId, string? assignee, string? onBehalfOf, CancellationToken ct)
    {
        var (actor, origin) = Authorise(caller, onBehalfOf, approver: false, claimRequired: false, "Assigning");

        if (string.IsNullOrWhiteSpace(assignee))
        {
            throw new McpException("Name the assignee: a person's name as the console shows it, me, or nobody to unassign.");
        }

        var who = assignee.Trim();
        string? target = who.ToLowerInvariant() switch
        {
            "nobody" or "none" or "unassign" => null,
            "me" => caller.Me ?? throw new McpException(
                $"assignee 'me' cannot be answered: this is a shared token ({caller.Actor}) and does not know who you are. Name the person."),
            _ => UntrustedText.Name(who) == who && !McpOptions.IsReserved(who)
                ? who
                : throw new McpException($"'{McpQuery.Echo(who)}' is not a person's name as the console shows it."),
        };

        var result = await queries.AssignIncidentAsync(incidentId, target, actor, ct, origin).ConfigureAwait(false);

        return await AnswerAsync(result, incidentId, actor, origin, target is null ? "unassigned" : $"assigned to {target}", ct).ConfigureAwait(false);
    }

    public async Task<WriteResult> CloseAsync(McpCaller caller, Guid incidentId, string? reason, string? onBehalfOf, CancellationToken ct)
    {
        var (actor, origin) = Authorise(caller, onBehalfOf, approver: true, claimRequired: false, "Closing");

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new McpException("Give the reason: why this needs no more attention, and what was done.");
        }

        var result = await queries.CloseIncidentAsync(incidentId, actor, UntrustedText.Clean(reason).Trim(), ct, origin).ConfigureAwait(false);

        return await AnswerAsync(result, incidentId, actor, origin, "closed", ct).ConfigureAwait(false);
    }

    public async Task<WriteResult> AddNoteEntryAsync(
        McpCaller caller, string alertName, string? text, Guid? incidentId, string? onBehalfOf, CancellationToken ct)
    {
        var (actor, origin) = Authorise(caller, onBehalfOf, approver: false, claimRequired: false, "Writing to a note");

        var result = await queries.AddAlertNoteEntryAsync(alertName, UntrustedText.Clean(text), incidentId, actor, ct, origin).ConfigureAwait(false);

        if (result.Outcome != AlertNoteOutcome.Applied)
        {
            throw new McpException($"The entry was not added: {result.Detail ?? result.Outcome.ToString()}");
        }

        return new WriteResult
        {
            Done = "added to the note",
            RecordedAs = McpText.Name(actor),
            ClaimedBy = McpText.NameOrNull(origin.ClaimedBy),
            ClaimVerified = false,
            Incident = incidentId is { } id ? await reader.RowAsync(id, ct).ConfigureAwait(false) : null,
            Note = "Marked as relayed by an agent. It is shown to people and never handed to the investigator as an operator's words.",
        };
    }

    public async Task<WriteResult> FeedbackAsync(
        McpCaller caller,
        Guid incidentId,
        bool helpful,
        bool? rootCauseCorrect,
        bool falsePositive,
        string? comment,
        string? onBehalfOf,
        CancellationToken ct)
    {
        var (actor, origin) = Authorise(caller, onBehalfOf, approver: false, claimRequired: false, "Giving feedback");

        var feedback = await queries.AddFeedbackAsync(
            incidentId, helpful, rootCauseCorrect, falsePositive, UntrustedText.Clean(comment), actor, ct, origin).ConfigureAwait(false)
            ?? throw new McpException($"No incident has the id {incidentId}.");

        return new WriteResult
        {
            Done = "feedback recorded",
            RecordedAs = McpText.Name(feedback.SubmittedBy),
            ClaimedBy = McpText.NameOrNull(origin.ClaimedBy),
            ClaimVerified = false,
            Incident = await reader.RowAsync(incidentId, ct).ConfigureAwait(false),
        };
    }

    public async Task<WriteResult> ReinvestigateAsync(McpCaller caller, Guid incidentId, string? onBehalfOf, CancellationToken ct)
    {
        var (actor, origin) = Authorise(caller, onBehalfOf, approver: true, claimRequired: false, "Re-investigating");

        var result = await queries.RequestReinvestigationAsync(incidentId, actor, ct, origin).ConfigureAwait(false);

        if (result.Outcome != ReinvestigateOutcome.Queued)
        {
            throw new McpException($"Not re-investigated ({result.Outcome}): {result.Detail ?? "no detail"}");
        }

        return new WriteResult
        {
            Done = "queued for another investigation",
            RecordedAs = McpText.Name(actor),
            ClaimedBy = McpText.NameOrNull(origin.ClaimedBy),
            ClaimVerified = false,
            Incident = await reader.RowAsync(incidentId, ct).ConfigureAwait(false),
            Note = "This spends the model budget. get_incident shows when the investigation is running and what it found.",
        };
    }

    private (string Actor, AuditOrigin Origin) Authorise(McpCaller caller, string? onBehalfOf, bool approver, bool claimRequired, string what)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.MayWrite)
        {
            throw new McpException($"{what} is refused: this token ({caller.Actor}) is read-only.");
        }

        if (approver && !caller.IsApprover)
        {
            throw new McpException($"{what} needs the approver role; this token ({caller.Actor}) is a reader.");
        }

        string? claim = null;

        if (!string.IsNullOrWhiteSpace(onBehalfOf))
        {
            var name = onBehalfOf.Trim();

            if (!caller.IsShared)
            {
                if (!string.Equals(name, caller.Actor, StringComparison.OrdinalIgnoreCase))
                {
                    throw new McpException(
                        $"This token acts as {caller.Actor} and cannot act on behalf of {McpQuery.Echo(name)}. "
                        + "Leave onBehalfOf out: it is recorded as the token's own person.");
                }
            }
            else if (UntrustedText.Name(name) != name || McpOptions.IsReserved(name))
            {
                throw new McpException($"onBehalfOf '{McpQuery.Echo(name)}' is not a person's name as the console shows it.");
            }
            else
            {
                claim = name;
            }
        }
        else if (caller.IsShared && claimRequired)
        {
            throw new McpException(
                $"{what} through a shared token ({caller.Actor}) needs onBehalfOf: the name of the person who asked. "
                + "It stops the paging, so somebody has to be named - it is recorded as a claim, beside the token.");
        }

        var client = http.HttpContext?.Request.Headers.UserAgent.ToString();

        return (caller.Actor, new AuditOrigin
        {
            Source = AuditOrigin.Mcp,
            Token = caller.Token,
            TokenKind = caller.Kind,
            Role = caller.Role,
            Client = string.IsNullOrWhiteSpace(client) ? null : client[..Math.Min(client.Length, 120)],
            ClaimedBy = claim,
            ClaimVerified = false,
        });
    }

    private async Task<WriteResult> AnswerAsync(
        LifecycleResult result, Guid incidentId, string actor, AuditOrigin origin, string done, CancellationToken ct) =>
        result.Outcome switch
        {
            LifecycleOutcome.Applied => new WriteResult
            {
                Done = done,
                RecordedAs = McpText.Name(actor),
                ClaimedBy = McpText.NameOrNull(origin.ClaimedBy),
                ClaimVerified = false,
                Incident = await reader.RowAsync(incidentId, ct).ConfigureAwait(false),
            },
            LifecycleOutcome.NotFound => throw new McpException($"No incident has the id {incidentId}."),
            _ => throw new McpException($"Nothing changed ({result.Outcome}): {result.Detail ?? "the incident is not in a state this applies to"}"),
        };
}
