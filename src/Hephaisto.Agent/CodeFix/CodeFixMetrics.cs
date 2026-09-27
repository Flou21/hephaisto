using System.Diagnostics.Metrics;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.CodeFix;

/// <summary>
/// The code-fix instruments. Every label is a closed enum or a fixed string - never a repository,
/// an attempt id or a reason sentence.
/// </summary>
public sealed class CodeFixMetrics
{
    private readonly Counter<long> evaluations;
    private readonly Counter<long> attempts;
    private readonly Counter<double> cost;
    private readonly Histogram<double> duration;
    private readonly UpDownCounter<long> jobsActive;
    private readonly UpDownCounter<long> awaitingApproval;

    public CodeFixMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(HephaistoTelemetry.MeterName);

        evaluations = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.CodeFixEvaluations);
        attempts = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.CodeFixAttempts);
        cost = meter.CreateCounter<double>(HephaistoTelemetry.Metrics.CodeFixCostUsd, "USD");
        duration = meter.CreateHistogram<double>(HephaistoTelemetry.Metrics.CodeFixDuration, "s");
        jobsActive = meter.CreateUpDownCounter<long>(HephaistoTelemetry.Metrics.CodeFixJobsActive);
        awaitingApproval = meter.CreateUpDownCounter<long>(HephaistoTelemetry.Metrics.CodeFixAwaitingApproval);
    }

    public void Evaluated(CodeFixVerdict verdict, bool requestedByHuman) =>
        evaluations.Add(1,
            new("result", verdict.Eligible ? "eligible" : verdict.WouldHaveStarted ? "would_have_started" : "declined"),
            new("reason", verdict.PrimaryCode?.ToString() ?? "none"),
            new("source", requestedByHuman ? "human" : "escalation"));

    public void PhaseFinished(CodeFixPhase phase, string outcome, TimeSpan elapsed, decimal costUsd)
    {
        var p = new KeyValuePair<string, object?>("phase", phase == CodeFixPhase.Plan ? "plan" : "implement");

        attempts.Add(1, p, new("outcome", outcome));
        duration.Record(Math.Max(0, elapsed.TotalSeconds), p);
        cost.Add((double)costUsd, p);
    }

    public void JobStarted() => jobsActive.Add(1);

    public void JobEnded() => jobsActive.Add(-1);

    public void AwaitingApproval(int delta) => awaitingApproval.Add(delta);
}
