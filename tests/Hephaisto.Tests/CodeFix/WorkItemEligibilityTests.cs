using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.CodeFix;

/// <summary>
/// The gate for a work item - a GitHub issue assigned to Hephaisto - held to the standard of
/// <see cref="CodeFixEligibilityTests"/>: a permissive baseline, every reason reachable by
/// exactly one change to it, and nothing about an incident asked at all.
/// </summary>
public sealed class WorkItemEligibilityTests
{
    private static WorkItemCandidate Candidate() => new()
    {
        Repository = "Flou21/hephaisto-fixture-dotnet",
        Taken = true,
        RepositoryListed = true,
        Binding = new RepositoryBinding { Url = "https://github.com/Flou21/hephaisto-fixture-dotnet", DefaultBranch = "main" },
    };

    private static CodeFixFacts Facts() => new()
    {
        Mode = CodeFixMode.Plan,
        AgentMode = AgentMode.Observe,
        EmergencyStop = false,
        RunawayLatched = false,
    };

    /// <summary>
    /// No eligible category and a confidence floor nothing reaches: the two knobs that are about a
    /// model's finding. A work item has no finding, so they must not be able to refuse one.
    /// </summary>
    private static CodeFixEligibilityOptions Options() => new()
    {
        EligibleCategories = [],
        ConfidenceFloor = 2,
        AllowedRepositoryHosts = ["github.com"],
        MaxAttemptsPerRepositoryPerDay = 3,
        MaxConcurrentJobs = 1,
        MaxCostUsdPerDay = 50,
    };

    [Fact]
    public void Baseline_IsEligible_InObserveMode_WithNoCategoryAndNoFinding()
    {
        var verdict = CodeFixEligibility.EvaluateWorkItem(Candidate(), Facts(), Options());

        verdict.Eligible.Should().BeTrue(verdict.Describe());
        verdict.Codes.Should().BeEmpty();
    }

    public static TheoryData<string, CodeFixReasonCode> OneChange => new()
    {
        { "mode off", CodeFixReasonCode.ModeOff },
        { "agent off", CodeFixReasonCode.AgentOff },
        { "stop", CodeFixReasonCode.EmergencyStop },
        { "latch", CodeFixReasonCode.RunawayLatched },
        { "not taken", CodeFixReasonCode.WorkItemNotTaken },
        { "not listed", CodeFixReasonCode.RepositoryNotListed },
        { "no url", CodeFixReasonCode.NoRepositoryMapping },
        { "foreign host", CodeFixReasonCode.RepositoryHostNotAllowed },
        { "attempt open", CodeFixReasonCode.AttemptAlreadyOpen },
        { "repo cap", CodeFixReasonCode.RepositoryDailyCapReached },
        { "concurrency", CodeFixReasonCode.ConcurrencyCapReached },
        { "cost", CodeFixReasonCode.DailyCostCapReached },
        { "llm budget", CodeFixReasonCode.LlmBudgetExhausted },
    };

    [Theory]
    [MemberData(nameof(OneChange))]
    public void EveryGate_IsReachable_ByExactlyOneChange(string change, CodeFixReasonCode expected)
    {
        var (candidate, facts) = Apply(change);

        var verdict = CodeFixEligibility.EvaluateWorkItem(candidate, facts, Options());

        verdict.Eligible.Should().BeFalse();
        verdict.Codes.Should().Equal([expected], "the baseline is permissive, so only '{0}' can refuse", change);
        verdict.Reasons.Should().ContainSingle();
    }

    [Fact]
    public void TheGatesAboutAnIncident_AreNeverAsked()
    {
        CodeFixReasonCode[] incidentOnly =
        [
            CodeFixReasonCode.SelfSignal,
            CodeFixReasonCode.IncidentNotEscalated,
            CodeFixReasonCode.EscalationReasonNotEligible,
            CodeFixReasonCode.InvestigationNotConcluded,
            CodeFixReasonCode.NoPrimaryFinding,
            CodeFixReasonCode.CategoryNotEligible,
            CodeFixReasonCode.InfraOnlyKind,
            CodeFixReasonCode.ConfidenceBelowFloor,
            CodeFixReasonCode.Ungrounded,
            CodeFixReasonCode.WorkloadAttemptOpen,
        ];

        // Everything that can be wrong at once, and - WorkloadAttemptOpen - a fact that is an
        // incident's and that a caller could still set.
        var everything = OneChange.Select(row => row.Data.Item1)
            .Aggregate((Candidate(), Facts()), (world, change) => Apply(change, world.Item1, world.Item2));

        var verdict = CodeFixEligibility.EvaluateWorkItem(
            everything.Item1, everything.Item2 with { WorkloadAttemptOpen = true }, new CodeFixEligibilityOptions());

        verdict.Codes.Should().NotIntersectWith(incidentOnly);
        OneChange.Select(row => row.Data.Item2).Should().NotIntersectWith(incidentOnly);

        // And between the two predicates no code is left that nothing can produce.
        OneChange.Select(row => row.Data.Item2).Concat(incidentOnly)
            .Should().BeEquivalentTo(Enum.GetValues<CodeFixReasonCode>());
    }

