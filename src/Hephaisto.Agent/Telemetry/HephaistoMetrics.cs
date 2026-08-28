using System.Diagnostics;
using System.Diagnostics.Metrics;
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

    public void IncidentOpened(SignalKind kind) =>
        incidentsOpened.Add(1, new KeyValuePair<string, object?>("kind", kind.ToString()));

    public void IncidentClosed(IncidentState resolution) =>
        incidentsClosed.Add(1, new KeyValuePair<string, object?>("resolution", resolution.ToString()));

    /// <summary>Signal first seen to incident opened. The agent's own MTTD.</summary>
    public void DetectionLatency(TimeSpan latency) => detectionLatency.Record(latency.TotalSeconds);

    public void IncidentDuration(TimeSpan duration) => incidentDuration.Record(duration.TotalSeconds);

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
    public void HumanFeedback(bool helpful, bool falsePositive) =>
        humanFeedback.Add(1,
            new("verdict", helpful ? "helpful" : "unhelpful"),
            new("false_positive", falsePositive ? "true" : "false"));

    public void Dispose() => meter.Dispose();
}
