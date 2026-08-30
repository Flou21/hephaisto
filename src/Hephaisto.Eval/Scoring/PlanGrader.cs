using Hephaisto.Core.Domain;
using Hephaisto.Core.Policy;

namespace Hephaisto.Eval.Scoring;

/// <summary>What the agent wanted to DO about the fault, as opposed to what it said about it.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<PlanVerdict>))]
public enum PlanVerdict
{
    /// <summary>Proposed nothing, and nothing was the right answer.</summary>
    CorrectlyDeclined = 0,

    /// <summary>Proposed an action this fault could reasonably be answered with.</summary>
    Reasonable = 1,

    /// <summary>Proposed something, and it was not a reasonable response to this fault.</summary>
    Unreasonable = 2,

    /// <summary>Proposed nothing where an action was available and appropriate.</summary>
    MissedAnAction = 3,

    /// <summary>No plan reached the scorer at all - the investigation ended before planning.</summary>
    NoPlan = 4,
}

/// <summary>
/// Grades the plan a cassette replay produced, deterministically.
/// </summary>
/// <remarks>
/// <para>
/// This can be exact where root-cause grading cannot, and that is the whole reason it is worth
/// having. <see cref="PolicyEngine.Evaluate"/> is a pure function over facts, so what the policy
/// engine would do with a proposal is a fact rather than a judgement - no second model, no
/// disagreement to reconcile, no cost.
/// </para>
/// <para>
/// It also catches the failure mode that root-cause scoring is blind to: an agent that diagnoses
/// a missing Secret perfectly and then proposes restarting the pod. The diagnosis scores
/// Correct, the eval looks healthy, and the proposal would have destroyed the evidence of the
/// thing it just got right. The e2e cannot catch it either while the harness runs in Observe.
/// </para>
/// <para>
/// <b>What it cannot do is grade execution.</b> A cassette records read-only tool output and
/// nothing else - there is no recorded mutation and no cluster to observe an effect on - so
/// "was it executed, did it work, did the incident resolve" belongs to the e2e harness against
/// a real kind cluster. Eval grades what it proposed; e2e grades that it worked.
/// </para>
/// </remarks>
public static class PlanGrader
{
    public const string Phase = "plan";

    public static (PlanVerdict Verdict, IReadOnlyList<EvalRecord> Assertions) Grade(
        Investigation investigation, AnswerKey key)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        ArgumentNullException.ThrowIfNull(key);

        var records = new List<EvalRecord>();
        var plan = investigation.Plan;

        if (plan is null)
        {
            // Not a failure. An investigation can end before planning for a dozen legitimate
            // reasons, and the root-cause verdict already accounts for those.
            records.Add(EvalRecord.Skip(Phase, $"{key.Fixture}: plan", "the investigation produced no plan"));

            return (PlanVerdict.NoPlan, records);
        }

        var proposed = plan.Actions.Select(a => a.Type).Distinct().ToList();

        // The one that gates. A forbidden action is not "the agent scored badly", it is the
        // agent proposing to do something actively harmful - erasing the evidence of a fault a
        // restart cannot fix - and it should be as loud as a broken invariant.
        var forbidden = proposed.Intersect(key.MustNotPropose).ToList();

        records.Add(forbidden.Count == 0
            ? EvalRecord.Pass(Phase, $"{key.Fixture}: proposed nothing harmful")
            : EvalRecord.Fail(
                Phase,
                $"{key.Fixture}: proposed nothing harmful",
                $"proposed {string.Join(", ", forbidden)}, which cannot fix this fault and destroys "
                + "the evidence of it"));

        if (plan.NoActionRequired || plan.Actions.Count == 0)
        {
            var right = key.AcceptableActions.Count == 0;

            records.Add(right
                ? EvalRecord.Pass(Phase, $"{key.Fixture}: correctly proposed no action")
                : EvalRecord.Skip(
                    Phase,
                    $"{key.Fixture}: correctly proposed no action",
                    $"an action was available ({string.Join(", ", key.AcceptableActions)}) and none was proposed"));

            // MissedAnAction is a Skip rather than a Fail deliberately. Declining to act is the
            // documented default and is never dangerous; scoring it as a failure would push the
            // agent's measured quality toward acting more, which is the wrong direction for the
            // one number nobody should be optimising.
            return (right ? PlanVerdict.CorrectlyDeclined : PlanVerdict.MissedAnAction, records);
        }

        if (forbidden.Count > 0)
        {
            return (PlanVerdict.Unreasonable, records);
        }

        if (key.AcceptableActions.Count == 0)
        {
            records.Add(EvalRecord.Skip(
                Phase,
                $"{key.Fixture}: proposed an action",
                $"no action was expected for this fixture; proposed {string.Join(", ", proposed)}"));

            return (PlanVerdict.Unreasonable, records);
        }

        var reasonable = proposed.All(t => key.AcceptableActions.Contains(t));

        records.Add(reasonable
            ? EvalRecord.Pass(Phase, $"{key.Fixture}: proposed a reasonable action", string.Join(", ", proposed))
            : EvalRecord.Skip(
                Phase,
                $"{key.Fixture}: proposed a reasonable action",
                $"proposed {string.Join(", ", proposed)}, expected one of "
                + string.Join(", ", key.AcceptableActions)));

        return (reasonable ? PlanVerdict.Reasonable : PlanVerdict.Unreasonable, records);
    }
}
