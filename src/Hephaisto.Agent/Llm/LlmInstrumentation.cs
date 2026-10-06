using System.Diagnostics;
using System.Diagnostics.Metrics;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// The <see cref="ActivitySource"/> and <see cref="Meter"/> instances for this layer, under
/// the names <see cref="HephaistoTelemetry"/> declares.
/// </summary>
/// <remarks>
/// <para>
/// <c>Hephaisto.Core</c> holds only the names, because Core must stay free of side effects -
/// constructing a Meter registers it with the global registry, which is a side effect. The
/// instances therefore live here, in the assembly that actually emits.
/// </para>
/// <para>
/// Several sources with the same name are fine and are what OpenTelemetry expects: the
/// exporter subscribes by name, so another stream constructing its own
/// <c>new ActivitySource("Hephaisto")</c> produces spans on the same source rather than a
/// second one. That is why this type is internal - it is this layer's handle, not a
/// singleton anybody else has to find.
/// </para>
/// </remarks>
internal static class LlmInstrumentation
{
    public static readonly ActivitySource Source = new(HephaistoTelemetry.ActivitySourceName);

    public static readonly Meter Meter = new(HephaistoTelemetry.MeterName);

    public static readonly Counter<long> Tokens =
        Meter.CreateCounter<long>(HephaistoTelemetry.Metrics.LlmTokens, "token");

    public static readonly Counter<double> CostUsd =
        Meter.CreateCounter<double>(HephaistoTelemetry.Metrics.LlmCostUsd, "usd");

    public static readonly Counter<long> ToolCalls =
        Meter.CreateCounter<long>(HephaistoTelemetry.Metrics.ToolCalls);

    public static readonly Histogram<double> ToolDuration =
        Meter.CreateHistogram<double>(HephaistoTelemetry.Metrics.ToolDuration, "ms");

    public static readonly Counter<long> InvestigationJobFallbacks =
        Meter.CreateCounter<long>(HephaistoTelemetry.Metrics.InvestigationJobFallbacks);

    // An investigation's steps, duration, termination and grounding rejections are NOT here.
    // They were, beside the registrations in HephaistoMetrics under the same names (backlog
    // #15): a counter beside a histogram, milliseconds beside seconds, and two counters that
    // each counted the same event. One name has one instrument, and those four are recorded
    // where an investigation's outcome is recorded - InvestigationCoordinator.
}
