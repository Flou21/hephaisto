using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Watchtower.Core.Digest;
using Watchtower.Core.Telemetry;

namespace Watchtower.Agent.Llm;

/// <summary>Limits applied to every tool, whatever server it came from.</summary>
public sealed class SafeToolOptions
{
    /// <summary>
    /// A tool that has not answered in twenty seconds is not going to change the diagnosis.
    /// Without this the wall-clock budget is enforced only between turns, so one hung
    /// datasource query stalls the whole investigation past its deadline.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// What the model is shown. Matches <see cref="LogDigestOptions.MaxBytes"/> so a log tool
    /// and a metrics tool cost the context window the same.
    /// </summary>
    public int MaxResultBytes { get; set; } = 8 * 1024;

    /// <summary>The untruncated result kept for the audit trail. Beyond this it is clipped.</summary>
    public int MaxRawBytes { get; set; } = 1_000_000;

    /// <summary>
    /// The longest range selector a query may carry. <c>[30d]</c> against a Prometheus with
    /// a fifteen-second scrape is 172,800 samples per series, which is minutes of query time,
    /// gigabytes of memory on the datasource and a result nothing can read.
    /// </summary>
    public TimeSpan MaxQueryRange { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// When true, a metrics or logs query with no time bound at all is refused. An unbounded
    /// LogQL query does not fail - it succeeds, slowly, against every byte Loki has.
    /// </summary>
    public bool RequireTimeRange { get; set; } = true;

    /// <summary>Argument names whose values never reach a log, a span or the database.</summary>
    public List<string> RedactedArgumentNames { get; set; } =
    [
        "token", "secret", "password", "passwd", "apikey", "api_key", "authorization",
        "auth", "credential", "credentials", "bearer", "key", "private_key",
    ];
}

/// <summary>
/// Wraps every tool the model can call - local Kubernetes tools and grafana-mcp tools alike -
/// with the limits that make an autonomous loop safe to leave running.
/// </summary>
/// <remarks>
/// <para>
/// Uniform application is the point. A limit that holds for the tools we wrote and not for
/// the fifty a remote MCP server happens to expose is not a limit; the failure will simply
/// arrive through the other door. <c>McpClientTool</c> derives from <see cref="AIFunction"/>,
/// so one decorator covers both without an adapter layer.
/// </para>
/// <para>
/// Note what this class cannot do: it has no way to mutate anything. It times, caps, redacts,
/// records and refuses. That absence is a security property, not an oversight - every tool
/// reaching the model in phase 1 passes through here, so if this file cannot write to the
/// cluster then neither can the model.
/// </para>
/// </remarks>
public sealed partial class SafeToolDecorator(
    AIFunction innerFunction,
    string server,
    SafeToolOptions options,
    InvestigationBudget? budget = null,
    IInvestigationRecorder? recorder = null) : DelegatingAIFunction(innerFunction)
{
    /// <summary>
    /// Prometheus/Loki/Tempo duration literals inside a range selector or an offset:
    /// <c>[30d]</c>, <c>[1h30m]</c>. Matched on the whole bracketed group so that a bare
    /// <c>5m</c> in a label value is not mistaken for a range.
    /// </summary>
    [GeneratedRegex(@"\[\s*((?:\d+(?:ms|[smhdwy]))+)\s*(?::\s*(?:\d+(?:ms|[smhdwy]))+\s*)?\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex RangeSelector();

    [GeneratedRegex(@"(\d+)(ms|[smhdwy])", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPart();

    /// <summary>
    /// Tools whose whole job is to run a user-supplied query against a time series or a log
    /// store. These are the ones that can be unbounded; a <c>list_datasources</c> cannot.
    /// </summary>
    private static readonly string[] QueryToolFragments =
        ["query_prometheus", "query_loki", "query_tempo", "traceql", "query_range", "logql", "promql"];

    /// <summary>
    /// Any one of these present and non-empty counts as "bounded". Matched
    /// case-insensitively against argument names, so <c>startRfc3339</c> and <c>start_time</c>
    /// both satisfy it.
    /// </summary>
    private static readonly string[] TimeBoundArgumentFragments =
        ["start", "end", "from", "to", "since", "until", "time", "duration", "range", "step", "lookback"];

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var redacted = Redact(arguments);

        using var activity = LlmInstrumentation.Source.StartActivity(
            WatchtowerTelemetry.Spans.ToolPrefix + Name,
            ActivityKind.Internal);

        activity?.SetTag("tool.name", Name);
        activity?.SetTag("tool.server", server);

        // Refusals are returned as text, never thrown. FunctionInvokingChatClient turns an
        // exception into a failed tool result anyway, so throwing would cost the explanation
        // and gain nothing - and the explanation is what lets the model fix the call itself.
        if (Reject(arguments) is { } rejection)
        {
            activity?.SetTag("tool.rejected", true);
            activity?.SetStatus(ActivityStatusCode.Error, rejection);
            RecordRefusal(redacted, rejection);
            return rejection;
        }

        if (budget is not null && !budget.TryConsumeToolCall())
        {
            const string exhausted =
                "REFUSED: the tool-call budget for this investigation is exhausted. "
                + "Conclude now with the evidence you already have.";

            activity?.SetTag("tool.rejected", true);
            RecordRefusal(redacted, exhausted);
            return exhausted;
        }

        var step = recorder?.BeginToolCall(Name, server, redacted);
        var start = Stopwatch.GetTimestamp();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        string raw;
        string? error = null;

        try
        {
            var result = await base.InvokeCoreAsync(arguments, timeout.Token).ConfigureAwait(false);
            raw = Stringify(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            error = $"timed out after {options.Timeout.TotalSeconds:F0}s";
            raw = $"ERROR: {Name} {error}. The datasource may be slow or the query too broad; "
                + "narrow the time range or the label matchers and try once more.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            raw = $"ERROR: {Name} failed: {ex.Message}";
        }

        var durationMs = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var rawBytes = Encoding.UTF8.GetByteCount(raw);

        var digest = LogDigester.Digest(
            raw,
            new LogDigestOptions { MaxBytes = options.MaxResultBytes });

        // The step id is prepended to what the model actually sees, because Evidence.StepId
        // is how a citation names its source and the model can only cite an id it was shown.
        // The same string is stored as ResultDigest, so the grounding check runs against
        // exactly the bytes the model read - header included.
        var shown = step is null
            ? digest.Text
            : $"[step {step.Id}] {Name}\n{digest.Text}";

        activity?.SetTag("tool.result_bytes", rawBytes);
        activity?.SetTag("tool.truncated", digest.Truncated);
        activity?.SetTag("tool.duration_ms", durationMs);

        if (error is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error);
        }

        var tags = new TagList
        {
            { "tool.name", Name },
            { "tool.server", server },
            { "tool.failed", error is not null },
        };

        LlmInstrumentation.ToolCalls.Add(1, tags);
        LlmInstrumentation.ToolDuration.Record(durationMs, tags);

        if (step is not null)
        {
            recorder!.CompleteToolCall(
                step,
                shown,
                Clip(raw, options.MaxRawBytes),
                digest.Truncated,
                rawBytes,
                durationMs,
                error);
        }

        return shown;
    }

    /// <summary>Wraps a whole tool list in one call, so no tool can be forgotten.</summary>
    public static IReadOnlyList<AIFunction> WrapAll(
        IEnumerable<AIFunction> functions,
        string server,
        SafeToolOptions options,
        InvestigationBudget? budget = null,
        IInvestigationRecorder? recorder = null) =>
        [.. functions.Select(f => new SafeToolDecorator(f, server, options, budget, recorder))];

    // ------------------------------------------------------------------
    // Refusals
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the refusal text, or null when the call is acceptable.
    /// </summary>
    /// <remarks>
    /// Refusing an unbounded query is a cost control and a stability control at once. The
    /// dangerous case is not that the query fails - it is that it succeeds: a
    /// <c>[30d]</c> range against Loki reads every byte it has, and the observability stack
    /// this agent depends on to see anything is the thing that falls over.
    /// </remarks>
    public string? Reject(IReadOnlyDictionary<string, object?> arguments)
    {
        var isQueryTool = QueryToolFragments.Any(f => Name.Contains(f, StringComparison.OrdinalIgnoreCase));

        foreach (var (key, value) in arguments)
        {
            if (value is not string text || text.Length == 0)
            {
                continue;
            }

            foreach (Match match in RangeSelector().Matches(text))
            {
                var range = ParseDuration(match.Groups[1].Value);

                if (range > options.MaxQueryRange)
                {
                    return $"REFUSED: argument '{key}' contains the range selector "
                        + $"'{match.Value}', which exceeds the {Describe(options.MaxQueryRange)} limit. "
                        + "Query a narrower window, or use a recording rule if you genuinely need "
                        + "the long view.";
                }
            }
        }

        if (isQueryTool && options.RequireTimeRange && !HasTimeBound(arguments))
        {
            return "REFUSED: this query has no time bound. An unbounded metrics or logs query "
                + "does not fail, it succeeds slowly against everything the datasource holds. "
                + "Supply a start/end pair or a range selector such as [15m] and try again.";
        }

        return null;
    }

    private static bool HasTimeBound(IReadOnlyDictionary<string, object?> arguments)
    {
        foreach (var (key, value) in arguments)
        {
            if (value is null || (value is string s && s.Length == 0))
            {
                continue;
            }

            if (TimeBoundArgumentFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // A range selector inside the query expression itself is a time bound too.
            if (value is string text && RangeSelector().IsMatch(text))
            {
                return true;
            }
        }

        return false;
    }

    public static TimeSpan ParseDuration(string literal)
    {
        var total = TimeSpan.Zero;

        foreach (Match part in DurationPart().Matches(literal))
        {
            var n = double.Parse(part.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            total += part.Groups[2].Value switch
            {
                "ms" => TimeSpan.FromMilliseconds(n),
                "s" => TimeSpan.FromSeconds(n),
                "m" => TimeSpan.FromMinutes(n),
                "h" => TimeSpan.FromHours(n),
                "d" => TimeSpan.FromDays(n),
                "w" => TimeSpan.FromDays(n * 7),
                "y" => TimeSpan.FromDays(n * 365),
                _ => TimeSpan.Zero,
            };
        }

        return total;
    }

    private static string Describe(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{span.TotalDays:F0}d" : $"{span.TotalHours:F0}h";

    // ------------------------------------------------------------------
    // Redaction and serialisation
    // ------------------------------------------------------------------

    /// <summary>
    /// Arguments are persisted and put on spans, so they leave the process. Nothing in this
    /// agent should ever pass a credential as a tool argument - but "should" is not a
    /// control, and a redaction that only runs where we remembered to call it is not one
    /// either. This runs on every tool, every call.
    /// </summary>
    public string Redact(IReadOnlyDictionary<string, object?> arguments)
    {
        var safe = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in arguments)
        {
            safe[key] = options.RedactedArgumentNames.Any(
                n => key.Contains(n, StringComparison.OrdinalIgnoreCase))
                ? "[redacted]"
                : value;
        }

        try
        {
            return JsonSerializer.Serialize(safe, ToolJson.Options);
        }
        catch (NotSupportedException)
        {
            // An argument that will not serialise must not take the investigation with it.
            return $"{{\"_unserialisable\":\"{safe.Count} argument(s)\"}}";
        }
    }

    private void RecordRefusal(string redactedArgs, string rejection)
    {
        var step = recorder?.BeginToolCall(Name, server, redactedArgs);

        if (step is not null)
        {
            recorder!.CompleteToolCall(
                step,
                rejection,
                rejection,
                truncated: false,
                resultBytes: Encoding.UTF8.GetByteCount(rejection),
                durationMs: 0,
                error: rejection);
        }
    }

    private static string Stringify(object? result) => result switch
    {
        null => "(no result)",
        string s => s,
        JsonElement json => json.ValueKind == JsonValueKind.String
            ? json.GetString() ?? string.Empty
            : json.ToString(),
        _ => JsonSerializer.Serialize(result, ToolJson.Options),
    };

    private static string Clip(string text, int maxBytes) =>
        Encoding.UTF8.GetByteCount(text) <= maxBytes ? text : text[..Math.Min(text.Length, maxBytes)];
}

internal static class ToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}
