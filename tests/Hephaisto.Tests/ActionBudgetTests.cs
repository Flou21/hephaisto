using Hephaisto.Core.Safety;
using Hephaisto.Tests.TestData;

namespace Hephaisto.Tests;

/// <summary>
/// One implementation, two callers: the policy engine and the UI. A second copy of this
/// arithmetic in Blazor would drift, and the drift shows up as a human being told the agent is
/// within budget while the engine has already downgraded the action.
/// </summary>
public sealed class ActionBudgetTests
{
    [Fact]
    public void FreshCounts_AreWithinBudget()
    {
        var status = ActionBudget.Evaluate(0, 0, 0, 0, Given.Options());

        status.IsExceeded.Should().BeFalse();
        status.Exceeded.Should().Be(BudgetWindow.None);
    }

    [Theory]
    [InlineData(2, 1, 5, 5)]
    [InlineData(0, 1, 9, 19)]
    public void JustBelowEveryCap_IsWithinBudget(int incident, int workload, int hour, int day)
    {
        ActionBudget.Evaluate(incident, workload, hour, day, Given.Options())
            .IsExceeded.Should().BeFalse();
    }

    [Fact]
    public void TheCapIsReachedAtTheLimit_NotPastIt()
    {
        // The counts are actions already taken, so the action being judged is the one that
        // would take the total past the cap.
        ActionBudget.Evaluate(3, 0, 0, 0, Given.Options()).Exceeded.Should().Be(BudgetWindow.Incident);
    }

    [Theory]
    [InlineData(3, 0, 0, 0, BudgetWindow.Incident)]
    [InlineData(0, 2, 0, 0, BudgetWindow.WorkloadHour)]
    [InlineData(0, 0, 10, 0, BudgetWindow.Hour)]
    [InlineData(0, 0, 0, 20, BudgetWindow.Day)]
    public void EachWindowIsReportedByName(int incident, int workload, int hour, int day, BudgetWindow expected)
    {
        ActionBudget.Evaluate(incident, workload, hour, day, Given.Options())
            .Exceeded.Should().Be(expected);
    }

    [Fact]
    public void NarrowestWindowWins()
    {
        // "You have already restarted this three times for this one incident" is a more useful
        // thing to tell a human than "the cluster-wide daily cap is full".
        var status = ActionBudget.Evaluate(99, 99, 99, 99, Given.Options());

        status.Exceeded.Should().Be(BudgetWindow.Incident);
    }

    [Fact]
    public void TheReasonCarriesUsedAndLimit()
    {
        var status = ActionBudget.Evaluate(0, 0, 12, 0, Given.Options());

        status.Used.Should().Be(12);
        status.Limit.Should().Be(10);
        status.Reason.Should().Contain("12/10");
    }

    [Fact]
    public void WithinBudget_StillReportsUtilisation()
    {
        // The UI renders this on the happy path too, so it cannot be empty when nothing is wrong.
        var status = ActionBudget.Evaluate(0, 0, 4, 4, Given.Options());

        status.Reason.Should().Contain("4/10");
    }

    [Fact]
    public void TheFactsOverloadAgreesWithTheCountsOverload()
    {
        var facts = Given.Facts() with
        {
            ActionsOnIncident = 1,
            RecentActionsOnWorkload = 2,
            ActionsClusterWideLastHour = 3,
            ActionsClusterWideLastDay = 4,
        };

        ActionBudget.Evaluate(facts, Given.Options())
            .Should().Be(ActionBudget.Evaluate(1, 2, 3, 4, Given.Options()));
    }
}
