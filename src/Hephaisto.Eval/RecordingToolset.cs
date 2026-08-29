using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Hephaisto.Agent.Llm;

namespace Hephaisto.Eval;

/// <summary>
/// Wraps a live tool surface and records everything needed to replay it later.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of <see cref="ReplayToolset"/>, and it plugs into the same seam: the wrapped
/// functions are handed to <c>InvestigationRunner</c> as its tools, so recording happens during
/// an ordinary investigation rather than as a separate pass.
/// </para>
/// <para>
/// <b>Position is the whole design.</b> These wrappers sit <i>inside</i> the real
/// <c>SafeToolDecorator</c>, which means they see the arguments the model actually sent - before
/// redaction - and return the tool's output before digestion and truncation. Both matter:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Redaction matches the substring <c>key</c>, so an argument named <c>labelKey</c> is stored as
/// <c>[redacted]</c> in the database. A cassette built from those values could never match what a
/// live model sends, so every such call would replay as a miss.
/// </description></item>
/// <item><description>
/// The digest is capped at 8 KB and prefixed with a step header. Recording it would freeze one
/// run's truncation into the fixture and re-prefix the header on replay.
/// </description></item>
/// </list>
/// <para>
/// Recording from the database instead was the original plan and does not work: tool
/// declarations are not persisted at all, and only 43 of 297 recorded tool calls carry an
/// untruncated blob.
/// </para>
/// </remarks>
public sealed class RecordingToolset
{
    private readonly ConcurrentDictionary<string, ToolDeclaration> declarations = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedCall> calls = new();
    private int ordinal;

    /// <summary>
    /// Wraps every tool in <paramref name="tools"/>, registering its declaration and recording
    /// each call. <paramref name="server"/> matches the value the agent uses:
    /// <c>kubernetes</c>, <c>grafana-mcp</c> or <c>internal</c>.
    /// </summary>
    public IReadOnlyList<AIFunction> Wrap(IEnumerable<AIFunction> tools, string server)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var wrapped = new List<AIFunction>();

        foreach (var tool in tools)
        {
            declarations[tool.Name] = new ToolDeclaration
            {
                Name = tool.Name,
                Description = tool.Description,
                Server = server,
                Schema = tool.JsonSchema,
            };

            wrapped.Add(new RecordingFunction(tool, this));
        }

        return wrapped;
    }

    /// <summary>Declarations captured so far, ordered by name for a stable cassette on disk.</summary>
    public IReadOnlyList<ToolDeclaration> Declarations =>
        [.. declarations.Values.OrderBy(d => d.Name, StringComparer.Ordinal)];

    /// <summary>Calls captured so far, in the order they were made.</summary>
    public IReadOnlyList<RecordedCall> Calls => [.. calls];

    /// <summary>
    /// Arguments that were redacted before reaching this recorder, which cannot happen from the
    /// live path and would indicate the cassette was built from persisted rows instead.
    /// A non-empty result means the cassette will replay as misses.
    /// </summary>
    public IReadOnlyList<string> RedactedArguments =>
        [.. calls.Where(c => c.ArgumentsJson.Contains("[redacted]", StringComparison.Ordinal))
            .Select(c => c.ToolName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    internal void Record(string toolName, string argumentsJson, string? rawResult, string? error)
    {
        calls.Enqueue(new RecordedCall
        {
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            RawResult = rawResult,
            Error = error,
            Ordinal = Interlocked.Increment(ref ordinal),
        });
    }
}

/// <summary>
/// One live tool, recorded on the way through.
/// </summary>
/// <remarks>
/// A <see cref="DelegatingAIFunction"/> so the name, description and schema the model sees are
/// the inner tool's, untouched - the recorder must not alter the surface it is capturing.
/// </remarks>
internal sealed class RecordingFunction(AIFunction inner, RecordingToolset toolset)
    : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // Serialised exactly as ReplayToolset serialises a call it is asked to resolve, so a
        // recorded key and a replayed key are produced by the same code path. If these two ever
        // diverge every replay silently becomes a miss.
        var json = JsonSerializer.Serialize(
            arguments as IDictionary<string, object?> ?? new Dictionary<string, object?>(),
            Cassette.Json);

        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);

            toolset.Record(Name, json, Stringify(result), error: null);

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recorded, then rethrown: the real decorator above still has to see the failure and
            // handle it exactly as it would in production. A cassette that dropped failures would
            // replay a cluster in which nothing ever goes wrong.
            toolset.Record(Name, json, rawResult: null, error: ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Mirrors <c>SafeToolDecorator.Stringify</c> exactly, and must keep mirroring it.
    /// </summary>
    /// <remarks>
    /// On replay this string is handed back to the real decorator, which stringifies it again -
    /// a string passes through untouched. So whatever is recorded here is precisely what the
    /// decorator will digest on replay, and the two rules agreeing is what makes a replayed run
    /// byte-identical to the recorded one. They disagreed on the first attempt: a tool returning
    /// a <see cref="JsonElement"/> was serialised with its JSON quotes, which the round-trip test
    /// caught.
    /// </remarks>
    private static string Stringify(object? result) => result switch
    {
        null => "(no result)",
        string text => text,
        JsonElement json => json.ValueKind == JsonValueKind.String
            ? json.GetString() ?? string.Empty
            : json.ToString(),
        _ => JsonSerializer.Serialize(result, Cassette.Json),
    };
}

/// <summary>
/// Records the Grafana half of the tool surface.
/// </summary>
/// <remarks>
/// <c>InvestigationRunner</c> fetches Grafana tools itself rather than receiving them, so this is
/// the only place they can be wrapped from outside. Without it a cassette would cover Kubernetes
/// tools only - and the Loki label discovery that spends the step budget is precisely what the
/// accuracy experiments need to see.
/// </remarks>
public sealed class RecordingGrafanaToolProvider(IGrafanaToolProvider inner, RecordingToolset toolset)
    : IGrafanaToolProvider
{
    public async Task<IReadOnlyList<AIFunction>> GetToolsAsync(CancellationToken ct)
    {
        var tools = await inner.GetToolsAsync(ct).ConfigureAwait(false);

        return [.. toolset.Wrap(tools, "grafana-mcp")];
    }
}
