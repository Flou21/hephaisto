using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Agent.Llm;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Investigations;

namespace Hephaisto.Agent.Investigations.Jobs;

/// <summary>
/// Runs an investigation's model loop as Claude Code in a Job (v0.12.0 F5), serving it the run's
/// own tools on the investigator port.
/// </summary>
/// <remarks>
/// <para>
/// <b>No table, no watcher.</b> The run holds its worker slot and polls its Job, exactly as an
/// in-process run holds the slot while it talks to a provider. A Job outlives an agent restart,
/// but its session does not: the orphan's next tool call is refused, its driver ends, and
/// <c>StrandedIncidentRequeue</c> re-runs the incident - the restart semantics of v0.11, with no
/// new state to reconcile.
/// </para>
/// <para>
/// <b>The answer is the conclude call, not the log.</b> A conclusion reaches the runner's holder
/// through the endpoint as a recorded step, and it is grounded like any other. The framed result
/// in the pod log carries only what the run cost and how it ended; a missing or forged frame after
/// a conclude costs the bookkeeping, never the diagnosis.
/// </para>
/// <para>
/// <b>Everything that goes wrong is a fallback.</b> A refused launch, a failed or vanished Job, a
/// missing credential, a subscription limit, a deadline: the runner investigates in-process
/// instead, as it would have without F5. Only a Job that ran and chose not to conclude - out of
/// turns, out of budget, or with nothing to say - ends the investigation as that.
/// </para>
/// </remarks>
public sealed class KubernetesInvestigationJobLoop(
    IInvestigationExecutorSwitch executor,
    InvestigationJobSessions sessions,
    ICodeFixJobLauncher launcher,
    PromptComposer prompts,
    Pipeline.InvestigationTracker tracker,
    IOptionsMonitor<InvestigationJobOptions> jobOptions,
    IOptionsMonitor<CodeFixOptions> codeFixOptions,
    IClock clock,
    ILogger<KubernetesInvestigationJobLoop> logger) : IInvestigationJobLoop
{
    /// <summary>After a conclude, how long the Job gets to print its frame before it is removed.</summary>
    internal static readonly TimeSpan FrameGrace = TimeSpan.FromSeconds(60);

    /// <summary>How often a running Job re-reads the executor, so switching back takes effect mid-run.</summary>
    private const int ResolveEveryPolls = 10;

    private readonly ConcurrentQueue<DateTimeOffset> _launches = new();

    public async Task<JobLoopDecision> DecideAsync(Incident incident, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incident);

        InvestigationExecutorResolution resolution;

        try
        {
            resolution = await executor.ResolveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A switch that cannot be read reads as in-process: the path that needs nothing.
            logger.LogWarning(ex, "Could not resolve the investigation executor; investigating in-process");
            return JobLoopDecision.InProcess("the executor could not be resolved");
        }

        if (resolution.Effective != InvestigationExecutor.Job)
            return JobLoopDecision.InProcess(resolution.Explain());

        if (!launcher.IsAvailable)
            return JobLoopDecision.InProcess("this process has no cluster client");

        var j = jobOptions.CurrentValue;
        var choice = InvestigationExecutorPolicy.Decide(
            resolution.Effective,
            j.Caps,
            sessions.ActiveCount,
            LaunchesInTheLastHour(),
            incident.Target.IsForeignTo(prompts.AgentCluster));

        return new JobLoopDecision(choice, choice switch
        {
            ExecutorChoice.Job => "a Job slot is free",
            ExecutorChoice.Overflow => $"all {j.MaxConcurrentJobs} Job slot(s) are taken",
            ExecutorChoice.HourlyCapReached => $"{j.MaxJobsPerHour} Jobs have started in the last hour",
            ExecutorChoice.ForeignCluster => "the incident is about another cluster, and a Job's tools read only this one",
            _ => resolution.Explain(),
        });
    }

    public InvestigationBudgetOptions BudgetFor(InvestigationBudgetOptions inProcess)
    {
        ArgumentNullException.ThrowIfNull(inProcess);
        var j = jobOptions.CurrentValue;

        // Only the tool-call cap and the wall clock bind a Job's tools; the step, token and cost
        // caps belong to a chat client this path does not have. A turn per tool call is the
        // ceiling a Job can reach anyway.
        return new InvestigationBudgetOptions
        {
            MaxSteps = inProcess.MaxSteps,
            MaxToolCalls = Math.Max(inProcess.MaxToolCalls, j.MaxTurns),
            MaxWallClock = j.Deadline,
            MaxInputTokens = inProcess.MaxInputTokens,
            MaxCostUsd = inProcess.MaxCostUsd,
            MaxConsecutiveNoToolTurns = inProcess.MaxConsecutiveNoToolTurns,
        };
    }

    public async Task<JobLoopOutcome> RunAsync(JobLoopContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var j = jobOptions.CurrentValue;
        var o = codeFixOptions.CurrentValue;

        var missing = new[]
        {
            (string.IsNullOrWhiteSpace(j.EndpointUrl), "Investigation:Job:EndpointUrl"),
            (string.IsNullOrWhiteSpace(o.Image), "CodeFix:Image"),
            (string.IsNullOrWhiteSpace(o.ContextRepositoryUrl), "CodeFix:ContextRepositoryUrl"),
        }.Where(m => m.Item1).Select(m => m.Item2).ToList();

        if (missing.Count > 0)
            return Fall(j, $"not configured: {string.Join(", ", missing)} is not set");

        var attemptId = Guid.CreateVersion7();
        var started = Stopwatch.GetTimestamp();

        var token = sessions.Open(new InvestigationJobSession
        {
            InvestigationId = context.InvestigationId,
            IncidentId = context.Incident.Id,
            Tools = context.Tools.ToDictionary(t => t.Name, StringComparer.Ordinal),
            Conclusion = context.Conclusion,
            ExpiresAt = clock.UtcNow + j.Deadline + FrameGrace,
        });

        _launches.Enqueue(clock.UtcNow);
        tracker.Relabel(context.Incident.Id, string.IsNullOrWhiteSpace(j.Model) ? "claude-code (Job)" : $"{j.Model} (Job)");

        string? jobName = null;

        try
        {
            var request = InvestigateRequestBuilder.Build(attemptId, context, j, o, token, source: null);
            var json = JsonSerializer.Serialize(request, CodeFixContract.Json);
            var spec = InvestigateJobSpec.Job(attemptId, context.Incident.Id, context.InvestigationId, withSource: false, j, o);

            try
            {
                jobName = await launcher.LaunchAsync(
                        spec,
                        created => InvestigateJobSpec.RequestConfigMap(
                            attemptId, context.Incident.Id, context.InvestigationId, o, json, created),
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Fall(j, $"the Job was not started: {ex.Message}");
            }

            logger.LogInformation(
                "Investigating incident {IncidentId} in Job {Job} (investigation {InvestigationId})",
                context.Incident.Id, jobName, context.InvestigationId);

            var ended = await WaitAsync(jobName, context, j, ct).ConfigureAwait(false);

            if (ended is not null)
                return ended;

            var log = await launcher.ReadResultLogAsync(jobName, ct).ConfigureAwait(false);
            var parsed = CodeFixResultParser.ParseInvestigate(log, attemptId);

            return Collect(parsed, context, j, jobName, Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (jobName is not null)
        {
            // The agent is stopping. The Job would outlive it only to have every call refused, so
            // it goes now rather than burning its deadline.
            await DeleteQuietlyAsync(jobName).ConfigureAwait(false);
            throw;
        }
        finally
        {
            sessions.Close(context.InvestigationId);
        }
    }

    /// <summary>Polls until the Job ends. Null when it ended in a way the log can speak for.</summary>
    private async Task<JobLoopOutcome?> WaitAsync(
        string jobName, JobLoopContext context, InvestigationJobOptions j, CancellationToken ct)
    {
        var deadline = clock.UtcNow + j.Deadline + FrameGrace;
        DateTimeOffset? concludedAt = null;

        for (var poll = 1; ; poll++)
        {
            await Task.Delay(j.PollInterval, ct).ConfigureAwait(false);

            var observed = await launcher.ObserveAsync(jobName, ct).ConfigureAwait(false);
            var concluded = context.Conclusion.Value is not null;

            if (concluded)
                concludedAt ??= clock.UtcNow;

            switch (observed.Phase)
            {
                case CodeFixJobPhase.Succeeded or CodeFixJobPhase.Failed:
                    return null;

                case CodeFixJobPhase.Missing when concluded:
                    return Concluded(context, null, jobName, "the Job was gone before its result could be read");

                case CodeFixJobPhase.Missing:
                    return Fall(j, "the Job disappeared before it concluded");
            }

            if (concludedAt is { } at && clock.UtcNow - at > FrameGrace)
            {
                // The answer is in; the frame is bookkeeping, and a Job that will not print it does
                // not get to hold a slot for its whole deadline.
                await DeleteQuietlyAsync(jobName).ConfigureAwait(false);
                return Concluded(context, null, jobName, "the Job did not report within a minute of concluding");
            }

            if (clock.UtcNow > deadline)
            {
                await DeleteQuietlyAsync(jobName).ConfigureAwait(false);
                return concluded
                    ? Concluded(context, null, jobName, "the Job outlived its deadline after concluding")
                    : Fall(j, $"the Job gave no answer within its {j.Deadline.TotalMinutes:F0}-minute deadline");
            }

            if (!concluded && poll % ResolveEveryPolls == 0)
            {
                var resolution = await executor.ResolveAsync(ct).ConfigureAwait(false);

                if (resolution.Effective != InvestigationExecutor.Job)
                {
                    await DeleteQuietlyAsync(jobName).ConfigureAwait(false);
                    return Fall(j, $"the executor was switched mid-run ({resolution.Explain()})");
                }
            }
        }
    }

    /// <summary>Turns the frame into how the loop ended.</summary>
    private JobLoopOutcome Collect(
        CodeFixParse<InvestigateResult> parsed,
        JobLoopContext context,
        InvestigationJobOptions j,
        string jobName,
        TimeSpan elapsed)
    {
        var result = parsed.Result;

        if (result is not null)
            RecordRun(context, result, jobName, elapsed);

        if (context.Conclusion.Value is not null)
            return Concluded(context, result, jobName, parsed.Ok ? null : $"its result could not be read: {parsed.Violation}");

        if (result is null)
            return Fall(j, $"the Job gave no readable answer: {parsed.Violation}");

        return result.Outcome switch
        {
            "no_conclusion" or "concluded" => Ended(result, TerminationReason.Stalled, "the Job ended without concluding"),
            "max_turns" => Ended(result, TerminationReason.StepBudgetExhausted, "the Job ran out of turns before concluding"),
            "budget_exhausted" => Ended(result, TerminationReason.CostBudgetExhausted, "the Job ran out of budget before concluding"),
            _ => Fall(j, $"the Job ended {result.Outcome}{(result.Error is { Length: > 0 } e ? $": {e}" : string.Empty)}"),
        };
    }

    private static JobLoopOutcome Concluded(JobLoopContext context, InvestigateResult? result, string jobName, string? note) => new()
    {
        Termination = TerminationReason.Concluded,
        ModelId = result?.Model,
        Turns = result?.Turns ?? 0,
        Error = note is null ? null : $"{jobName}: {note}",
    };

    private static JobLoopOutcome Ended(InvestigateResult result, TerminationReason termination, string why) => new()
    {
        Termination = termination,
        ModelId = result.Model,
        Turns = result.Turns,
        Error = result.Error is { Length: > 0 } e ? $"{why}: {e}" : why,
    };

    /// <summary>
    /// A fallback, or - with <see cref="InvestigationJobOptions.FallbackToInProcess"/> off - the
    /// failure itself, so an operator who chose "Job or nothing" gets an escalation that says why.
    /// </summary>
    private JobLoopOutcome Fall(InvestigationJobOptions j, string reason)
    {
        logger.LogWarning("Investigator Job gave no answer: {Reason}", reason);

        LlmInstrumentation.InvestigationJobFallbacks.Add(1, new System.Diagnostics.TagList
        {
            { "reason", FallbackKind(reason) },
            { "fallback", j.FallbackToInProcess },
        });

        return j.FallbackToInProcess
            ? JobLoopOutcome.Fallback(reason)
            : new JobLoopOutcome { Termination = TerminationReason.Faulted, Error = reason };
    }

    /// <summary>
    /// The Job's model turns as one step, so the investigation's token and cost sums include them.
    /// </summary>
    /// <remarks>
    /// On a subscription the cost the SDK reports is notional - nothing is billed per token - and
    /// charging it to the global LLM budget would trip hourly caps sized for metered API spend, and
    /// with them the runaway latch. So a subscription run is recorded at $0 with its notional cost
    /// in the step's text; an API-key run is charged what it cost.
    /// </remarks>
    private static void RecordRun(JobLoopContext context, InvestigateResult result, string jobName, TimeSpan elapsed)
    {
        var charged = result.Billing == "api" ? result.CostUsd : 0m;

        context.Recorder.RecordLlmTurn(
            result.Model,
            result.InputTokens,
            result.OutputTokens,
            charged,
            (long)elapsed.TotalMilliseconds,
            result.Outcome is "concluded" or "no_conclusion" or "max_turns" or "budget_exhausted" ? null : result.Error ?? result.Outcome,
            $"-> Claude Code in Job {jobName}: {result.Turns} turn(s), {result.Outcome}, {result.Billing}"
                + (result.Billing == "subscription" ? $" (notional ${result.CostUsd:F4}, not charged)" : string.Empty));
    }

    /// <summary>The reason as a closed vocabulary, safe as a metric label.</summary>
    internal static string FallbackKind(string reason) => reason switch
    {
        _ when reason.StartsWith("not configured", StringComparison.Ordinal) => "not_configured",
        _ when reason.StartsWith("the Job was not started", StringComparison.Ordinal) => "launch_refused",
        _ when reason.Contains("disappeared", StringComparison.Ordinal) => "vanished",
        _ when reason.Contains("deadline", StringComparison.Ordinal) => "deadline",
        _ when reason.Contains("switched mid-run", StringComparison.Ordinal) => "switched",
        _ when reason.Contains("no readable answer", StringComparison.Ordinal) => "unreadable",
        _ when reason.Contains("rate_limited", StringComparison.Ordinal) => "rate_limited",
        _ when reason.Contains("no_credential", StringComparison.Ordinal) => "no_credential",
        _ => "failed",
    };

    private int LaunchesInTheLastHour()
    {
        var since = clock.UtcNow - TimeSpan.FromHours(1);

        while (_launches.TryPeek(out var oldest) && oldest < since)
            _launches.TryDequeue(out _);

        return _launches.Count;
    }

    private async Task DeleteQuietlyAsync(string jobName)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await launcher.DeleteAsync(jobName, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete investigator Job {Job}; its TTL will", jobName);
        }
    }
}
