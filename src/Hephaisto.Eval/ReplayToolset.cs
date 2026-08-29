using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Hephaisto.Eval;

/// <summary>How a replayed call was satisfied. Every call lands in exactly one bucket.</summary>
public enum ReplayHit
{
    /// <summary>Same tool, same arguments. The only outcome that replays what was recorded.</summary>
    Exact = 0,

    /// <summary>
    /// Same tool, different arguments, and the recording holds exactly one call to it. Served,
    /// and counted separately - it is a real answer to a question that was not quite asked.
    /// </summary>
    Fuzzy = 1,

    /// <summary>Nothing recorded. The model is told so, plainly, and the run reports it.</summary>
    Miss = 2,
}

/// <summary>One replayed call, for the run report.</summary>
public sealed record ReplayEvent(string ToolName, ReplayHit Hit, string ArgumentsJson);

/// <summary>
/// Rebuilds a recorded tool surface and answers from the recording instead of the cluster.
/// </summary>
/// <remarks>
/// <para>
/// Handed to <c>InvestigationRunner</c> as its <c>IEnumerable&lt;AIFunction&gt;</c>, so the
/// runner wraps these in the real <c>SafeToolDecorator</c> exactly as it wraps the real tools.
/// Timeouts, caps, redaction, digestion and step recording are all the production code paths;
/// the only substitution is where the bytes come from.
/// </para>
/// <para>
/// <b>A miss is reported, never disguised.</b> Once a prompt changes, the model will ask
/// questions the recording never answered - that is not a bug, it is the thing to measure. A
/// miss returns text saying plainly that nothing was recorded, and is counted; a scenario whose
/// miss rate is high needs re-recording, and <see cref="Summarise"/> is what says so. Silently
/// returning an empty result would read to the model as "the cluster has none of those", which
/// is a different and false claim.
/// </para>
/// </remarks>
public sealed class ReplayToolset
{
    private readonly Dictionary<string, RecordedCall> byExactKey;
    private readonly Dictionary<string, List<RecordedCall>> byToolName;
    private readonly Dictionary<string, IReadOnlyList<AIFunction>> byServer;
    private readonly ConcurrentQueue<ReplayEvent> events = new();

