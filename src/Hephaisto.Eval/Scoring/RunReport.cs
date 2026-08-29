namespace Hephaisto.Eval.Scoring;

/// <summary>
/// One labelled experiment arm: every pass over the corpus, and the tally across them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Repeats are the reason this type exists.</b> There is a language model in the loop, so one
/// pass over eight scenarios is one sample of a distribution, and 7/8 against 6/8 on single passes
/// is indistinguishable from noise. An arm is three passes, and the comparison that means
/// something is between two arms' totals - which is why the headline here is over
/// <see cref="Total"/> attempts rather than over the eight fixtures.
/// </para>
/// <para>
/// The overrides are recorded alongside the numbers. An arm whose settings are not written down
/// next to its score is a number nobody can reproduce a week later, and the whole reason the
/// harness exists is that the previous numbers could not be.
/// </para>
/// </remarks>
public sealed record RunReport
{
    public required string Label { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>The model that investigated, so two arms cannot be compared across a model change.</summary>
    public string? ModelId { get; init; }

    /// <summary>The <c>--set</c> overrides this arm ran with, verbatim.</summary>
    public IReadOnlyList<string> Overrides { get; init; } = [];

    /// <summary>One entry per pass over the corpus.</summary>
    public required IReadOnlyList<RunScore> Passes { get; init; }

    private IEnumerable<ScenarioScore> AllScenarios => Passes.SelectMany(p => p.Scenarios);

    /// <summary>Total attempts: scenarios times passes.</summary>
    public int Total => AllScenarios.Count();

    public int Correct => AllScenarios.Count(s => s.Verdict is RootCauseVerdict.Correct);

    public int Incorrect => AllScenarios.Count(s => s.Verdict is RootCauseVerdict.Incorrect);

    public int NoFinding => AllScenarios.Count(s => s.Verdict is RootCauseVerdict.NoFinding);

    /// <summary>
    /// Attempts whose deterministic assertions all held.
    /// </summary>
    /// <remarks>
    /// Separate from correctness on purpose. An unsound attempt - a dangling citation, a miss rate
    /// over the threshold - says the instrument slipped, and reading its verdict as a measurement
    /// of the change under test is how a broken harness becomes a published number.
    /// </remarks>
    public int Sound => AllScenarios.Count(s => s.StructurallySound);

    public decimal CostUsd => AllScenarios.Sum(s => s.CostUsd);

    public int StepsUsed => AllScenarios.Sum(s => s.StepsUsed);

    /// <summary>Per-fixture tallies, so a change that helps one scenario and breaks another shows.</summary>
    public IReadOnlyList<FixtureTally> ByFixture =>
    [
        .. AllScenarios
            .GroupBy(s => s.Fixture, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new FixtureTally
            {
                Fixture = g.Key,
                Attempts = g.Count(),
                Correct = g.Count(s => s.Verdict is RootCauseVerdict.Correct),
                Incorrect = g.Count(s => s.Verdict is RootCauseVerdict.Incorrect),
                NoFinding = g.Count(s => s.Verdict is RootCauseVerdict.NoFinding),
                MeanSteps = g.Average(s => (double)s.StepsUsed),
                CostUsd = g.Sum(s => s.CostUsd),
            })
    ];

    public override string ToString() =>
        $"{Label}: {Correct}/{Total} correct ({Incorrect} wrong, {NoFinding} no finding) "
        + $"over {Passes.Count} pass(es), {StepsUsed} steps, ${CostUsd:F4}";
}

/// <summary>How one fixture fared across every pass in an arm.</summary>
public sealed record FixtureTally
{
    public required string Fixture { get; init; }

    public required int Attempts { get; init; }

    public required int Correct { get; init; }

    public required int Incorrect { get; init; }

    public required int NoFinding { get; init; }

    /// <summary>
    /// Mean steps spent, which is half of what an experiment is judging.
    /// </summary>
    /// <remarks>
    /// Raising the step budget will buy accuracy by spending more; the question worth asking is
    /// whether a cheaper change buys the same accuracy for fewer steps. A report with no cost axis
    /// cannot answer it.
    /// </remarks>
    public required double MeanSteps { get; init; }

    public required decimal CostUsd { get; init; }

    public override string ToString() =>
        $"{Fixture,-4} {Correct}/{Attempts} correct, {Incorrect} wrong, {NoFinding} none, "
        + $"{MeanSteps:F1} steps avg";
}
