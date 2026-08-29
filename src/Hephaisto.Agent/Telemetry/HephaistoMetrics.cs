using System.Diagnostics;
using System.Diagnostics.Metrics;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent;

/// <summary>
/// The concrete instruments behind the names in <see cref="HephaistoTelemetry"/>.
/// </summary>
/// <remarks>
/// The names live in Core so a dashboard panel, an alert rule and the emitting code cannot
/// drift apart; the instruments live here so Core stays free of side effects. Registered as a
/// singleton - a Meter created per request leaks instruments and produces duplicate series.
/// </remarks>
public sealed class HephaistoMetrics : IDisposable
{
    public static readonly ActivitySource ActivitySource = new(HephaistoTelemetry.ActivitySourceName);

    private readonly Meter meter;

    private readonly Counter<long> signalsReceived;
    private readonly Counter<long> signalsDropped;
    private readonly Counter<long> incidentsOpened;
    private readonly Counter<long> incidentsClosed;
    private readonly UpDownCounter<long> incidentsOpen;
    private readonly Histogram<double> detectionLatency;
    private readonly Histogram<double> incidentDuration;
    private readonly Histogram<double> investigationDuration;
    private readonly Histogram<int> investigationSteps;
    private readonly Counter<long> investigationTerminations;
    private readonly Counter<long> policyDecisions;
    private readonly Counter<long> actionsExecuted;
    private readonly Counter<long> actionsRolledBack;
    private readonly Counter<long> verificationResults;
    private readonly Counter<long> groundingRejected;
    private readonly Counter<long> humanFeedback;

