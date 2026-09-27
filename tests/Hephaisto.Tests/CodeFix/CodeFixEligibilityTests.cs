using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.CodeFix;

/// <summary>
/// The code-fix gate, held to the PolicyEngineTests standard: every reason code is reachable by
/// exactly one change to a permissive baseline, and the baseline itself is proven permissive.
/// </summary>
public sealed class CodeFixEligibilityTests
{
    private static CodeFixCandidate Candidate() => new()
    {
        State = IncidentState.Escalated,
        EscalationReason = EscalationReason.NoPlanProduced,
        Kind = SignalKind.CrashLoopBackOff,
        SelfSignal = false,
        Termination = TerminationReason.Concluded,
        WorkloadKey = "hephaisto-chaos/Deployment/shop-api",
        PrimaryCategory = "application",
        PrimaryConfidence = 0.9,
        GroundedEvidenceCount = 2,
        Binding = new RepositoryBinding
        {
            Workload = "hephaisto-chaos/Deployment/shop-api",
            Url = "https://github.com/Flou21/hephaisto-fixture-dotnet",
            DefaultBranch = "main",
        },
    };

    private static CodeFixFacts Facts() => new()
    {
        Mode = CodeFixMode.Plan,
        AgentMode = AgentMode.Observe,
        EmergencyStop = false,
        RunawayLatched = false,
    };

    private static CodeFixEligibilityOptions Options() => new()
    {
        EligibleCategories = ["application"],
        ConfidenceFloor = 0.7,
        AllowedRepositoryHosts = ["github.com"],
        MaxAttemptsPerRepositoryPerDay = 3,
        MaxConcurrentJobs = 1,
        MaxCostUsdPerDay = 50,
    };

    [Fact]
    public void Baseline_IsEligible_InObserveMode()
    {
        // The property the design rests on: production runs in Observe, and Observe must not
        // refuse. If this fails, the feature can never run where it is for.
        var verdict = CodeFixEligibility.Evaluate(Candidate(), Facts(), Options());

        verdict.Eligible.Should().BeTrue(verdict.Describe());
        verdict.Codes.Should().BeEmpty();
    }

    [Fact]
    public void EmptyOptions_PermitNothing()
    {
        var verdict = CodeFixEligibility.Evaluate(Candidate(), Facts(), new CodeFixEligibilityOptions());

        verdict.Eligible.Should().BeFalse();
        verdict.Codes.Should().Contain([
            CodeFixReasonCode.CategoryNotEligible,
            CodeFixReasonCode.RepositoryHostNotAllowed,
            CodeFixReasonCode.RepositoryDailyCapReached,
            CodeFixReasonCode.ConcurrencyCapReached,
            CodeFixReasonCode.DailyCostCapReached,
        ]);
    }

    public static TheoryData<string, CodeFixReasonCode> OneChange => new()
    {
        { "mode off", CodeFixReasonCode.ModeOff },
        { "agent off", CodeFixReasonCode.AgentOff },
        { "stop", CodeFixReasonCode.EmergencyStop },
        { "latch", CodeFixReasonCode.RunawayLatched },
        { "self", CodeFixReasonCode.SelfSignal },
        { "not escalated", CodeFixReasonCode.IncidentNotEscalated },
        { "budget escalation", CodeFixReasonCode.EscalationReasonNotEligible },
        { "not concluded", CodeFixReasonCode.InvestigationNotConcluded },
        { "no primary", CodeFixReasonCode.NoPrimaryFinding },
        { "category", CodeFixReasonCode.CategoryNotEligible },
        { "infra kind", CodeFixReasonCode.InfraOnlyKind },
        { "low confidence", CodeFixReasonCode.ConfidenceBelowFloor },
        { "ungrounded", CodeFixReasonCode.Ungrounded },
        { "no mapping", CodeFixReasonCode.NoRepositoryMapping },
        { "foreign host", CodeFixReasonCode.RepositoryHostNotAllowed },
        { "attempt open", CodeFixReasonCode.AttemptAlreadyOpen },
        { "workload open", CodeFixReasonCode.WorkloadAttemptOpen },
        { "repo cap", CodeFixReasonCode.RepositoryDailyCapReached },
        { "concurrency", CodeFixReasonCode.ConcurrencyCapReached },
        { "cost", CodeFixReasonCode.DailyCostCapReached },
        { "llm budget", CodeFixReasonCode.LlmBudgetExhausted },
    };

