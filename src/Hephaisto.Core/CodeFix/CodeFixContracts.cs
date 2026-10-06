using Hephaisto.Core.Domain;

namespace Hephaisto.Core.CodeFix;

/// <summary>
/// One entry of the operator's authorization list: this workload's code lives in this repository.
/// </summary>
/// <remarks>
/// The first half of a double opt-in. The chart value <c>codeFix.repositories</c> says Hephaisto may
/// start a coder for this workload; dev-context's <c>repos.yaml</c> (<c>coderEnabled</c>) says the
/// coder is willing to work on the repository. Either one missing and nothing runs.
/// </remarks>
public sealed record RepositoryBinding
{
    /// <summary><c>namespace/Kind/name</c> of the controller, as <see cref="TargetRef.WorkloadKey"/> renders it.</summary>
    public string Workload { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string DefaultBranch { get; init; } = "main";

    /// <summary>Subdirectory the workload is built from; empty for the repository root.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>The repository's name: the last path segment of <see cref="Url"/>, without <c>.git</c>.</summary>
    public string Name
    {
        get
        {
            var trimmed = Url.TrimEnd('/');
            var last = trimmed[(trimmed.LastIndexOf('/') + 1)..];
            return last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
        }
    }

    /// <summary>The host of <see cref="Url"/>, lower-cased, or null when it is not an absolute URI.</summary>
    public string? Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;
}

/// <summary>What is known about the incident, gathered by the caller. No I/O happens past this point.</summary>
public sealed record CodeFixCandidate
{
    public required IncidentState State { get; init; }

    public required EscalationReason EscalationReason { get; init; }

    public required SignalKind Kind { get; init; }

    /// <summary>True when the incident is about Hephaisto itself or its coder namespace.</summary>
    public required bool SelfSignal { get; init; }

    /// <summary>How the latest investigation ended; null when there was none.</summary>
    public required TerminationReason? Termination { get; init; }

    public required string WorkloadKey { get; init; }

    public string? PrimaryCategory { get; init; }

    public double? PrimaryConfidence { get; init; }

    /// <summary>Evidence rows on the primary finding that survived the grounding check.</summary>
    public int GroundedEvidenceCount { get; init; }

    /// <summary>The binding for <see cref="WorkloadKey"/>, or null when the operator mapped none.</summary>
    public RepositoryBinding? Binding { get; init; }

    /// <summary>
    /// True when a human asked for this code fix through the console. A human judgement replaces
    /// the model's: the category, confidence and escalation-reason gates do not apply. Nothing
    /// else relaxes - not the kill switch, not the mapping, not a single cap.
    /// </summary>
    public bool RequestedByHuman { get; init; }
}

/// <summary>
/// What is known about a piece of work somebody handed over, gathered by the caller. The
/// counterpart of <see cref="CodeFixCandidate"/> for a <see cref="WorkItem"/>, and far smaller:
/// nothing about it is a diagnosis.
/// </summary>
public sealed record WorkItemCandidate
{
    /// <summary><c>owner/repo</c>, for the sentence of a refusal.</summary>
    public required string Repository { get; init; }

    /// <summary>The work item is still <see cref="WorkItemState.Taken"/>.</summary>
    public required bool Taken { get; init; }

    /// <summary>The repository is one the install lists for issues. The authorization.</summary>
    public required bool RepositoryListed { get; init; }

    /// <summary>Where the code is cloned from, and its default branch; null when nothing says.</summary>
    public RepositoryBinding? Binding { get; init; }
}

/// <summary>The world at the moment of judging, read by the caller.</summary>
public sealed record CodeFixFacts
{
    /// <summary>What the code-fix arms alone declare. The agent-side overrides are the flags below.</summary>
    public required CodeFixMode Mode { get; init; }

    public required AgentMode AgentMode { get; init; }

    public required bool EmergencyStop { get; init; }

    public required bool RunawayLatched { get; init; }

    /// <summary>An attempt is open for the same subject - the incident, or the work item.</summary>
    public bool IncidentAttemptOpen { get; init; }

    public bool WorkloadAttemptOpen { get; init; }

    public int RepositoryAttemptsToday { get; init; }

    public int JobsInFlight { get; init; }

    public decimal CostTodayUsd { get; init; }

    public bool LlmBudgetExhausted { get; init; }
}

/// <summary>The knobs the predicate reads. An empty instance permits nothing.</summary>
public sealed record CodeFixEligibilityOptions
{
    /// <summary>Primary-finding categories that may start a coder. Empty permits nothing.</summary>
    public IReadOnlyList<string> EligibleCategories { get; init; } = [];

    public double ConfidenceFloor { get; init; } = 0.7;

    /// <summary>Hosts a repository URL may point at. Empty permits nothing.</summary>
    public IReadOnlyList<string> AllowedRepositoryHosts { get; init; } = [];

    public int MaxAttemptsPerRepositoryPerDay { get; init; }

    public int MaxConcurrentJobs { get; init; }

    public decimal MaxCostUsdPerDay { get; init; }
}

/// <summary>
/// Why a code fix did or did not start. A closed set, carried beside the prose for the same reason
/// <see cref="Policy.PolicyReasonCode"/> is: metric labels and tests need a stable value, and
/// deriving one from a sentence is brittle in exactly the wrong place.
/// </summary>
public enum CodeFixReasonCode
{
    ModeOff = 0,
    AgentOff = 1,
    EmergencyStop = 2,
    RunawayLatched = 3,
    SelfSignal = 4,
    IncidentNotEscalated = 5,
    EscalationReasonNotEligible = 6,
    InvestigationNotConcluded = 7,
    NoPrimaryFinding = 8,
    CategoryNotEligible = 9,
    InfraOnlyKind = 10,
    ConfidenceBelowFloor = 11,
    Ungrounded = 12,
    NoRepositoryMapping = 13,
    RepositoryHostNotAllowed = 14,
    AttemptAlreadyOpen = 15,
    WorkloadAttemptOpen = 16,
    RepositoryDailyCapReached = 17,
    ConcurrencyCapReached = 18,
    DailyCostCapReached = 19,
    LlmBudgetExhausted = 20,

    /// <summary>A work item's repository is not one the install lists for issues.</summary>
    RepositoryNotListed = 21,

    /// <summary>The work item was cancelled or finished between being read and being judged.</summary>
    WorkItemNotTaken = 22,
}

/// <summary>The predicate's answer: eligible, or every reason it is not.</summary>
public sealed record CodeFixVerdict
{
    public required bool Eligible { get; init; }

    public required IReadOnlyList<CodeFixReasonCode> Codes { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>The first code, for a metric label; null when eligible.</summary>
    public CodeFixReasonCode? PrimaryCode => Codes.Count > 0 ? Codes[0] : null;

    /// <summary>
    /// True when every refusal is about the mode alone - the case the console words as "would have
    /// started a code fix, but the mode is Off". Same honesty as the would-have panel.
    /// </summary>
    public bool WouldHaveStarted => Codes.Count > 0 && Codes.All(c => c == CodeFixReasonCode.ModeOff);

    public string Describe() => Eligible ? "eligible" : string.Join("; ", Reasons);
}