    public HephaistoMetrics(IMeterFactory meterFactory)
    {
        meter = meterFactory.Create(HephaistoTelemetry.MeterName);

        signalsReceived   = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.SignalsReceived);
        signalsDropped    = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.SignalsDropped);
        incidentsOpened   = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.IncidentsOpened);
        incidentsClosed   = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.IncidentsClosed);

        // UpDownCounter, per the dashboard's metric-spec table. A polled gauge over the
        // database would survive a restart, which this does not - but it would also hide the
        // failure this instrument exists to expose: a close path that forgets to decrement.
        // A count that drifts upward forever is visible; a re-read count silently self-heals.
        incidentsOpen     = meter.CreateUpDownCounter<long>(
            HephaistoTelemetry.Metrics.IncidentsOpen,
            unit: "{incident}",
            description:
                "Incidents currently in an open state. Decremented on the transitions that "
                + "leave HephaistoDbContext.OpenStates - which does NOT include Escalated, so "
                + "this tracks /api/status.openIncidents rather than hephaisto.incidents.closed.");

        // Seconds, not milliseconds: MTTD and MTTR are read by humans on a dashboard, and the
        // OTel convention for duration is seconds.
        detectionLatency     = meter.CreateHistogram<double>(HephaistoTelemetry.Metrics.DetectionLatency, "s");
        incidentDuration     = meter.CreateHistogram<double>(HephaistoTelemetry.Metrics.IncidentDuration, "s");
        investigationDuration = meter.CreateHistogram<double>(HephaistoTelemetry.Metrics.InvestigationDuration, "s");
        investigationSteps   = meter.CreateHistogram<int>(HephaistoTelemetry.Metrics.InvestigationSteps);

        investigationTerminations = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.InvestigationTerminations);
        policyDecisions    = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.PolicyDecisions);
        actionsExecuted    = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.ActionsExecuted);
        actionsRolledBack  = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.ActionsRolledBack);
        verificationResults = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.VerificationResult);
        groundingRejected  = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.GroundingRejected);
        humanFeedback      = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.HumanFeedback);
    }

    public void SignalReceived(SignalSource source, SignalKind kind) =>
        signalsReceived.Add(1, new("source", source.ToString()), new("kind", kind.ToString()));

    public void SignalDropped(SignalSource source, string reason) =>
        signalsDropped.Add(1, new("source", source.ToString()), new("reason", reason));

    /// <summary>
    /// Every incident that is persisted, including one opened only to be suppressed
    /// immediately.
    /// </summary>
    /// <remarks>
    /// The flapping path opens an incident and suppresses it in the same breath, and used to
    /// skip this call. That made the count read as "incidents worth investigating" rather than
    /// "incidents opened", and - now that the same call moves an UpDownCounter - would have
    /// decremented on a suppression that never incremented, driving the gauge negative.
    /// </remarks>
    public void IncidentOpened(SignalKind kind, Severity severity)
    {
        incidentsOpened.Add(1,
            new("kind", kind.ToString()),
            new("severity", severity.ToString()));

        incidentsOpen.Add(1, new KeyValuePair<string, object?>("kind", kind.ToString()));
    }

    /// <summary>Signal first seen to incident opened. The agent's own MTTD.</summary>
    public void DetectionLatency(TimeSpan latency) => detectionLatency.Record(latency.TotalSeconds);

    /// <summary>
    /// An incident reached an outcome: the closed counter and the MTTR histogram, together,
    /// because recording one without the other is how MTTR came to be undrawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Escalated counts as an outcome but not as a close.</b> In Observe mode nothing is
    /// fixed, so almost every incident ends <c>Suppressed</c> or <c>Escalated</c>; scoring MTTR
    /// only on <c>Resolved</c> would measure a transition that
    /// <see href="../../docs/backlog.md">backlog #11</see> says no production path reaches, and
    /// the histogram would stay empty exactly as it is today.
    /// </para>
    /// <para>
    /// But <c>Escalated</c> is still an <i>open</i> state - both <see cref="Incident.IsOpen"/>
    /// and <see cref="HephaistoDbContext.OpenStates"/> say so - because a human still has it.
    /// So the gauge is decremented from the same authority rather than from this method's
    /// argument, and <c>opened - closed</c> deliberately does not equal <c>open</c>.
    /// </para>
    /// </remarks>
    public void IncidentClosed(SignalKind kind, Severity severity, IncidentState outcome, TimeSpan duration)
    {
        incidentsClosed.Add(1,
            new("kind", kind.ToString()),
            new("severity", severity.ToString()),
            new("outcome", outcome.ToString()));

        incidentDuration.Record(duration.TotalSeconds,
            new("kind", kind.ToString()),
            new("outcome", outcome.ToString()));

        if (!HephaistoDbContext.OpenStates.Contains(outcome))
        {
            incidentsOpen.Add(-1, new KeyValuePair<string, object?>("kind", kind.ToString()));
        }
    }

    public void InvestigationCompleted(TimeSpan duration, int steps, TerminationReason reason)
    {
        investigationDuration.Record(duration.TotalSeconds);
        investigationSteps.Record(steps);
        investigationTerminations.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));
    }

    public void PolicyDecision(PolicyDecision decision, ActionType type, string reason) =>
        policyDecisions.Add(1,
            new("decision", decision.ToString()),
            new("action_type", type.ToString()),
            new("reason", reason));

    public void ActionExecuted(ActionType type, AgentMode mode, string outcome) =>
        actionsExecuted.Add(1,
            new("type", type.ToString()),
            new("mode", mode.ToString()),
            new("outcome", outcome));

    public void ActionRolledBack(ActionType type) =>
        actionsRolledBack.Add(1, new KeyValuePair<string, object?>("type", type.ToString()));

    public void VerificationResult(VerificationOutcome outcome, int attempt) =>
        verificationResults.Add(1,
            new("result", outcome.ToString()),
            new("attempt", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>A rising rate here is the earliest available signal of prompt drift.</summary>
    public void GroundingRejected(string reason) =>
        groundingRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>The only honest false-positive rate the system will ever have.</summary>
    /// <remarks>
    /// <para>
    /// The <c>verdict</c> values are <c>correct</c> / <c>incorrect</c> / <c>partial</c> /
    /// <c>unclear</c>, which is the vocabulary the dashboard's metric-spec table declares and
    /// the "Feedback precision" panel divides by. This method previously emitted
    /// <c>helpful</c> / <c>unhelpful</c>, so had it ever been called, the precision panel's
    /// denominator - <c>verdict=~"correct|incorrect|partial"</c> - would have matched nothing
    /// and drawn a division by zero. Recording an unreadable metric is not fixing the metric.
    /// </para>
    /// <para>
    /// <c>unclear</c> is the unjudged case and is deliberately outside that denominator: a
    /// reviewer who rated usefulness without ruling on the root cause has not supplied
    /// evidence either way, and folding them in would quietly deflate precision.
    /// </para>
    /// </remarks>
    public void HumanFeedback(Core.Domain.HumanFeedback feedback, SignalKind kind)
    {
        ArgumentNullException.ThrowIfNull(feedback);

        var verdict = feedback.RootCauseCorrect switch
        {
            true => "correct",

            // Useful, but it named the wrong cause. The domain keeps these separate on
            // purpose - "a useful investigation can still name the wrong cause".
            false when feedback.Helpful => "partial",
            false => "incorrect",
            null => "unclear",
        };

        humanFeedback.Add(1,
            new("verdict", verdict),
            new("kind", kind.ToString()),
            new("false_positive", feedback.FalsePositive ? "true" : "false"));
    }

    public void Dispose() => meter.Dispose();
}
