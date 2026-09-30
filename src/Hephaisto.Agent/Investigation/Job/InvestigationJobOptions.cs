using Hephaisto.Core.Investigations;

namespace Hephaisto.Agent.Investigations.Jobs;

/// <summary>
/// Investigation in a Job (v0.12.0 F5). Bound from <c>Investigation:Job:*</c>; the chart emits
/// <c>Investigation__Job__*</c> only when <c>investigation.job.enabled</c> is set.
/// </summary>
/// <remarks>
/// Every default here is inert: <see cref="Enabled"/> false resolves every investigation to
/// in-process whatever the arms say. The Job itself borrows the code-fix stage's image, namespace,
/// ServiceAccount, Secret and egress proxy (<c>CodeFix:*</c>), which is why the chart requires
/// <c>codeFix.enabled</c> for this.
/// </remarks>
public sealed class InvestigationJobOptions
{
    public const string SectionName = "Investigation:Job";

    /// <summary>Whether the investigator port, its NetworkPolicy and the Job's credentials exist at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The environment arm, kept raw so the strict parser in
    /// <see cref="InvestigationExecutorResolver"/> sees exactly what was typed.
    /// </summary>
    public string? Executor { get; set; }

    /// <summary>The investigator port. Nothing else answers on it, and it answers nothing else.</summary>
    public int Port { get; set; } = 8084;

    /// <summary>
    /// The URL a Job calls, <c>http://&lt;release&gt;.&lt;ns&gt;.svc:&lt;port&gt;/investigate</c>. The chart
    /// sets it; empty means the Job path cannot be used and every Job investigation falls back.
    /// </summary>
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>
    /// The Claude model the investigator runs, passed through as <c>CODEFIX_MODEL</c>. Empty lets the
    /// CLI choose the account default. Independent of <c>CodeFix:Model</c>: the point of F5 is often a
    /// stronger model for diagnosis than for the plan.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Passed through as <c>CODEFIX_SDK</c>. <c>fake</c> runs scripted, $0 - dev and CI only.</summary>
    public string Sdk { get; set; } = "real";

    /// <summary>The run's cap, passed to the SDK as <c>maxBudgetUsd</c>. Notional on a subscription.</summary>
    public decimal MaxCostUsd { get; set; } = 2m;

    /// <summary>The Job's <c>activeDeadlineSeconds</c>, and the in-process wait for its answer.</summary>
    public TimeSpan Deadline { get; set; } = TimeSpan.FromMinutes(15);

    public int MaxTurns { get; set; } = 60;

    public int MaxConcurrentJobs { get; set; } = 1;

    public int MaxJobsPerHour { get; set; } = 20;

    /// <summary>
    /// What an investigation does when every Job slot is taken or the hourly cap is reached:
    /// investigate in-process (the v0.12.0 default), or wait for a slot so the in-process model is
    /// never called. <see cref="InvestigationOverflow.Wait"/> also sends incidents about another
    /// cluster to a Job; its Grafana tools still read them.
    /// </summary>
    public InvestigationOverflow Overflow { get; set; } = InvestigationOverflow.InProcess;

    /// <summary>
    /// When a Job cannot give an answer - it was refused, failed, vanished, hit a subscription limit
    /// or outlived its deadline - investigate in-process instead of escalating with nothing.
    /// </summary>
    public bool FallbackToInProcess { get; set; } = true;

    /// <summary>How often the run polls its Job. Short: the worker slot is held while it waits.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

    public InvestigationJobSourceOptions Source { get; set; } = new();

    public ExecutorCaps Caps => new() { MaxConcurrentJobs = MaxConcurrentJobs, MaxJobsPerHour = MaxJobsPerHour };
}

/// <summary>What an investigation does when it cannot have a Job right now.</summary>
public enum InvestigationOverflow
{
    InProcess,
    Wait,
}

/// <summary>A read-only checkout of the running revision for the investigator (Part 8).</summary>
public sealed class InvestigationJobSourceOptions
{
    /// <summary>Off by default: a clone adds a credential and a minute to every investigation.</summary>
    public bool Enabled { get; set; }
}
