using Hephaisto.Core.Domain;

namespace Hephaisto.Core.CodeFix;

/// <summary>
/// Decides whether an escalated incident may start a coder. The code-fix sibling of
/// <see cref="Policy.PolicyEngine"/>, and built to the same rules.
/// </summary>
/// <remarks>
/// <para>
/// Pure, static and deterministic: no clock, no database, no model. The caller gathers
/// <see cref="CodeFixCandidate"/> and <see cref="CodeFixFacts"/> and this decides, which is what lets
/// every refusal below be demonstrated by a unit test rather than argued about.
/// </para>
/// <para>
/// <b>Default-deny.</b> Eligible is reached only by falling off the end. Empty
/// <see cref="CodeFixEligibilityOptions.EligibleCategories"/> and
/// <see cref="CodeFixEligibilityOptions.AllowedRepositoryHosts"/> permit nothing, and zero caps
/// permit nothing, so an options section that failed to bind leaves the stage inert.
/// </para>
/// <para>
/// <b>Codes accumulate rather than short-circuit.</b> "Why did this not start a code fix" is asked
/// once, after the fact, and <see cref="CodeFixReasonCode.ModeOff"/> alone would hide the more
/// useful answer that the workload has no repository mapped either. That accumulation is also what
/// makes the Off verdict honest: an incident refused ONLY for the mode is one that would have
/// started, and the console says so.
/// </para>
/// <para>
/// <b><see cref="AgentMode.Observe"/> does not refuse.</b> It is the property this design rests on:
/// production runs in Observe, and a coder mutates a git branch, never the cluster. Only an agent
/// that is Off, stopped or latched stops the coder too.
/// </para>
/// </remarks>
public static class CodeFixEligibility
{
    /// <summary>
    /// Escalations that mean "the diagnosis is done and nobody can act on the cluster" - the case a
    /// code fix exists for. Everything else (budget, grounding, a failed investigation, a storm) is
    /// an agent that did not finish thinking, and a coder would be building on nothing.
    /// </summary>
    public static readonly IReadOnlyList<EscalationReason> EligibleEscalations =
        [EscalationReason.NoPlanProduced, EscalationReason.PolicyDenied];

    /// <summary>
    /// Kinds whose cause is the platform, never the application's code, whatever category the model
    /// wrote. <see cref="Finding.Category"/> is free text a model chose; this is the hard rule under it.
    /// </summary>
    public static readonly IReadOnlyList<SignalKind> InfraOnlyKinds =
        [SignalKind.Unschedulable, SignalKind.ImagePullBackOff, SignalKind.NodePressure, SignalKind.PvcNearlyFull];

    public static CodeFixVerdict Evaluate(CodeFixCandidate candidate, CodeFixFacts facts, CodeFixEligibilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);

        var codes = new List<CodeFixReasonCode>();
        var reasons = new List<string>();

        void Refuse(CodeFixReasonCode code, string reason)
        {
            codes.Add(code);
            reasons.Add(reason);
        }

        // 1. The switches. First, and none of them ends the evaluation: the rest of the verdict is
        //    what tells an operator whether turning the mode on would have mattered.
        if (facts.Mode == CodeFixMode.Off)
            Refuse(CodeFixReasonCode.ModeOff, "code-fix mode is Off");

        if (facts.AgentMode == AgentMode.Off)
            Refuse(CodeFixReasonCode.AgentOff, "agent is off");

        if (facts.EmergencyStop)
            Refuse(CodeFixReasonCode.EmergencyStop, "the emergency stop is engaged");

        if (facts.RunawayLatched)
            Refuse(CodeFixReasonCode.RunawayLatched, "the runaway latch is set");

        // 2. Never about ourselves. A coder asked to fix the thing that runs coders is a loop.
        if (candidate.SelfSignal)
            Refuse(CodeFixReasonCode.SelfSignal, "the incident is about Hephaisto or its coder namespace");

        // 3. Only an incident the agent has finished with and handed to a human.
        if (candidate.State != IncidentState.Escalated)
            Refuse(CodeFixReasonCode.IncidentNotEscalated, $"incident is {candidate.State}, not Escalated");

