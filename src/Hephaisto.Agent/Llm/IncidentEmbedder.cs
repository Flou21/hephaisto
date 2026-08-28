using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// Everything the digest is composed from, gathered by the caller. Passed in rather than
/// queried for, so composing a digest stays a pure function over facts.
/// </summary>
public sealed record IncidentDigestInput
{
    public required Incident Incident { get; init; }

    /// <summary>The winning hypothesis. Null when the investigation never concluded.</summary>
    public Finding? PrimaryFinding { get; init; }

    /// <summary>Excerpts already through <c>GroundingVerifier</c>. Ungrounded text must never
    /// reach a digest that outlives the evidence behind it.</summary>
    public IReadOnlyList<string> TopEvidence { get; init; } = [];

    public IReadOnlyList<AgentAction> Actions { get; init; } = [];

    /// <summary>Null when nothing was done, so nothing was verified.</summary>
    public bool? VerificationPassed { get; init; }
}

/// <summary>
/// Composes and embeds the <see cref="IncidentDigest"/> that outlives an incident.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure degrades, it does not block.</b> If the embedding call fails the digest is
/// still saved, with a null embedding, and hybrid search falls back to its lexical arm. That
/// is a deliberate ranking of harms: a provider outage must not be able to stop an incident
/// from resolving, and the lexical arm is the half that finds exact identifiers - image tags,
/// error codes, workload names - which is what an SRE search is usually about anyway.
/// </para>
/// <para>
/// Re-embedding is keyed on <see cref="IncidentDigest.DigestHash"/>, not on time. A digest is
/// recomposed whenever an incident changes, and most of those recompositions produce
/// identical text; embedding it again would be a bill for a vector we already have.
/// </para>
/// </remarks>
public sealed class IncidentEmbedder(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IOptions<LlmOptions> options,
    IClock clock,
    ILogger<IncidentEmbedder> logger)
{
    /// <summary>
    /// Small, process-local, and bounded. Not a cache in the interesting sense - the real
    /// one is the <c>digest_hash</c> column - but it stops a burst of incidents that produce
    /// the same digest from paying twice within one process lifetime.
    /// </summary>
    private const int MaxCachedVectors = 256;

    private readonly ConcurrentDictionary<string, float[]> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds the digest and, if it changed, its embedding.
    /// </summary>
    /// <param name="input">Facts to compose from.</param>
    /// <param name="existing">
    /// The digest already stored for this incident, if any. When its hash matches, the stored
    /// embedding is reused untouched and no provider call is made.
    /// </param>
    public async Task<IncidentDigest> BuildAsync(
        IncidentDigestInput input,
        IncidentDigest? existing,
        CancellationToken ct)
    {
        var text = Compose(input);
        var hash = HashOf(text);

        var digest = existing ?? new IncidentDigest { IncidentId = input.Incident.Id };

        // Read before the update, because the row is updated in place: after the assignments
        // below, `existing.DigestHash` IS the new hash and the "has it changed" test would
        // always say no - so nothing would ever be re-embedded.
        var previousHash = existing?.DigestHash;
        var previousEmbedding = existing?.Embedding;

        digest.IncidentId = input.Incident.Id;
        digest.Digest = text;
        digest.DigestHash = hash;
        digest.Namespace = input.Incident.Target.Namespace;
        digest.WorkloadKey = input.Incident.Target.WorkloadKey;
        digest.Kind = input.Incident.Kind;
        digest.Resolved = input.Incident.State == IncidentState.Resolved;
        digest.CreatedAt = existing?.CreatedAt ?? clock.UtcNow;

        if (string.Equals(previousHash, hash, StringComparison.Ordinal)
            && previousEmbedding is { Length: > 0 })
        {
            return digest;
        }

        digest.Embedding = await EmbedAsync(text, hash, ct).ConfigureAwait(false);

        return digest;
    }

    /// <summary>Returns null on any failure. The caller saves the digest either way.</summary>
    public async Task<float[]?> EmbedAsync(string text, string? hash, CancellationToken ct)
    {
        hash ??= HashOf(text);

        if (_cache.TryGetValue(hash, out var cached))
        {
            return cached;
        }

        try
        {
            var embedding = await generator.GenerateAsync(
                text,
                new EmbeddingGenerationOptions
                {
                    ModelId = options.Value.EmbeddingModel,
                    Dimensions = options.Value.EmbeddingDimensions,
                },
                ct).ConfigureAwait(false);

            var vector = embedding.Vector.ToArray();

            if (vector.Length != options.Value.EmbeddingDimensions)
            {
                // A width mismatch is not a degradation, it is a data corruption: pgvector
                // will reject the insert and take the whole save with it, including the
                // incident resolution. Drop the vector and keep the digest.
                logger.LogError(
                    "Embedding returned {Actual} dimensions, expected {Expected}. Storing the digest "
                    + "without an embedding; search stays lexical for it until Llm:EmbeddingDimensions "
                    + "and the incident_digests.embedding column agree.",
                    vector.Length,
                    options.Value.EmbeddingDimensions);

                return null;
            }

            if (_cache.Count < MaxCachedVectors)
            {
                _cache.TryAdd(hash, vector);
            }

            return vector;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Embedding failed; saving the incident digest with a null embedding. "
                + "Similarity search will miss this incident until it is re-embedded.");

            return null;
        }
    }

    /// <summary>
    /// The digest has to still make sense in a year, when the evidence blobs behind it have
    /// long since expired. So it names things - the workload, the action, the outcome -
    /// rather than pointing at them.
    /// </summary>
    public static string Compose(IncidentDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var incident = input.Incident;
        var sb = new StringBuilder();

        sb.Append("incident: ").Append(incident.Title).Append('\n');
        sb.Append("kind: ").Append(incident.Kind).Append('\n');
        sb.Append("severity: ").Append(incident.Severity).Append('\n');
        sb.Append("workload: ").Append(incident.Target.WorkloadKey).Append('\n');
        sb.Append("namespace: ").Append(incident.Target.Namespace).Append('\n');

        if (input.PrimaryFinding is { } finding)
        {
            sb.Append("cause (")
                .Append(finding.Category)
                .Append(", confidence ")
                .Append(finding.Confidence.ToString("F2"))
                .Append("): ")
                .Append(finding.Hypothesis)
                .Append('\n');
        }
        else
        {
            sb.Append("cause: not determined\n");
        }

        if (input.TopEvidence.Count > 0)
        {
            sb.Append("evidence:\n");

            foreach (var excerpt in input.TopEvidence)
            {
                sb.Append("- ").Append(Flatten(excerpt)).Append('\n');
            }
        }

        if (input.Actions.Count > 0)
        {
            sb.Append("actions:\n");

            foreach (var action in input.Actions)
            {
                sb.Append("- ")
                    .Append(action.Type)
                    .Append(" on ")
                    .Append(action.Target)
                    .Append(" [")
                    .Append(action.State)
                    .Append(", ")
                    .Append(action.Decision)
                    .Append("]\n");
            }
        }
        else
        {
            sb.Append("actions: none\n");
        }

        sb.Append("verification: ")
            .Append(input.VerificationPassed switch
            {
                true => "passed",
                false => "failed",
                null => "not applicable",
            })
            .Append('\n');

        sb.Append("outcome: ").Append(incident.State).Append('\n');

        if (incident.EscalationReason != EscalationReason.None)
        {
            sb.Append("escalation: ").Append(incident.EscalationReason).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(incident.Resolution))
        {
            sb.Append("resolution: ").Append(Flatten(incident.Resolution)).Append('\n');
        }

        return sb.ToString();
    }

    public static string HashOf(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Evidence excerpts are raw log lines and often multi-line. The digest is one field
    /// that gets embedded and full-text indexed; embedded newlines make both worse.
    /// </summary>
    private static string Flatten(string text)
    {
        var single = string.Join(' ', text.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return single.Length <= 400 ? single : string.Concat(single.AsSpan(0, 400), "…");
    }
}
