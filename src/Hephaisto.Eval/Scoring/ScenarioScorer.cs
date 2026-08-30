using Hephaisto.Core.Domain;

namespace Hephaisto.Eval.Scoring;

/// <summary>
/// Folds one investigation, the deterministic grade, the judge and the replay accounting into
/// a single <see cref="ScenarioScore"/>.
/// </summary>
/// <remarks>
/// Deliberately a pure function over already-collected results rather than something that runs
/// an investigation. Everything expensive - the model call, the cluster, the judge's HTTP request
/// - happens in the caller, so the part that decides what a run <i>means</i> is testable with no
/// network at all. That matters more here than usual: this is the code that turns a pile of
/// evidence into the one number the roadmap gates on.
/// </remarks>
public static class ScenarioScorer
{
    public const string JudgePhase = "judge";
    public const string ReplayPhase = "replay";

    /// <summary>
    /// The miss rate above which a run is reported as invalid rather than as a bad score.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A miss means the model asked something the recording has no answer to, and the harness
    /// replies "not recorded". A few are expected and healthy - a change that redirects the
    /// investigation is supposed to ask different questions. Many mean the model is mostly
    /// talking to the harness rather than to the recorded cluster, and whatever it concluded is
    /// about this fixture's gaps, not about the change under test.
    /// </para>
    /// <para>
    /// So this is a <b>validity check, not a verdict</b>: over the threshold the scenario is
    /// marked unsound and the answer is "re-record", never "the agent got worse".
    /// </para>
    /// </remarks>
    public const double MaxAcceptableMissRate = 0.25;

    public static ScenarioScore Combine(
        Cassette cassette,
        AnswerKey key,
        Investigation investigation,
        ReplaySummary? replay = null,
        JudgeVerdict? judged = null)
    {
        ArgumentNullException.ThrowIfNull(cassette);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(investigation);

        var grade = StructuralGrader.Grade(investigation, key);
        var records = new List<EvalRecord>(grade.Assertions);

        // What it wanted to DO, graded separately and deterministically. A correct diagnosis
        // followed by a harmful proposal scores Correct on the root cause alone, and that is
        // the gap this closes - the e2e cannot see it either while the harness runs in Observe.
        var (planVerdict, planRecords) = PlanGrader.Grade(investigation, key);
        records.AddRange(planRecords);

        if (replay is not null)
        {
            records.Add(replay.MissRate <= MaxAcceptableMissRate
                ? EvalRecord.Pass(ReplayPhase, $"{key.Fixture}: replay covered the investigation", replay.ToString())
                : EvalRecord.Fail(
                    ReplayPhase,
                    $"{key.Fixture}: replay covered the investigation",
                    $"{replay} - over {MaxAcceptableMissRate:P0}; re-record rather than reading the verdict"));
        }

        records.Add(Agreement(key, grade.Verdict, judged));

        return new ScenarioScore
        {
            Fixture = key.Fixture,
            Verdict = grade.Verdict,
            PlanVerdict = planVerdict,
            Hypothesis = grade.Hypothesis,
            JudgeReason = judged?.Reason,
            Assertions = records,
            StepsUsed = investigation.StepsUsed,
            CostUsd = investigation.CostUsd,
            TerminationReason = investigation.TerminationReason.ToString(),
            Replay = replay,
        };
    }

    /// <summary>
    /// Records whether the two graders reached the same answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The deterministic verdict is the one that counts; the judge is a second opinion that is
    /// reported and never gates. So a disagreement is recorded as a <see cref="EvalStatus.Skip"/>
    /// and not a <see cref="EvalStatus.Fail"/> - failing here would mean a release could be
    /// blocked by another language model having an opinion, which is precisely what
    /// <c>judge.sh</c> refuses to allow and what this port must keep refusing.
    /// </para>
    /// <para>
    /// Skip is also the honest status for it. In the shared report format a skip reads as
    /// "not established", and two graders contradicting each other establishes nothing except
    /// that one of them is wrong and a human should look.
    /// </para>
    /// </remarks>
    private static EvalRecord Agreement(AnswerKey key, RootCauseVerdict deterministic, JudgeVerdict? judged)
    {
        var name = $"{key.Fixture}: judge agrees with the deterministic verdict";

        if (judged is null)
        {
            return EvalRecord.Skip(JudgePhase, name, "no judge ran");
        }

        // No-finding is not something the judge is asked about - there is no diagnosis to grade -
        // so it cannot agree or disagree, and calling that a disagreement would manufacture one.
        if (deterministic is RootCauseVerdict.NoFinding)
        {
            return EvalRecord.Skip(JudgePhase, name, "no diagnosis to judge");
        }

        var judgeSaysCorrect = judged.Correct;
        var deterministicSaysCorrect = deterministic is RootCauseVerdict.Correct;

        return judgeSaysCorrect == deterministicSaysCorrect
            ? EvalRecord.Pass(JudgePhase, name, judged.Reason)
            : EvalRecord.Skip(
                JudgePhase,
                name,
                $"judge says {(judgeSaysCorrect ? "correct" : "incorrect")}, "
                + $"deterministic says {deterministic.ToString().ToLowerInvariant()}: {judged.Reason}");
    }
}