    public ReplayToolset(Cassette cassette)
    {
        ArgumentNullException.ThrowIfNull(cassette);

        Cassette = cassette;

        byToolName = cassette.Calls
            .GroupBy(c => c.ToolName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Last write wins: a tool called twice with identical arguments answered twice, and the
        // later answer is the one closer to the state the investigation ended in.
        byExactKey = [];

        foreach (var call in cassette.Calls)
        {
            byExactKey[Key(call.ToolName, call.ArgumentsJson)] = call;
        }

        Functions = [.. cassette.Tools.Select(AIFunction (d) => new ReplayFunction(d, this))];

        byServer = cassette.Tools
            .Zip(Functions, (declaration, function) => (declaration.Server, function))
            .GroupBy(pair => pair.Server, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<AIFunction> (g) => [.. g.Select(pair => pair.function)],
                StringComparer.Ordinal);
    }

    public Cassette Cassette { get; }

    /// <summary>The rebuilt tool surface, in the order it was declared.</summary>
    public IReadOnlyList<AIFunction> Functions { get; }

    /// <summary>
    /// Which servers this cassette holds tools from, so a caller can check the split is total.
    /// </summary>
    /// <remarks>
    /// The runner takes its two halves through different seams - Kubernetes tools are injected,
    /// Grafana tools are fetched - so replay has to route each recorded tool back to the seam it
    /// came from. A declaration whose server matches neither would silently vanish from the
    /// surface, and a run that never offered a tool the recording used would report a miss rate
    /// caused by the harness rather than by the change under test.
    /// </remarks>
    public IReadOnlyList<string> Servers => [.. byServer.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The rebuilt tools that were declared by one server.</summary>
    public IReadOnlyList<AIFunction> FunctionsFor(string server) =>
        byServer.TryGetValue(server, out var functions) ? functions : [];

    /// <summary>Every call this toolset served, in order.</summary>
    public IReadOnlyList<ReplayEvent> Events => [.. events];

    public ReplaySummary Summarise()
    {
        var all = Events;

        return new ReplaySummary
        {
            Total = all.Count,
            Exact = all.Count(e => e.Hit == ReplayHit.Exact),
            Fuzzy = all.Count(e => e.Hit == ReplayHit.Fuzzy),
            Missed = all.Count(e => e.Hit == ReplayHit.Miss),
            MissedTools =
            [
                .. all.Where(e => e.Hit == ReplayHit.Miss)
                    .Select(e => e.ToolName)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
            ],
        };
    }

    /// <summary>
    /// Canonical form of a call, so argument order and whitespace cannot decide a match. Object
    /// keys are sorted recursively and nulls dropped - a tool called with <c>{"ns":"x"}</c> and
    /// with <c>{"ns":"x","selector":null}</c> asked the same question.
    /// </summary>
    internal static string Key(string toolName, string? argumentsJson) =>
        toolName + " " + Canonicalise(argumentsJson);

    internal static string Canonicalise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            using var buffer = new MemoryStream();

            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteCanonical(doc.RootElement, writer);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            // Not JSON. Compare it verbatim rather than throwing: an unparseable argument string
            // is still a stable key, and a cassette is allowed to be odd.
            return json;
        }
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in element.EnumerateObject()
                    .Where(p => p.Value.ValueKind is not JsonValueKind.Null)
                    .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                // Array order is meaningful - it is the caller's list, not a set.
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    internal string Resolve(string toolName, string argumentsJson)
    {
        if (byExactKey.TryGetValue(Key(toolName, argumentsJson), out var exact))
        {
            events.Enqueue(new ReplayEvent(toolName, ReplayHit.Exact, argumentsJson));
            return Render(exact);
        }

        // One recorded call to this tool is unambiguous: whatever the model asked, the recording
        // has exactly one answer from that tool, and serving it is more useful than refusing.
        // More than one and there is a real choice to make, which this cannot make.
        if (byToolName.TryGetValue(toolName, out var candidates) && candidates.Count == 1)
        {
            events.Enqueue(new ReplayEvent(toolName, ReplayHit.Fuzzy, argumentsJson));
            return Render(candidates[0]);
        }

        events.Enqueue(new ReplayEvent(toolName, ReplayHit.Miss, argumentsJson));

        return $"No output for this call was recorded in cassette '{Cassette.Id}'. "
            + "This scenario was captured from a different sequence of tool calls; treat it as "
            + "unknown rather than as an empty result.";
    }

    private static string Render(RecordedCall call) =>
        call.Error is { Length: > 0 } error
            ? $"tool error (recorded): {error}"
            : call.RawResult ?? string.Empty;
}

/// <summary>Replay accounting for one run. A high <see cref="MissRate"/> invalidates the run.</summary>
public sealed record ReplaySummary
{
    public required int Total { get; init; }

    public required int Exact { get; init; }

    public required int Fuzzy { get; init; }

    public required int Missed { get; init; }

    public required IReadOnlyList<string> MissedTools { get; init; }

    public double MissRate => Total == 0 ? 0 : (double)Missed / Total;

    public override string ToString() =>
        $"{Total} calls: {Exact} exact, {Fuzzy} fuzzy, {Missed} missed ({MissRate:P0})";
}

/// <summary>
/// One recorded tool, rebuilt. Carries the recorded name, description and schema verbatim, so
/// the model sees precisely the surface the recording was made against.
/// </summary>
internal sealed class ReplayFunction(ToolDeclaration declaration, ReplayToolset toolset) : AIFunction
{
    public override string Name => declaration.Name;

    public override string Description => declaration.Description;

    public override JsonElement JsonSchema => declaration.Schema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var json = JsonSerializer.Serialize(
            arguments as IDictionary<string, object?> ?? new Dictionary<string, object?>(),
            Cassette.Json);

        return ValueTask.FromResult<object?>(toolset.Resolve(declaration.Name, json));
    }
}

/// <summary>
/// Serves the Grafana half of a cassette back through the seam the runner fetches it from.
/// </summary>
/// <remarks>
/// The mirror of <see cref="RecordingGrafanaToolProvider"/>. It exists for the same reason: the
/// runner asks for Grafana tools rather than being handed them, so this is the only place a
/// replayed Loki or Prometheus tool can be substituted. Without it a replayed run would be
/// offered the Kubernetes tools only, and every recorded Loki call would come back a miss -
/// which is exactly the half of the surface the accuracy experiments are about.
/// </remarks>
public sealed class ReplayGrafanaToolProvider(ReplayToolset toolset) : Agent.Llm.IGrafanaToolProvider
{
    public Task<IReadOnlyList<AIFunction>> GetToolsAsync(CancellationToken ct) =>
        Task.FromResult(toolset.FunctionsFor(ToolDeclaration.GrafanaMcp));
}