    [Fact]
    public void ModeOff_DoesNotShortCircuit_AndAloneMeansWouldHavePlanned()
    {
        var alone = CodeFixEligibility.EvaluateWorkItem(Candidate(), Facts() with { Mode = CodeFixMode.Off }, Options());

        alone.WouldHaveStarted.Should().BeTrue();

        var both = CodeFixEligibility.EvaluateWorkItem(
            Candidate() with { RepositoryListed = false }, Facts() with { Mode = CodeFixMode.Off }, Options());

        both.Codes.Should().Equal(CodeFixReasonCode.ModeOff, CodeFixReasonCode.RepositoryNotListed);
        both.WouldHaveStarted.Should().BeFalse("the mode is not the only thing in the way");
    }

    [Theory]
    [InlineData(CodeFixMode.Plan)]
    [InlineData(CodeFixMode.Pr)]
    public void PlanAndPr_BothPlan(CodeFixMode mode) =>
        CodeFixEligibility.EvaluateWorkItem(Candidate(), Facts() with { Mode = mode }, Options()).Eligible.Should().BeTrue();

    [Fact]
    public void ARepositoryThatIsNotListed_IsRefusedForThat_WhateverItsUrl()
    {
        // Listed is the authorization; a clone URL on an allowed host does not stand in for it,
        // and the host is not even looked at.
        var verdict = CodeFixEligibility.EvaluateWorkItem(
            Candidate() with { RepositoryListed = false, Binding = new RepositoryBinding { Url = "https://evil.example/o/r" } }, Facts(), Options());

        verdict.Codes.Should().Equal(CodeFixReasonCode.RepositoryNotListed);
        verdict.Describe().Should().Contain("Flou21/hephaisto-fixture-dotnet");
    }

    [Fact]
    public void TheHostIsTheCloneUrls_NotGitHubs()
    {
        var inCluster = Candidate() with { Binding = new RepositoryBinding { Url = "http://coder-git.hephaisto-coder.svc/Flou21/hephaisto-fixture-dotnet.git" } };

        CodeFixEligibility.EvaluateWorkItem(inCluster, Facts(), Options())
            .Codes.Should().Equal(CodeFixReasonCode.RepositoryHostNotAllowed);

        CodeFixEligibility.EvaluateWorkItem(inCluster, Facts(), Options() with { AllowedRepositoryHosts = ["coder-git.hephaisto-coder.svc"] })
            .Eligible.Should().BeTrue();
    }

    [Fact]
    public void Caps_RefuseAtTheBoundary_NotAfterIt()
    {
        CodeFixEligibility.EvaluateWorkItem(Candidate(), Facts() with { RepositoryAttemptsToday = 2 }, Options()).Eligible.Should().BeTrue();
        CodeFixEligibility.EvaluateWorkItem(Candidate(), Facts() with { RepositoryAttemptsToday = 3 }, Options()).Eligible.Should().BeFalse();
        CodeFixEligibility.EvaluateWorkItem(Candidate(), Facts() with { CostTodayUsd = 49.99m }, Options()).Eligible.Should().BeTrue();
        CodeFixEligibility.EvaluateWorkItem(Candidate(), Facts() with { CostTodayUsd = 50m }, Options()).Eligible.Should().BeFalse();
    }

    private static (WorkItemCandidate, CodeFixFacts) Apply(string change) => Apply(change, Candidate(), Facts());

    private static (WorkItemCandidate, CodeFixFacts) Apply(string change, WorkItemCandidate c, CodeFixFacts f) => change switch
    {
        "mode off" => (c, f with { Mode = CodeFixMode.Off }),
        "agent off" => (c, f with { AgentMode = AgentMode.Off }),
        "stop" => (c, f with { EmergencyStop = true }),
        "latch" => (c, f with { RunawayLatched = true }),
        "not taken" => (c with { Taken = false }, f),
        "not listed" => (c with { RepositoryListed = false }, f),
        "no url" => (c with { Binding = null }, f),
        "foreign host" => (c with { Binding = new RepositoryBinding { Url = "https://gitlab.example/Flou21/hephaisto-fixture-dotnet" } }, f),
        "attempt open" => (c, f with { IncidentAttemptOpen = true }),
        "repo cap" => (c, f with { RepositoryAttemptsToday = 3 }),
        "concurrency" => (c, f with { JobsInFlight = 1 }),
        "cost" => (c, f with { CostTodayUsd = 50 }),
        "llm budget" => (c, f with { LlmBudgetExhausted = true }),
        _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
    };
}
