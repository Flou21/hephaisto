using Hephaisto.Core.Domain;

namespace Hephaisto.Eval.Scoring;

/// <summary>What the deterministic pass concluded about one investigation.</summary>
public sealed record StructuralGrade
{
    public required IReadOnlyList<EvalRecord> Assertions { get; init; }

    /// <summary>
    /// A verdict reached without asking a model anything, from whether the diagnosis names the
    /// thing that was actually broken.
    /// </summary>
    public required RootCauseVerdict Verdict { get; init; }

    public string? Hypothesis { get; init; }
}

/// <summary>
/// Grades an investigation without asking a model anything.
/// </summary>
/// <remarks>
/// <para>
/// Two separate jobs, deliberately not merged - and this is a considered departure from lumping
/// everything under "assertions":
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Invariants</b> become <see cref="EvalStatus.Fail"/> records and gate the run. These are
/// things that must hold whatever the agent concluded: at most one primary finding, evidence
/// attached to it, citations that resolve, a category from the published list. A failure here
/// means something is broken - in the agent or in this harness - not that the agent was wrong.
/// </description></item>
/// <item><description>
/// <b>Answer quality</b> becomes a <see cref="RootCauseVerdict"/>. Being wrong about a root cause
/// is the measurement, not a harness failure, and marking it <c>fail</c> would make a bad score
/// indistinguishable from a broken instrument.
/// </description></item>
/// </list>
/// <para>
/// The verdict here is also a free second opinion on the LLM judge. Where the two disagree, one of
/// them is wrong and the run says so - which is worth more than either alone, and costs nothing.
/// </para>
/// </remarks>
public static class StructuralGrader
{
    public const string Phase = "structure";

    public static StructuralGrade Grade(Investigation investigation, AnswerKey key)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        ArgumentNullException.ThrowIfNull(key);

        var records = new List<EvalRecord>();
        var primaries = investigation.Findings.Where(f => f.IsPrimary).ToList();

        // More than one primary is impossible through ConcludeMapper, which keeps the most
        // confident. If it ever happens, the mapper was bypassed.
        records.Add(primaries.Count <= 1
            ? EvalRecord.Pass(Phase, $"{key.Fixture}: at most one primary finding")
            : EvalRecord.Fail(Phase, $"{key.Fixture}: at most one primary finding",
                $"{primaries.Count} findings claim to be primary"));

        var primary = primaries.FirstOrDefault();

        if (primary is null)
        {
            // Not a failed assertion. An agent that could not reach a conclusion is the thing
            // being measured, and it is counted against the total rather than skipped.
            records.Add(EvalRecord.Skip(Phase, $"{key.Fixture}: diagnosis quality",
                "no primary finding survived grounding"));

            return new StructuralGrade
            {
                Assertions = records,
                Verdict = RootCauseVerdict.NoFinding,
            };
        }

        // A primary finding with no evidence is a guess with a confidence score attached.
        records.Add(primary.Evidence.Count > 0
            ? EvalRecord.Pass(Phase, $"{key.Fixture}: primary finding cites evidence")
            : EvalRecord.Fail(Phase, $"{key.Fixture}: primary finding cites evidence",
                "the finding survived with zero citations"));

        var stepIds = investigation.Steps.Select(s => s.Id).ToHashSet();
        var dangling = primary.Evidence.Where(e => !stepIds.Contains(e.StepId)).ToList();

        records.Add(dangling.Count == 0
            ? EvalRecord.Pass(Phase, $"{key.Fixture}: every citation resolves to a step")
            : EvalRecord.Fail(Phase, $"{key.Fixture}: every citation resolves to a step",
                $"{dangling.Count} citation(s) name a step not in this investigation"));

        // Finding.Category is a free string in the agent - the eight values live only in prose,
        // in the tool description and in 20-output-contract.md. Nothing validates it there.
        records.Add(AnswerKey.Categories.Contains(primary.Category)
            ? EvalRecord.Pass(Phase, $"{key.Fixture}: category is one of the published eight")
            : EvalRecord.Fail(Phase, $"{key.Fixture}: category is one of the published eight",
                $"'{primary.Category}' is not in the output contract"));

        var verdict = Judge(primary, key, records);

        return new StructuralGrade
        {
            Assertions = records,
            Verdict = verdict,
            Hypothesis = primary.Hypothesis,
        };
    }

    /// <summary>
    /// Does the diagnosis name the thing that was actually broken?
    /// </summary>
    /// <remarks>
    /// Checked over the hypothesis <i>and</i> its evidence excerpts, because naming the missing
    /// Secret in a quoted event is naming it. Case-insensitive, and substring rather than word
    /// matching, so <c>busybox:this-tag-does-not-exist</c> matches inside a longer image reference.
    /// <para>
    /// This is much stronger than the check it replaces. The e2e harness compares c4's and c7's
    /// hypotheses for exact string equality, which "the container cannot start" versus "the
    /// container failed to start" passes while telling you nothing.
    /// </para>
    /// </remarks>
    private static RootCauseVerdict Judge(Finding primary, AnswerKey key, List<EvalRecord> records)
    {
        if (key.MustMentionAnyOf.Count == 0)
        {
            records.Add(EvalRecord.Skip(Phase, $"{key.Fixture}: names the broken thing",
                "no required mentions defined for this fixture"));

            return RootCauseVerdict.Incorrect;
        }

        var haystack = string.Join(
            '\n',
            [primary.Hypothesis, .. primary.Evidence.Select(e => e.Excerpt)]);

        var hit = key.MustMentionAnyOf.FirstOrDefault(
            m => haystack.Contains(m, StringComparison.OrdinalIgnoreCase));

        if (hit is not null)
        {
            records.Add(EvalRecord.Pass(Phase, $"{key.Fixture}: names the broken thing", $"matched '{hit}'"));
            return RootCauseVerdict.Correct;
        }

        records.Add(EvalRecord.Skip(Phase, $"{key.Fixture}: names the broken thing",
            $"none of: {string.Join(", ", key.MustMentionAnyOf)}"));

        return RootCauseVerdict.Incorrect;
    }
}
