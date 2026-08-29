using System.Text.Json;
using System.Text.Json.Serialization;
using Hephaisto.Agent.Investigations;

namespace Hephaisto.Eval;

/// <summary>
/// One scenario's recorded tool surface and every answer it gave.
/// </summary>
/// <remarks>
/// <para>
/// A cassette is what makes a prompt change measurable. Running the real investigation loop
/// against a real cluster costs a kind cluster, a seeded fault, ten minutes and a handful of
/// dollars, and gives one noisy sample; running it against a cassette costs one model call and
/// is repeatable, so the difference between two prompts is attributable to the prompts.
/// </para>
/// <para>
/// <b>What is deliberately NOT recorded: the model.</b> The model is the thing under test, so
/// replay serves recorded <i>tool output</i> to a live model. A cassette that also pinned the
/// model's replies would assert only that JSON round-trips.
/// </para>
/// <para>
/// <b>Tool declarations are part of the recording.</b> They carry the exact name, description
/// and JSON schema the model was shown. Two reasons. Replay needs no Kubernetes client and no
/// cluster to rebuild the tool surface. And when the real tool surface changes, a cassette
/// recorded against the old one is visibly stale rather than quietly wrong - which is correct,
/// because changing a tool's description changes the thing being measured.
/// </para>
/// </remarks>
public sealed record Cassette
{
    /// <summary>Stable scenario name, matching the chaos fixture where there is one: <c>c4-imagepull</c>.</summary>
    public required string Id { get; init; }

    /// <summary>What was broken, in one line, for the run report.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// The known-correct root cause, from <c>infra/chaos/README.md</c>. This is the answer the
    /// grader scores a diagnosis against; it is never shown to the model.
    /// </summary>
    public required string ExpectedRootCause { get; init; }

    /// <summary>The tool surface as the model saw it.</summary>
    public required IReadOnlyList<ToolDeclaration> Tools { get; init; }

    /// <summary>Every tool call observed while recording, with its untruncated result.</summary>
    public required IReadOnlyList<RecordedCall> Calls { get; init; }

    /// <summary>Provenance: which investigation this came out of, and when.</summary>
    public CassetteOrigin? Origin { get; init; }

    /// <summary>
    /// The environment card the prompt was composed with.
    /// </summary>
    /// <remarks>
    /// Config-only in the agent and persisted nowhere, yet it is rendered into every system
    /// prompt - cluster name, in-scope namespaces, datasource UIDs. A cassette replayed against
    /// different values is measuring a different prompt, so it travels with the recording.
    /// </remarks>
    public EnvironmentCardOptions? Environment { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Cassette Load(string path) =>
        JsonSerializer.Deserialize<Cassette>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} deserialised to null");

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
}

/// <summary>Where a cassette came from, so a stale one can be traced back and re-recorded.</summary>
public sealed record CassetteOrigin
{
    public Guid? InvestigationId { get; init; }

    public Guid? IncidentId { get; init; }

    public DateTimeOffset RecordedAt { get; init; }

    /// <summary>The agent version that produced it - the version the tool surface belongs to.</summary>
    public string? AgentVersion { get; init; }

    /// <summary>The model that was investigating. Not replayed; recorded so a run can say so.</summary>
    public string? ModelId { get; init; }

    /// <summary>The commit the agent was built from when this was recorded.</summary>
    public string? AgentCommit { get; init; }

    /// <summary>
    /// Hash of the prompt fragments and the runbook used.
    /// </summary>
    /// <remarks>
    /// Prompts and runbooks are files on disk, read fresh on every compose, so a cassette is
    /// silently tied to the commit that produced it. Recording the hash turns "this fixture is
    /// measuring a prompt that no longer exists" from invisible into a warning.
    /// </remarks>
    public string? PromptHash { get; init; }
}

/// <summary>
/// One tool exactly as it was declared to the model.
/// </summary>
/// <remarks>
/// The schema is stored as raw JSON rather than a parsed shape on purpose: it is handed
/// straight back to <see cref="Microsoft.Extensions.AI.AIFunction.JsonSchema"/> on replay, and
/// anything this type understood about it would be a second, drifting definition.
/// </remarks>
public sealed record ToolDeclaration
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>Which server answered: <c>kubernetes</c>, <c>grafana-mcp</c> or <c>internal</c>.</summary>
    public required string Server { get; init; }

    /// <summary>The JSON Schema for the tool's parameters, verbatim.</summary>
    public required JsonElement Schema { get; init; }
}

/// <summary>
/// One recorded call and its answer.
/// </summary>
/// <remarks>
/// <b><see cref="RawResult"/> is the raw tool output, not the digest the model was shown.</b>
/// Replay hands it to the real <c>SafeToolDecorator</c>, which re-derives the digest with the
/// current settings. Recording the digest instead would freeze one run's truncation and cap
/// behaviour into the fixture, and make the digester the one component the harness could never
/// measure a change to.
/// </remarks>
public sealed record RecordedCall
{
    public required string ToolName { get; init; }

    /// <summary>Arguments as JSON, exactly as recorded. Matched after canonicalisation.</summary>
    public required string ArgumentsJson { get; init; }

    /// <summary>Untruncated tool output. Null when the call failed.</summary>
    public string? RawResult { get; init; }

    /// <summary>Set when the recorded call itself failed; replay reproduces the failure.</summary>
    public string? Error { get; init; }

    /// <summary>Ordinal in the recorded investigation, kept for reading a cassette by hand.</summary>
    public int Ordinal { get; init; }
}
