using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Investigations;

/// <summary>
/// What phase 2 emits, against a JSON response schema, with no tools available.
/// </summary>
/// <remarks>
/// <para>
/// This type is the security boundary between "what a language model produced" and "what
/// Hephaisto will consider doing". It is inert data: there is nothing on it that executes,
/// and every field is either a closed enum, a string that ends up in an audit row, or a
/// typed argument the executor parses itself. A prompt injection in a log line that survives
/// phase 1 gets to fill this in — and then meets the policy engine.
/// </para>
/// <para>
/// It is deliberately a separate type from <see cref="ActionPlan"/> rather than the domain
/// object with a schema attached. The draft is untrusted and its ids are strings the model
/// typed; the domain object is what the system believes. Mapping between them
/// (<see cref="TryToDomain"/>) is where the untrusted ids get resolved, and having that be an
/// explicit conversion rather than a deserialisation is what makes it reviewable.
/// </para>
/// </remarks>
public sealed class ActionPlanDraft
{
    [JsonPropertyName("summary")]
    [Description("One paragraph an on-call engineer can read in ten seconds: what you believe and what you intend.")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("no_action_required")]
    [Description("True when nothing should be done. This is the correct answer for most incidents.")]
    public bool NoActionRequired { get; set; }

    [JsonPropertyName("actions")]
    [Description("Proposed actions. Empty when no_action_required is true.")]
    public List<ActionDraft> Actions { get; set; } = [];
}

public sealed class ActionDraft
{
    [JsonPropertyName("type")]
    [Description("The action type. Only the listed values exist.")]
    [JsonConverter(typeof(JsonStringEnumConverter<ActionType>))]
    public ActionType Type { get; set; }

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    [Description("Kubernetes kind of the object to act on, e.g. Deployment, Pod, Job.")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments_json")]
    [Description("Typed arguments as a JSON object, e.g. {\"replicas\":3}. Never a shell command.")]
    public string? ArgumentsJson { get; set; }

    [JsonPropertyName("predicted_effect")]
    [Description(
        "What specifically becomes true afterwards. Recorded with the plan for a human to "
        + "judge the action against. Make it concrete and falsifiable.")]
    public string PredictedEffect { get; set; } = string.Empty;

    [JsonPropertyName("rollback_json")]
    [Description(
        "How to undo this, as a JSON object. An action with no rollback can never be executed "
        + "automatically, whatever its risk tier.")]
    public string? RollbackJson { get; set; }

    [JsonPropertyName("evidence_finding_ids")]
    [Description("Ids of the grounded findings that justify this action. An action citing none is rejected.")]
    public List<string> EvidenceFindingIds { get; set; } = [];

    [JsonPropertyName("risk")]
    [Description("Your own assessment. Advisory: the policy engine assigns the tier it acts on.")]
    [JsonConverter(typeof(JsonStringEnumConverter<RiskTier>))]
    public RiskTier Risk { get; set; } = RiskTier.Medium;
}

public static class ActionPlanDraftMapper
{
    /// <summary>
    /// Turns a verified draft into the domain <see cref="ActionPlan"/>.
    /// </summary>
    /// <remarks>
    /// Call only after <see cref="GroundingVerifier.VerifyPlan"/> has accepted the draft.
    /// This method assumes the finding ids resolve; it does not re-check, because doing the
    /// same check in two places is how the two copies end up disagreeing.
    /// </remarks>
    public static ActionPlan TryToDomain(
        ActionPlanDraft draft,
        Guid investigationId,
        Guid incidentId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var plan = new ActionPlan
        {
            InvestigationId = investigationId,
            Summary = draft.Summary,
            NoActionRequired = draft.NoActionRequired || draft.Actions.Count == 0,
            CreatedAt = now,
        };

        if (plan.NoActionRequired)
        {
            return plan;
        }

        foreach (var action in draft.Actions)
        {
            // An action with no target is not an action. These three fields are NOT NULL in
            // agent_actions (they are an owned TargetRef, and a target with a null namespace
            // is not a thing that can be acted on or audited), so a blank one fails the
            // INSERT - and because the whole investigation commits as one unit of work, it
            // takes the diagnosis, the findings and the evidence down with it.
            //
            // The declared type of ActionDraft.Namespace is non-nullable string with a
            // string.Empty initialiser, which is why this looks unnecessary. It is not:
            // System.Text.Json writes an explicit JSON `null` straight over that initialiser
            // without consulting nullable reference annotations, so a model that emits
            // {"namespace": null} produces exactly the state the type says cannot exist.
            //
            // Dropped rather than defaulted. There is no safe namespace to guess, and
            // inventing one would hand the policy engine a target the model never named.
            if (string.IsNullOrWhiteSpace(action.Namespace)
                || string.IsNullOrWhiteSpace(action.Kind)
                || string.IsNullOrWhiteSpace(action.Name))
            {
                continue;
            }

            plan.Actions.Add(new AgentAction
            {
                IncidentId = incidentId,
                ActionPlanId = plan.Id,
                Type = action.Type,
                Target = new TargetRef
                {
                    Namespace = action.Namespace,
                    Kind = action.Kind,
                    Name = action.Name,
                },
                Arguments = Sanitise(action.ArgumentsJson),
                Risk = action.Risk,
                PredictedEffect = action.PredictedEffect,
                RollbackSpec = Sanitise(action.RollbackJson),
                EvidenceFindingIds =
                    [.. action.EvidenceFindingIds
                        .Select(id => Guid.TryParse(id, out var g) ? g : (Guid?)null)
                        .Where(g => g is not null)
                        .Select(g => g!.Value)],

                // Left at their defaults on purpose: State is Proposed and Decision is Deny
                // until the policy engine says otherwise. A default-deny engine whose input
                // arrives pre-approved is not default-deny.
            });
        }

        // NoActionRequired is deliberately NOT recomputed here. It was set from the draft
        // before the loop, so a plan whose every action was dropped as malformed comes out
        // as "action required, zero actions" - which escalates to a human. Flipping it to
        // true would report "nothing to do" for an incident the model believed needed
        // remediation, which is the one wrong answer that looks like a right one.
        return plan;
    }

    /// <summary>
    /// Keeps only well-formed JSON objects.
    /// </summary>
    /// <remarks>
    /// The model can put anything in these strings, and they end up in a <c>jsonb</c> column
    /// and in front of the executor's parser. Rejecting malformed input here turns "the
    /// action row failed to insert" or "the executor threw on a string it expected to be an
    /// object" into an action with no arguments, which the policy engine handles as the
    /// ordinary case it is.
    /// </remarks>
    private static string? Sanitise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.GetRawText()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
