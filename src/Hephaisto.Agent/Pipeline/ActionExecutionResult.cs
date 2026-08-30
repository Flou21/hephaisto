using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Pipeline;

/// <summary>Why an execution attempt ended the way it did.</summary>
public enum ActionExecutionOutcome
{
    /// <summary>The API call was made. In DryRun that means the server validated and discarded it.</summary>
    Executed = 0,

    /// <summary>Admission refused it. Nothing was sent to the cluster.</summary>
    Refused = 1,

    /// <summary>The call was made and failed. The cluster may or may not have changed.</summary>
    Failed = 2,

    /// <summary>This executor cannot perform that action type. Nothing was attempted.</summary>
    Unsupported = 3,

    /// <summary>The target could not be read, so there is no PreState to verify or revert against.</summary>
    NoPreState = 4,
}

public sealed record ActionExecutionResult
{
    public required ActionExecutionOutcome Outcome { get; init; }

    /// <summary>Set when <see cref="Outcome"/> is <see cref="ActionExecutionOutcome.Refused"/>.</summary>
    public AdmissionRefusal Refusal { get; init; }

    public string? Detail { get; init; }

    /// <summary>True when the call carried <c>dryRun=All</c> and the cluster did not change.</summary>
    public bool DryRun { get; init; }

    /// <summary>The mode admission resolved, which is what actually decided dry-run.</summary>
    public AgentMode Mode { get; init; }

    public bool Changed => Outcome == ActionExecutionOutcome.Executed && !DryRun;
}