    [Theory]
    [MemberData(nameof(OneChange))]
    public void EveryReasonCode_IsReachable_ByExactlyOneChange(string change, CodeFixReasonCode expected)
    {
        var (candidate, facts) = Apply(change, Candidate(), Facts());

        var verdict = CodeFixEligibility.Evaluate(candidate, facts, Options());

        verdict.Eligible.Should().BeFalse();
        verdict.Codes.Should().Equal([expected], "the baseline is permissive, so only '{0}' can refuse", change);
        verdict.Reasons.Should().HaveCount(1);
    }

    [Fact]
    public void EveryReasonCode_IsCovered()
    {
        var covered = OneChange.Select(row => row.Data.Item2).ToHashSet();

        covered.Should().BeEquivalentTo(Enum.GetValues<CodeFixReasonCode>());
    }

    [Fact]
    public void ModeOff_DoesNotShortCircuit_AndAloneMeansWouldHaveStarted()
    {
        var off = CodeFixEligibility.Evaluate(Candidate(), Facts() with { Mode = CodeFixMode.Off }, Options());

        off.WouldHaveStarted.Should().BeTrue("mode is the only refusal");

        var offAndUnmapped = CodeFixEligibility.Evaluate(
            Candidate() with { Binding = null }, Facts() with { Mode = CodeFixMode.Off }, Options());

        offAndUnmapped.Codes.Should().Equal(CodeFixReasonCode.ModeOff, CodeFixReasonCode.NoRepositoryMapping);
        offAndUnmapped.WouldHaveStarted.Should().BeFalse("turning the mode on would still not start it");
    }

    [Theory]
    [InlineData(AgentMode.Observe)]
    [InlineData(AgentMode.DryRun)]
    [InlineData(AgentMode.Auto)]
    public void AnyAgentModeAboveOff_Permits(AgentMode mode)
    {
        CodeFixEligibility.Evaluate(Candidate(), Facts() with { AgentMode = mode }, Options())
            .Eligible.Should().BeTrue();
    }

    [Theory]
    [InlineData(SignalKind.Unschedulable)]
    [InlineData(SignalKind.ImagePullBackOff)]
    [InlineData(SignalKind.NodePressure)]
    [InlineData(SignalKind.PvcNearlyFull)]
    public void InfraOnlyKinds_RefuseWhateverTheModelCategorised(SignalKind kind)
    {
        var verdict = CodeFixEligibility.Evaluate(Candidate() with { Kind = kind }, Facts(), Options());

        verdict.Codes.Should().Equal(CodeFixReasonCode.InfraOnlyKind);
    }

    [Fact]
    public void PolicyDeniedEscalation_IsEligible()
    {
        CodeFixEligibility.Evaluate(
                Candidate() with { EscalationReason = EscalationReason.PolicyDenied }, Facts(), Options())
            .Eligible.Should().BeTrue();
    }

    [Fact]
    public void CategoryMatch_IsCaseAndWhitespaceInsensitive()
    {
        CodeFixEligibility.Evaluate(Candidate() with { PrimaryCategory = " Application " }, Facts(), Options())
            .Eligible.Should().BeTrue();
    }

    [Fact]
    public void ConfidenceAtTheFloor_Permits()
    {
        CodeFixEligibility.Evaluate(Candidate() with { PrimaryConfidence = 0.7 }, Facts(), Options())
            .Eligible.Should().BeTrue();
    }

