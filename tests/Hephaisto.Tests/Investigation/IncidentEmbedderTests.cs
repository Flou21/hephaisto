using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Llm;
using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Investigations;

public class IncidentEmbedderTests
{
    private static Incident ResolvedIncident() => new()
    {
        Title = "hephaisto-chaos/api OOMKilled every ~40 minutes",
        Kind = SignalKind.OomKilled,
        Severity = Severity.Warning,
        State = IncidentState.Resolved,
        Resolution = "Memory limit raised from 64Mi to 192Mi.",
        Target = new TargetRef
        {
            Namespace = "hephaisto-chaos",
            Kind = "Pod",
            Name = "api-7d9f8-xk2p1",
            OwnerKind = "Deployment",
            OwnerName = "api",
        },
    };

    private static IncidentDigestInput InputFor(Incident incident) => new()
    {
        Incident = incident,
        PrimaryFinding = new Finding
        {
            Category = "resource-limit",
            Hypothesis = "Working set climbs linearly to the 64Mi limit and the kernel kills it.",
            Confidence = 0.92,
            IsPrimary = true,
        },
        TopEvidence = ["reason: OOMKilled\n    exitCode: 137"],
        Actions =
        [
            new AgentAction
            {
                Type = ActionType.PatchResources,
                Target = incident.Target,
                State = ActionState.Verified,
                Decision = PolicyDecision.Allow,
            },
        ],
        VerificationPassed = true,
    };

    private static IncidentEmbedder Embedder(IEmbeddingGenerator<string, Embedding<float>> generator) =>
        new(
            generator,
            Options.Create(new LlmOptions()),
            new TestClock(),
            NullLogger<IncidentEmbedder>.Instance);

    [Fact]
    public void The_digest_stands_on_its_own_without_the_evidence_behind_it()
    {
        // Blobs expire at 30 days; this is kept indefinitely. It has to still make sense in a
        // year, so it names things rather than pointing at them.
        var digest = IncidentEmbedder.Compose(InputFor(ResolvedIncident()));

        digest.Should().Contain("OomKilled");
        digest.Should().Contain("hephaisto-chaos/Deployment/api");
        digest.Should().Contain("resource-limit");
        digest.Should().Contain("Working set climbs");
        digest.Should().Contain("PatchResources");
        digest.Should().Contain("verification: passed");
        digest.Should().Contain("Memory limit raised");
    }

    [Fact]
    public void Multi_line_evidence_is_flattened()
    {
        // The digest is one field that gets embedded and full-text indexed; embedded newlines
        // make both worse.
        var digest = IncidentEmbedder.Compose(InputFor(ResolvedIncident()));

        digest.Should().Contain("reason: OOMKilled exitCode: 137");
    }

    [Fact]
    public void An_undetermined_cause_says_so_rather_than_being_omitted()
    {
        var input = InputFor(ResolvedIncident()) with { PrimaryFinding = null, Actions = [] };

        var digest = IncidentEmbedder.Compose(input);

        digest.Should().Contain("cause: not determined");
        digest.Should().Contain("actions: none");
    }

    [Fact]
    public async Task A_failing_embedding_saves_the_digest_with_a_null_embedding()
    {
        // A provider outage must not be able to stop an incident from resolving. Search falls
        // back to its lexical arm, which is the half that finds exact identifiers anyway.
        var digest = await Embedder(new ThrowingGenerator())
            .BuildAsync(InputFor(ResolvedIncident()), existing: null, CancellationToken.None);

        digest.Embedding.Should().BeNull();
        digest.Digest.Should().NotBeNullOrWhiteSpace();
        digest.DigestHash.Should().NotBeNullOrWhiteSpace();
        digest.Kind.Should().Be(SignalKind.OomKilled);
        digest.Resolved.Should().BeTrue();
    }

    [Fact]
    public async Task A_wrong_width_vector_is_dropped_rather_than_stored()
    {
        // pgvector rejects a width mismatch and takes the whole save with it, including the
        // incident resolution. Better to lose the vector than the incident.
        var digest = await Embedder(new FixedGenerator(dimensions: 1536))
            .BuildAsync(InputFor(ResolvedIncident()), existing: null, CancellationToken.None);

        digest.Embedding.Should().BeNull();
    }

    [Fact]
    public async Task An_unchanged_digest_is_not_re_embedded()
    {
        var generator = new FixedGenerator(dimensions: 768);
        var embedder = Embedder(generator);
        var input = InputFor(ResolvedIncident());

        var first = await embedder.BuildAsync(input, existing: null, CancellationToken.None);
        var second = await embedder.BuildAsync(input, existing: first, CancellationToken.None);

        second.DigestHash.Should().Be(first.DigestHash);
        second.Embedding.Should().NotBeNull();

        // Re-embedding text we already have a vector for is a bill for nothing.
        generator.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_changed_digest_is_re_embedded()
    {
        var generator = new FixedGenerator(dimensions: 768);
        var embedder = Embedder(generator);

        var incident = ResolvedIncident();
        var first = await embedder.BuildAsync(InputFor(incident), existing: null, CancellationToken.None);

        // Captured before the second call: BuildAsync updates the digest it was handed in
        // place, so `first` and `second` are the same object.
        var firstHash = first.DigestHash;

        incident.Resolution = "Actually the sidecar was leaking.";
        var second = await embedder.BuildAsync(InputFor(incident), existing: first, CancellationToken.None);

        second.DigestHash.Should().NotBe(firstHash);
        generator.Calls.Should().Be(2);
    }

    [Fact]
    public void The_hash_is_stable_for_identical_text() =>
        IncidentEmbedder.HashOf("same text").Should().Be(IncidentEmbedder.HashOf("same text"));

    private sealed class ThrowingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("the embedding provider is down");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FixedGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Calls { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            var embeddings = values
                .Select(_ => new Embedding<float>(new float[dimensions]))
                .ToList();

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
