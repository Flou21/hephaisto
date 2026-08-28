using System.ComponentModel;
using System.Text.Json.Serialization;
using Watchtower.Core.Domain;

namespace Watchtower.Agent.Investigations;

/// <summary>
/// The argument shape of the virtual <c>conclude</c> tool.
/// </summary>
/// <remarks>
/// <para>
/// <c>conclude</c> is a tool and not "stop producing tool calls" for two reasons. It makes
/// termination an explicit, recorded act rather than an inference from silence — a model that
/// simply stops talking is indistinguishable from one that got confused, and those want
/// different <see cref="TerminationReason"/>s. And it carries the findings as a typed
/// structure, so the citations arrive as fields rather than as prose a parser has to guess at.
/// </para>
/// <para>
/// It is "virtual" in the sense that calling it reaches nothing: it writes into the runner's
/// own state and returns an acknowledgement. Like every other tool in phase 1 it cannot
/// change anything.
/// </para>
/// </remarks>
public sealed class ConcludeRequest
{
    [JsonPropertyName("summary")]
    [Description("A short paragraph an on-call engineer can read in ten seconds and act on.")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    [Description("Your confidence in the primary finding, 0.0 to 1.0. Be calibrated; this is scored against human feedback.")]
    public double Confidence { get; set; }

    [JsonPropertyName("findings")]
    [Description("One or more hypotheses. Exactly one must have primary set to true.")]
    public List<FindingDraft> Findings { get; set; } = [];
}

public sealed class FindingDraft
{
    [JsonPropertyName("category")]
    [Description("One of: resource-limit, dependency, config, image, scheduling, application, infrastructure, unknown.")]
    public string Category { get; set; } = "unknown";

    [JsonPropertyName("hypothesis")]
    [Description("What is wrong, in one or two plain sentences. Name the object and the mechanism, not the symptom.")]
    public string Hypothesis { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    [Description("0.0 to 1.0. If you are guessing between two causes, neither gets above 0.6.")]
    public double Confidence { get; set; }

    [JsonPropertyName("primary")]
    [Description("True for the one finding the plan should be built on. Exactly one.")]
    public bool Primary { get; set; }

    [JsonPropertyName("evidence")]
    [Description("Citations supporting this finding. A finding whose citations all fail verification is discarded.")]
    public List<EvidenceDraft> Evidence { get; set; } = [];
}

public sealed class EvidenceDraft
{
    [JsonPropertyName("step_id")]
    [Description("The step id shown in the header of the tool result you are quoting, e.g. '[step 0198...]'.")]
    public string StepId { get; set; } = string.Empty;

    [JsonPropertyName("excerpt")]
    [Description(
        "Text copied verbatim from that step's result. Not paraphrased, not tidied. "
        + "Checked as a substring against what the step actually returned.")]
    public string Excerpt { get; set; } = string.Empty;
}

/// <summary>
/// Turns the model's untrusted draft into domain objects. Does <b>not</b> verify grounding -
/// that is <see cref="GroundingVerifier"/>'s job, and it runs on the output of this.
/// </summary>
internal static class ConcludeMapper
{
    /// <summary>
    /// Resolves a cited step id against this investigation's steps.
    /// </summary>
    /// <remarks>
    /// Accepts a bare guid, a guid wrapped in the <c>[step …]</c> header the model was shown,
    /// or a 1-based ordinal. The last two are leniency about <i>which identifier was named</i>,
    /// which is a formatting question. It is not leniency about <i>what was quoted</i>: an
    /// unresolvable id still fails, and a resolved id whose digest does not contain the
    /// excerpt still fails. Being strict here would only convert honest citations into
    /// grounding rejections and hide the drift the metric exists to show.
    /// </remarks>
    public static Guid ResolveStepId(string raw, IReadOnlyList<InvestigationStep> steps)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Guid.Empty;
        }

        var text = raw.Trim().Trim('[', ']').Trim();

        if (text.StartsWith("step", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..].Trim();
        }

        if (Guid.TryParse(text, out var id))
        {
            return id;
        }

        text = text.TrimStart('s', 'S', '#');

        if (int.TryParse(text, out var ordinal))
        {
            var match = steps.FirstOrDefault(s => s.Ordinal == ordinal);

            if (match is not null)
            {
                return match.Id;
            }
        }

        // Guid.Empty names no step, so the verifier rejects it as UnknownStep with the
        // citation intact in the audit trail. Returning null here instead would drop the
        // citation before it was counted, and the metric would under-report.
        return Guid.Empty;
    }

    public static List<Finding> ToFindings(
        ConcludeRequest request,
        Guid investigationId,
        IReadOnlyList<InvestigationStep> steps)
    {
        var findings = new List<Finding>();

        foreach (var draft in request.Findings)
        {
            var finding = new Finding
            {
                InvestigationId = investigationId,
                Category = string.IsNullOrWhiteSpace(draft.Category) ? "unknown" : draft.Category,
                Hypothesis = draft.Hypothesis,
                Confidence = Math.Clamp(draft.Confidence, 0, 1),
                IsPrimary = draft.Primary,
            };

            foreach (var evidence in draft.Evidence)
            {
                var stepId = ResolveStepId(evidence.StepId, steps);

                finding.Evidence.Add(new Evidence
                {
                    FindingId = finding.Id,
                    StepId = stepId,
                    Excerpt = evidence.Excerpt,

                    // Clickable provenance for the UI. Points at the step, which owns the raw
                    // blob, so a human can read the untruncated original the digest came from.
                    SourceUri = stepId == Guid.Empty ? null : $"evidence://step/{stepId}",
                });
            }

            findings.Add(finding);
        }

        // "Exactly zero or one per investigation" is the domain rule. A model that marks two
        // primaries has not given us a second opinion, it has failed to choose; taking the
        // most confident is a deterministic tie-break rather than an extra round trip.
        var primaries = findings.Where(f => f.IsPrimary).ToArray();

        if (primaries.Length > 1)
        {
            foreach (var finding in primaries.OrderByDescending(f => f.Confidence).Skip(1))
            {
                finding.IsPrimary = false;
            }
        }
        else if (primaries.Length == 0 && findings.Count > 0)
        {
            findings.OrderByDescending(f => f.Confidence).First().IsPrimary = true;
        }

        return findings;
    }
}
