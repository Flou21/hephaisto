using System.Text.Json.Serialization;

namespace Hephaisto.Eval.Scoring;

/// <summary>
/// How one scenario's diagnosis came out.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="NoFinding"/> is why this is an enum and not a bool</b>, and it is the most
/// important decision in the scoring design.
/// </para>
/// <para>
/// The two instruments that exist today both treat "the agent produced no primary finding" as a
/// <i>skip</i>: <c>judge.sh</c> prints <c>skip grade &lt;f&gt;</c> and moves on, and the e2e c4/c7
/// discrimination check skips when either side has no finding. And no-finding is the dominant
/// failure mode - 9 of 14 concluded investigations in the dev cluster produced none.
/// </para>
/// <para>
/// So a score of <c>correct / graded</c> has a hole in it: a change that made the agent produce
/// <i>fewer</i> findings would shrink the denominator and push the reported number <b>up</b>.
/// Scoring against the number of scenarios, with no-finding as a first-class outcome, is what
/// closes it.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<RootCauseVerdict>))]
public enum RootCauseVerdict
{
    /// <summary>The agent named the right cause.</summary>
    Correct = 0,

    /// <summary>The agent named a cause, and it was wrong.</summary>
    Incorrect = 1,

    /// <summary>
    /// No primary finding survived. Counted against the total, never skipped.
    /// </summary>
    NoFinding = 2,
}

/// <summary>Status of one deterministic assertion.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvalStatus>))]
public enum EvalStatus
{
    Pass = 0,
    Fail = 1,
    Skip = 2,
}

/// <summary>
/// One assertion, in the shape <c>scripts/e2e/lib/common.sh</c> already writes and
/// <c>report.sh</c> already renders: <c>{at, phase, status, name, detail}</c>.
/// </summary>
/// <remarks>
/// Deliberately the same record rather than a better one. The e2e harness's renderer is good,
/// already handles the three outcomes and the "phases that recorded nothing" case, and a second
/// incompatible results format would mean two report formats to read and keep honest.
/// </remarks>
public sealed record EvalRecord
{
    public required DateTimeOffset At { get; init; }

    public required string Phase { get; init; }

    public required EvalStatus Status { get; init; }

    public required string Name { get; init; }

    public string Detail { get; init; } = string.Empty;

    public static EvalRecord Pass(string phase, string name, string detail = "") =>
        new() { At = DateTimeOffset.UtcNow, Phase = phase, Status = EvalStatus.Pass, Name = name, Detail = detail };

    public static EvalRecord Fail(string phase, string name, string detail = "") =>
        new() { At = DateTimeOffset.UtcNow, Phase = phase, Status = EvalStatus.Fail, Name = name, Detail = detail };

    public static EvalRecord Skip(string phase, string name, string detail = "") =>
        new() { At = DateTimeOffset.UtcNow, Phase = phase, Status = EvalStatus.Skip, Name = name, Detail = detail };
}

/// <summary>Everything one scenario produced, for the run report.</summary>
public sealed record ScenarioScore
{
    public required string Fixture { get; init; }

    public required RootCauseVerdict Verdict { get; init; }

    /// <summary>
    /// What the agent proposed to DO, graded deterministically against the answer key.
    /// </summary>
    /// <remarks>
    /// Reported beside the root-cause verdict rather than folded into it, because they fail
    /// independently and the interesting case is the one where they disagree: a perfect
    /// diagnosis followed by a proposal that would have destroyed the evidence for it.
    /// </remarks>
    public PlanVerdict PlanVerdict { get; init; } = PlanVerdict.NoPlan;

    /// <summary>The judge's one-sentence reason, when a judge ran.</summary>
    public string? JudgeReason { get; init; }

    /// <summary>The primary hypothesis, verbatim, so a run can be read without the database.</summary>
    public string? Hypothesis { get; init; }

    public required IReadOnlyList<EvalRecord> Assertions { get; init; }

    public int StepsUsed { get; init; }

    public decimal CostUsd { get; init; }

    public string? TerminationReason { get; init; }

    public ReplaySummary? Replay { get; init; }

    /// <summary>True when every deterministic assertion passed. These gate; the judge does not.</summary>
    public bool StructurallySound => Assertions.All(a => a.Status is not EvalStatus.Fail);
}

/// <summary>The headline numbers for one run of the corpus.</summary>
public sealed record RunScore
{
    public required string Label { get; init; }

    public required IReadOnlyList<ScenarioScore> Scenarios { get; init; }

    public int Total => Scenarios.Count;

    public int Correct => Scenarios.Count(s => s.Verdict is RootCauseVerdict.Correct);

    public int Incorrect => Scenarios.Count(s => s.Verdict is RootCauseVerdict.Incorrect);

    public int NoFinding => Scenarios.Count(s => s.Verdict is RootCauseVerdict.NoFinding);

    public decimal CostUsd => Scenarios.Sum(s => s.CostUsd);

    public int StepsUsed => Scenarios.Sum(s => s.StepsUsed);

    /// <summary>
    /// The headline. Denominator is the number of scenarios, never the number that happened to be
    /// gradeable - see <see cref="RootCauseVerdict.NoFinding"/>.
    /// </summary>
    public override string ToString() =>
        $"{Correct}/{Total} correct ({Incorrect} wrong, {NoFinding} no finding), "
        + $"{StepsUsed} steps, ${CostUsd:F4}";
}