    [Fact]
    public void Caps_RefuseAtTheBoundary_NotAfterIt()
    {
        var atCaps = Facts() with { RepositoryAttemptsToday = 3, JobsInFlight = 1, CostTodayUsd = 50m };

        CodeFixEligibility.Evaluate(Candidate(), atCaps, Options()).Codes.Should().Equal(
            CodeFixReasonCode.RepositoryDailyCapReached,
            CodeFixReasonCode.ConcurrencyCapReached,
            CodeFixReasonCode.DailyCostCapReached);

        var belowCaps = Facts() with { RepositoryAttemptsToday = 2, JobsInFlight = 0, CostTodayUsd = 49.99m };

        CodeFixEligibility.Evaluate(Candidate(), belowCaps, Options()).Eligible.Should().BeTrue();
    }

    [Fact]
    public void AHumanRequest_ReplacesTheModelsJudgement_AndNothingElse()
    {
        var weak = Candidate() with
        {
            RequestedByHuman = true,
            PrimaryCategory = "config",
            PrimaryConfidence = 0.2,
            EscalationReason = EscalationReason.BudgetExhausted,
        };

        CodeFixEligibility.Evaluate(weak, Facts(), Options()).Eligible.Should().BeTrue();

        // ...but a human cannot ask past the switches, the mapping or the caps.
        CodeFixEligibility.Evaluate(weak with { Binding = null }, Facts() with { EmergencyStop = true, JobsInFlight = 1 }, Options())
            .Codes.Should().Equal(
                CodeFixReasonCode.EmergencyStop,
                CodeFixReasonCode.NoRepositoryMapping,
                CodeFixReasonCode.ConcurrencyCapReached);
    }

    [Fact]
    public void CodeFixMode_IsOrderedByPermissiveness()
    {
        // The resolver takes Min; reordering the enum would silently invert it.
        ((int)CodeFixMode.Off).Should().BeLessThan((int)CodeFixMode.Plan);
        ((int)CodeFixMode.Plan).Should().BeLessThan((int)CodeFixMode.Pr);
        default(CodeFixMode).Should().Be(CodeFixMode.Off);
    }

    private static (CodeFixCandidate, CodeFixFacts) Apply(string change, CodeFixCandidate c, CodeFixFacts f) => change switch
    {
        "mode off" => (c, f with { Mode = CodeFixMode.Off }),
        "agent off" => (c, f with { AgentMode = AgentMode.Off }),
        "stop" => (c, f with { EmergencyStop = true }),
        "latch" => (c, f with { RunawayLatched = true }),
        "self" => (c with { SelfSignal = true }, f),
        "not escalated" => (c with { State = IncidentState.Investigating }, f),
        "budget escalation" => (c with { EscalationReason = EscalationReason.BudgetExhausted }, f),
        "not concluded" => (c with { Termination = TerminationReason.StepBudgetExhausted }, f),
        "no primary" => (c with { PrimaryCategory = null, PrimaryConfidence = null, GroundedEvidenceCount = 0 }, f),
        "category" => (c with { PrimaryCategory = "resource-limit" }, f),
        "infra kind" => (c with { Kind = SignalKind.ImagePullBackOff }, f),
        "low confidence" => (c with { PrimaryConfidence = 0.5 }, f),
        "ungrounded" => (c with { GroundedEvidenceCount = 0 }, f),
        "no mapping" => (c with { Binding = null }, f),
        "foreign host" => (c with { Binding = c.Binding! with { Url = "https://gitlab.example.com/x/y" } }, f),
        "attempt open" => (c, f with { IncidentAttemptOpen = true }),
        "workload open" => (c, f with { WorkloadAttemptOpen = true }),
        "repo cap" => (c, f with { RepositoryAttemptsToday = 3 }),
        "concurrency" => (c, f with { JobsInFlight = 1 }),
        "cost" => (c, f with { CostTodayUsd = 50m }),
        "llm budget" => (c, f with { LlmBudgetExhausted = true }),
        _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
    };
}
