using Hephaisto.Agent.Llm;
using Hephaisto.Core.Domain;
using Hephaisto.Tests.TestData;

namespace Hephaisto.Tests.Investigations;

/// <summary>
/// The step a run keeps back so it can answer.
/// </summary>
/// <remarks>
/// Once provider overloads stopped destroying runs outright, every investigation that
/// survived ended StepBudgetExhausted at exactly 12 of 12 steps and not one produced a
/// finding. The agent was not failing to reach an answer - it was reaching the end of its
/// budget with the answer unspoken, because a conclusion costs a step and every step was
/// spent asking. The `conclude` tool was already exempt from the tool budget for exactly this
/// reason; these pin the same reserve for steps.
/// </remarks>
public class ConcludingStepTests
{
    private static InvestigationBudget Budget(InvestigationBudgetOptions? options = null) =>
        new(options ?? new InvestigationBudgetOptions { MaxSteps = 2 }, Given.Clock());

    [Fact]
    public void A_run_out_of_steps_normally_cannot_start_another()
    {
        var budget = Budget();
        budget.RecordStep(10, 10, 0.01m);
        budget.RecordStep(10, 10, 0.01m);

        var act = budget.EnsureCanStartStep;

        act.Should().Throw<BudgetExhaustedException>()
            .Which.Reason.Should().Be(TerminationReason.StepBudgetExhausted);
    }

    [Fact]
    public void The_granted_step_lets_exactly_one_more_call_through()
    {
        var budget = Budget();
        budget.RecordStep(10, 10, 0.01m);
        budget.RecordStep(10, 10, 0.01m);

        budget.TryGrantConcludingStep().Should().BeTrue();

        // The one reserved call.
        budget.EnsureCanStartStep();
    }

    [Fact]
    public void The_granted_step_is_not_a_second_budget()
    {
        var budget = Budget();
        budget.RecordStep(10, 10, 0.01m);
        budget.RecordStep(10, 10, 0.01m);

        budget.TryGrantConcludingStep();

        // TWO round trips, not one, and the number is the tool-calling protocol rather than a
        // preference: the conclusion is taken through the `conclude` tool, so the model emits
        // a call on the first trip and answers after the framework runs it on the second.
        // Reserving one paid for the emit and refused the answer, so the tool never returned a
        // value and the run reported no finding - backlog #59's "every one produced no
        // finding", which was this.
        budget.EnsureCanStartStep();
        budget.RecordStep(10, 10, 0.01m);
        budget.EnsureCanStartStep();
        budget.RecordStep(10, 10, 0.01m);

        // And then it stops. The point of the reservation is that it is finite: "a final turn"
        // must not become "unlimited turns" the moment a model declines to conclude.
        var act = budget.EnsureCanStartStep;

        act.Should().Throw<BudgetExhaustedException>();
    }

    [Fact]
    public void It_is_granted_only_once()
    {
        var budget = Budget();
        budget.RecordStep(10, 10, 0.01m);
        budget.RecordStep(10, 10, 0.01m);

        budget.TryGrantConcludingStep().Should().BeTrue();
        budget.TryGrantConcludingStep().Should().BeFalse();
    }

    [Fact]
    public void It_is_refused_when_the_wall_clock_is_gone()
    {
        // A run past its deadline has nowhere to put the call, and the deadline is the
        // backstop that stops a wedged provider holding a worker slot.
        var clock = Given.Clock();
        var budget = new InvestigationBudget(
            new InvestigationBudgetOptions { MaxSteps = 1, MaxWallClock = TimeSpan.FromMinutes(5) },
            clock);

        budget.RecordStep(10, 10, 0.01m);
        clock.UtcNow += TimeSpan.FromMinutes(6);

        budget.TryGrantConcludingStep().Should().BeFalse();
    }

    [Fact]
    public void A_run_that_never_exhausted_its_budget_is_unaffected()
    {
        var budget = Budget(new InvestigationBudgetOptions { MaxSteps = 5 });
        budget.RecordStep(10, 10, 0.01m);

        budget.EnsureCanStartStep();
        budget.IsExhausted.Should().BeFalse();
    }
}