        if (!candidate.RequestedByHuman && !EligibleEscalations.Contains(candidate.EscalationReason))
            Refuse(CodeFixReasonCode.EscalationReasonNotEligible,
                $"escalated for {candidate.EscalationReason}, which is not a finished diagnosis");

        // 4. A diagnosis to build on. The coder receives the grounded evidence and nothing else, so
        //    without a concluded investigation and a primary finding it would start from nothing.
        if (candidate.Termination != TerminationReason.Concluded)
            Refuse(CodeFixReasonCode.InvestigationNotConcluded,
                candidate.Termination is null ? "no investigation" : $"investigation ended {candidate.Termination}");

        var hasPrimary = !string.IsNullOrWhiteSpace(candidate.PrimaryCategory);

        if (!hasPrimary)
            Refuse(CodeFixReasonCode.NoPrimaryFinding, "no primary finding");

        if (hasPrimary && !candidate.RequestedByHuman
            && !options.EligibleCategories.Contains(candidate.PrimaryCategory!.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            Refuse(CodeFixReasonCode.CategoryNotEligible,
                $"primary finding category '{candidate.PrimaryCategory}' is not eligible");
        }

        if (InfraOnlyKinds.Contains(candidate.Kind))
            Refuse(CodeFixReasonCode.InfraOnlyKind, $"{candidate.Kind} is a platform condition, never a code bug");

        if (hasPrimary && !candidate.RequestedByHuman && (candidate.PrimaryConfidence ?? 0) < options.ConfidenceFloor)
        {
            Refuse(CodeFixReasonCode.ConfidenceBelowFloor,
                $"confidence {candidate.PrimaryConfidence ?? 0:0.00} is below the floor {options.ConfidenceFloor:0.00}");
        }

        if (hasPrimary && candidate.GroundedEvidenceCount <= 0)
            Refuse(CodeFixReasonCode.Ungrounded, "the primary finding has no grounded evidence");

        // 5. The operator's half of the double opt-in.
        if (candidate.Binding is not { } binding || string.IsNullOrWhiteSpace(binding.Url))
        {
            Refuse(CodeFixReasonCode.NoRepositoryMapping, $"no repository is mapped for {candidate.WorkloadKey}");
        }
        else if (binding.Host is not { } host
                 || !options.AllowedRepositoryHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            Refuse(CodeFixReasonCode.RepositoryHostNotAllowed,
                $"repository host '{binding.Host ?? binding.Url}' is not allowed");
        }

        // 6. One at a time, and within the caps. Zero caps permit nothing.
        if (facts.IncidentAttemptOpen)
            Refuse(CodeFixReasonCode.AttemptAlreadyOpen, "a code fix is already open for this incident");

        if (facts.WorkloadAttemptOpen)
            Refuse(CodeFixReasonCode.WorkloadAttemptOpen, "a code fix is already open for this workload");

        if (facts.RepositoryAttemptsToday >= options.MaxAttemptsPerRepositoryPerDay)
        {
            Refuse(CodeFixReasonCode.RepositoryDailyCapReached,
                $"{facts.RepositoryAttemptsToday} attempts on this repository today (cap {options.MaxAttemptsPerRepositoryPerDay})");
        }

        if (facts.JobsInFlight >= options.MaxConcurrentJobs)
            Refuse(CodeFixReasonCode.ConcurrencyCapReached,
                $"{facts.JobsInFlight} coder job(s) running (cap {options.MaxConcurrentJobs})");

        if (facts.CostTodayUsd >= options.MaxCostUsdPerDay)
            Refuse(CodeFixReasonCode.DailyCostCapReached,
                $"coder spend today ${facts.CostTodayUsd:0.00} (cap ${options.MaxCostUsdPerDay:0.00})");

        if (facts.LlmBudgetExhausted)
            Refuse(CodeFixReasonCode.LlmBudgetExhausted, "the global LLM budget is exhausted");

        return new CodeFixVerdict { Eligible = codes.Count == 0, Codes = codes, Reasons = reasons };
    }
}
