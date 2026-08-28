using Microsoft.Extensions.AI;
using Watchtower.Agent.Llm;
using Watchtower.Core.Domain;

namespace Watchtower.Tests.Investigations;

/// <summary>
/// Budgets are the reason it is safe to leave an autonomous loop running. They are enforced
/// in code and not asked for in the prompt, so they are testable - which is the whole
/// argument for putting them here.
/// </summary>
public class InvestigationBudgetTests
{
    private static readonly LlmPricing FreePricing = new(new Dictionary<string, ModelPrice>());

    private static LlmPricing Pricing(decimal inputPerMillion, decimal outputPerMillion) =>
        new(new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["fake-model"] = new()
            {
                InputPerMillionUsd = inputPerMillion,
                OutputPerMillionUsd = outputPerMillion,
            },
        });

    [Fact]
    public void Step_budget_throws_with_the_matching_termination_reason()
    {
        var clock = new TestClock();
        var budget = new InvestigationBudget(new InvestigationBudgetOptions { MaxSteps = 2 }, clock);

        budget.EnsureCanStartStep();
        budget.RecordStep(10, 10, 0m);
        budget.EnsureCanStartStep();
        budget.RecordStep(10, 10, 0m);

        var act = budget.EnsureCanStartStep;

        act.Should().Throw<BudgetExhaustedException>()
            .Which.Reason.Should().Be(TerminationReason.StepBudgetExhausted);
    }

    [Fact]
    public void Wall_clock_budget_throws_with_the_matching_termination_reason()
    {
        var clock = new TestClock();

        var budget = new InvestigationBudget(
            new InvestigationBudgetOptions { MaxWallClock = TimeSpan.FromMinutes(4) },
            clock);

        budget.EnsureCanStartStep();
        clock.Advance(TimeSpan.FromMinutes(4));

        var act = budget.EnsureCanStartStep;

        act.Should().Throw<BudgetExhaustedException>()
            .Which.Reason.Should().Be(TerminationReason.WallClockExhausted);
    }

    [Fact]
    public void Token_budget_throws_with_the_matching_termination_reason()
    {
        var budget = new InvestigationBudget(
            new InvestigationBudgetOptions { MaxInputTokens = 1000 },
            new TestClock());

        budget.RecordStep(1000, 0, 0m);

        var act = budget.EnsureCanStartStep;

        act.Should().Throw<BudgetExhaustedException>()
            .Which.Reason.Should().Be(TerminationReason.TokenBudgetExhausted);
    }

    [Fact]
    public void Cost_budget_throws_with_the_matching_termination_reason()
    {
        var budget = new InvestigationBudget(
            new InvestigationBudgetOptions { MaxCostUsd = 0.50m },
            new TestClock());

        budget.RecordStep(0, 0, 0.50m);

        var act = budget.EnsureCanStartStep;

        act.Should().Throw<BudgetExhaustedException>()
            .Which.Reason.Should().Be(TerminationReason.CostBudgetExhausted);
    }

    [Fact]
    public void Tool_call_budget_refuses_rather_than_throwing()
    {
        // Throwing inside a tool invocation would be caught by FunctionInvokingChatClient and
        // handed back to the model as a broken tool, so it would not stop the loop. Refusing
        // in text and letting the next EnsureCanStartStep stop the run is both honest to the
        // model and actually effective.
        var budget = new InvestigationBudget(
            new InvestigationBudgetOptions { MaxToolCalls = 1 },
            new TestClock());

        budget.TryConsumeToolCall().Should().BeTrue();
        budget.TryConsumeToolCall().Should().BeFalse();

        budget.Breach.Should().Be(TerminationReason.ToolCallBudgetExhausted);

        var act = budget.EnsureCanStartStep;
        act.Should().Throw<BudgetExhaustedException>()
            .Which.Reason.Should().Be(TerminationReason.ToolCallBudgetExhausted);
    }

    [Fact]
    public void The_check_is_before_the_call_so_a_run_overshoots_by_at_most_one()
    {
        // Aborting after a call that has already been paid for burns the tokens and keeps
        // none of the answer, which makes an overspend worse rather than better. The cost of
        // that choice is bounded and known, and this is the test that says by how much.
        var budget = new InvestigationBudget(
            new InvestigationBudgetOptions { MaxCostUsd = 0.10m },
            new TestClock());

        budget.EnsureCanStartStep();
        budget.RecordStep(0, 0, 0.09m);

        budget.EnsureCanStartStep();
        budget.RecordStep(0, 0, 5.00m);

        budget.CostUsd.Should().Be(5.09m);

        var act = budget.EnsureCanStartStep;
        act.Should().Throw<BudgetExhaustedException>();
    }

    [Fact]
    public async Task Guard_client_counts_one_step_per_provider_round_trip()
    {
        var clock = new TestClock();
        var budget = new InvestigationBudget(new InvestigationBudgetOptions(), clock);
        var recorder = new Watchtower.Agent.Investigations.InvestigationRecorder(
            Guid.CreateVersion7(), clock, TimeSpan.FromDays(30));

        var fake = new FakeChatClient((_, _) => FakeChatClient.Text("done", inputTokens: 500, outputTokens: 100));

        using var guard = new BudgetGuardChatClient(fake, budget, Pricing(1m, 10m), "fake-model", recorder);

        await guard.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        await guard.GetResponseAsync([new ChatMessage(ChatRole.User, "again")]);

        budget.Steps.Should().Be(2);
        budget.InputTokens.Should().Be(1000);
        budget.OutputTokens.Should().Be(200);

        // 1000 input at $1/M plus 200 output at $10/M.
        budget.CostUsd.Should().Be(0.001m + 0.002m);

        recorder.Steps.Should().HaveCount(2);
        recorder.Steps.Should().AllSatisfy(s => s.Kind.Should().Be(StepKind.LlmTurn));
    }

    [Fact]
    public async Task Guard_client_records_a_failed_call_as_a_step()
    {
        // A failed call still cost wall clock and usually tokens the provider will bill for.
        // Not recording it would make a provider that fails slowly look free.
        var clock = new TestClock();
        var budget = new InvestigationBudget(new InvestigationBudgetOptions(), clock);
        var recorder = new Watchtower.Agent.Investigations.InvestigationRecorder(
            Guid.CreateVersion7(), clock, TimeSpan.FromDays(30));

        var fake = new FakeChatClient((_, _) => FakeChatClient.Text("never returned"))
        {
            Throws = new HttpRequestException("503 from the provider"),
        };

        using var guard = new BudgetGuardChatClient(fake, budget, FreePricing, "fake-model", recorder);

        var act = () => guard.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        await act.Should().ThrowAsync<HttpRequestException>();

        recorder.Steps.Should().ContainSingle().Which.Failed.Should().BeTrue();
        budget.Steps.Should().Be(1);
    }

    [Fact]
    public void Guard_client_refuses_to_stream()
    {
        var budget = new InvestigationBudget(new InvestigationBudgetOptions(), new TestClock());
        var fake = new FakeChatClient((_, _) => FakeChatClient.Text("x"));

        using var guard = new BudgetGuardChatClient(fake, budget, FreePricing, "fake-model");

        var act = () => guard.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        // Streaming usage accounting is not reliable enough to enforce a budget on, and an
        // unenforceable budget is not a budget.
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void An_unpriced_model_costs_zero_rather_than_throwing()
    {
        // Refusing to investigate because a price list is stale would turn a bookkeeping gap
        // into an outage in the thing that diagnoses outages.
        FreePricing.CostOf("some-model-nobody-priced", 1_000_000, 1_000_000).Should().Be(0m);
    }

    [Fact]
    public void Pricing_falls_back_to_the_longest_matching_prefix()
    {
        // Providers append dated suffixes to a model whose price is the base model's. A price
        // list that has to be updated on every such rename is a price list that is wrong.
        var pricing = new LlmPricing(new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini-2.5"] = new() { InputPerMillionUsd = 100m },
            ["gemini-2.5-flash"] = new() { InputPerMillionUsd = 1m },
        });

        pricing.CostOf("gemini-2.5-flash-preview-09-2025", 1_000_000, 0).Should().Be(1m);
        pricing.CostOf("gemini-2.5-pro", 1_000_000, 0).Should().Be(100m);
    }
}
