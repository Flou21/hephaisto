using System.Text;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Investigations;

/// <summary>Why a citation, a finding or a plan was thrown away. Becomes the metric's <c>reason</c> tag.</summary>
public enum GroundingRejectionReason
{
    /// <summary>The cited step id does not parse, or names no step at all.</summary>
    UnknownStep = 0,

    /// <summary>The step exists but belongs to a different investigation.</summary>
    ForeignStep = 1,

    /// <summary>The step has no stored result to check against - a failed or refused tool call.</summary>
    NoDigest = 2,

    /// <summary>The excerpt is empty or whitespace, so it asserts nothing.</summary>
    EmptyExcerpt = 3,

    /// <summary>The excerpt is not a substring of what that step returned. The important one.</summary>
    ExcerptNotFound = 4,

    /// <summary>Every citation the finding rested on was dropped.</summary>
    FindingWithoutEvidence = 5,

    /// <summary>An action cites a finding that no longer exists.</summary>
    ActionCitesDroppedFinding = 6,

    /// <summary>An action cites nothing at all.</summary>
    ActionWithoutEvidence = 7,
}

public sealed record GroundingRejection(
    GroundingRejectionReason Reason,
    string Detail,
    Guid? FindingId = null,
    Guid? StepId = null);

public sealed record GroundingResult(
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<GroundingRejection> Rejections)
{
    /// <summary>True when at least one finding survived with at least one citation.</summary>
    public bool HasGroundedFindings => Findings.Count > 0;
}

public sealed record PlanGroundingResult(
    bool Accepted,
    IReadOnlyList<GroundingRejection> Rejections);

/// <summary>
/// Checks that every claim is traceable to something a tool actually returned.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a runtime invariant, not a request in the prompt.</b> Asking a model to cite
/// honestly does not work — a model that hallucinates a plausible log line will also
/// sincerely believe it quoted one, so its self-report is worth nothing. What works is
/// checking, afterwards, in code that cannot be talked round.
/// </para>
/// <para>
/// <b>The comparison is against <see cref="InvestigationStep.ResultDigest"/>, never the raw
/// blob.</b> The digest is what the model was actually shown; the blob is the untruncated
/// original kept for the audit. Checking against the blob would accept an excerpt from a
/// region the digester cut — text the model could not have read and therefore did not read,
/// which means it invented something that happened to be true. That is not evidence, it is a
/// coincidence, and accepting it would defeat the entire mechanism.
/// </para>
/// <para>
/// Whitespace is normalised on both sides before comparing: runs of whitespace collapse to a
/// single space and the ends are trimmed. Everything else is compared exactly, case included.
/// The reasoning is that reflowing a quote is a formatting artefact of passing text through a
/// JSON field, while changing a word or a capital is a change of meaning — <c>ERROR</c> and
/// <c>error</c> are different log levels.
/// </para>
/// <para>
/// Pure and side-effect free by design, so it can be exhaustively unit-tested. The metric is
/// emitted by the caller from the rejections it returns.
/// </para>
/// </remarks>
public static class GroundingVerifier
{
    /// <summary>
    /// Drops failing evidence, then drops findings left with none.
    /// </summary>
    /// <param name="investigationId">The investigation whose steps may be cited. Nothing else may.</param>
    /// <param name="steps">Every step of that investigation.</param>
    /// <param name="findings">What the model claimed, unverified.</param>
    public static GroundingResult Verify(
        Guid investigationId,
        IReadOnlyList<InvestigationStep> steps,
        IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(findings);

        var byId = new Dictionary<Guid, InvestigationStep>();

        foreach (var step in steps)
        {
            byId[step.Id] = step;
        }

        var survivors = new List<Finding>();
        var rejections = new List<GroundingRejection>();

        foreach (var finding in findings)
        {
            var kept = new List<Evidence>();

            foreach (var evidence in finding.Evidence)
            {
                var rejection = Check(investigationId, byId, finding, evidence);

                if (rejection is null)
                {
                    kept.Add(evidence);
                }
                else
                {
                    rejections.Add(rejection);
                }
            }

            if (kept.Count == 0)
            {
                // No exception for a confident, well-reasoned finding. Reasoning is not
                // evidence, and a finding whose citations all failed is exactly the case this
                // check exists to catch.
                rejections.Add(new GroundingRejection(
                    GroundingRejectionReason.FindingWithoutEvidence,
                    $"finding '{Truncate(finding.Hypothesis)}' lost all {finding.Evidence.Count} of its citations",
                    finding.Id));

                continue;
            }

            finding.Evidence = kept;
            survivors.Add(finding);
        }

        // "Exactly zero or one per investigation" is the domain rule for IsPrimary. If the
        // primary finding was the one that got dropped, promote the most confident survivor
        // rather than returning a set with no primary - the planning phase is built around
        // there being one, and silently having none reads downstream as "no findings".
        if (survivors.Count > 0 && !survivors.Any(f => f.IsPrimary))
        {
            var promoted = survivors.OrderByDescending(f => f.Confidence).First();
            promoted.IsPrimary = true;
        }

        return new GroundingResult(survivors, rejections);
    }

    /// <summary>
    /// Checks a plan's citations against the findings that survived
    /// <see cref="Verify"/>.
    /// </summary>
    /// <remarks>
    /// Rejection here is whole-plan, not per-action. An action justified by a finding that
    /// turned out to be invented is not an action to drop quietly from an otherwise fine
    /// plan - it is a signal that this investigation's reasoning is not trustworthy, and the
    /// right response is to hand the whole thing to a human with
    /// <see cref="EscalationReason.GroundingRejected"/>.
    /// </remarks>
    public static PlanGroundingResult VerifyPlan(
        ActionPlanDraft draft,
        IReadOnlyCollection<Finding> groundedFindings)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(groundedFindings);

        var rejections = new List<GroundingRejection>();

        if (draft.NoActionRequired || draft.Actions.Count == 0)
        {
            // Nothing to justify. "Do nothing" is a perfectly good outcome and needs no
            // evidence to support it - most incidents want a diagnosis, not a change.
            return new PlanGroundingResult(true, rejections);
        }

        var valid = groundedFindings.Select(f => f.Id).ToHashSet();

        foreach (var action in draft.Actions)
        {
            var cited = action.EvidenceFindingIds
                .Select(id => Guid.TryParse(id, out var g) ? g : (Guid?)null)
                .ToArray();

            if (cited.Length == 0)
            {
                rejections.Add(new GroundingRejection(
                    GroundingRejectionReason.ActionWithoutEvidence,
                    $"action {action.Type} cites no finding"));

                continue;
            }

            foreach (var id in cited)
            {
                if (id is null || !valid.Contains(id.Value))
                {
                    rejections.Add(new GroundingRejection(
                        GroundingRejectionReason.ActionCitesDroppedFinding,
                        $"action {action.Type} cites finding '{id?.ToString() ?? "(unparseable)"}', "
                        + "which is not among the grounded findings",
                        id));
                }
            }
        }

        return new PlanGroundingResult(rejections.Count == 0, rejections);
    }

    private static GroundingRejection? Check(
        Guid investigationId,
        Dictionary<Guid, InvestigationStep> byId,
        Finding finding,
        Evidence evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.Excerpt))
        {
            return new GroundingRejection(
                GroundingRejectionReason.EmptyExcerpt,
                "empty excerpt",
                finding.Id,
                evidence.StepId);
        }

        if (!byId.TryGetValue(evidence.StepId, out var step))
        {
            return new GroundingRejection(
                GroundingRejectionReason.UnknownStep,
                $"step {evidence.StepId} is not a step of this investigation",
                finding.Id,
                evidence.StepId);
        }

        // Belt and braces against a caller that hands us steps from more than one
        // investigation. The dictionary is normally built from one investigation's steps, but
        // "normally" is not a guarantee, and cross-investigation citation is precisely the
        // failure that would let stale evidence justify a fresh action.
        if (step.InvestigationId != investigationId)
        {
            return new GroundingRejection(
                GroundingRejectionReason.ForeignStep,
                $"step {step.Id} belongs to investigation {step.InvestigationId}, not {investigationId}",
                finding.Id,
                evidence.StepId);
        }

        if (string.IsNullOrEmpty(step.ResultDigest))
        {
            return new GroundingRejection(
                GroundingRejectionReason.NoDigest,
                $"step {step.Id} ({step.ToolName ?? "llm-turn"}) returned nothing to quote",
                finding.Id,
                evidence.StepId);
        }

        return Normalise(step.ResultDigest).Contains(Normalise(evidence.Excerpt), StringComparison.Ordinal)
            ? null
            : new GroundingRejection(
                GroundingRejectionReason.ExcerptNotFound,
                $"excerpt '{Truncate(evidence.Excerpt)}' does not appear in step {step.Id} "
                + $"({step.ToolName ?? "llm-turn"})",
                finding.Id,
                evidence.StepId);
    }

    /// <summary>
    /// Collapses runs of whitespace to one space and trims. Case and every other character
    /// are left exactly as they are.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than as a regex because it runs over every digest for every
    /// citation, and the digests are up to 8 KB each. This allocates one builder per call and
    /// makes a single pass.
    /// </remarks>
    public static string Normalise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string Truncate(string text) =>
        text.Length <= 120 ? text : string.Concat(text.AsSpan(0, 120), "…");
}
