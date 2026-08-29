using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// Hybrid retrieval, against a real Postgres with real extensions.
/// </summary>
/// <remarks>
/// <para>
/// This suite exists because of a bug that no offline test could have caught and that survived
/// for the whole life of the feature: <c>IncidentQueries</c> passed a null embedding, so the
/// <b>vector arm had never run once</b> - while the corpus was fully embedded and an HNSW index
/// was being maintained for it. The expensive half was paid for and the cheap half was missing.
/// </para>
/// <para>
/// The second half of the bug is the one these tests pin hardest.
/// <see cref="Crash_finds_CrashLoopBackOff_which_the_lexical_arm_alone_cannot"/> is the
/// reproduction: <c>to_tsvector('english', 'CrashLoopBackOff')</c> is the single lexeme
/// <c>crashloopbackoff</c>, so the query an SRE actually types returns <b>nothing</b> from the
/// full-text arm - not fewer results, nothing. Measured against the dev cluster's 32 digests
/// before this change: zero hits.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class IncidentSearchTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private static IncidentSearch Search(HephaistoDbContext db) =>
        new(db, new StaticOptionsMonitor<PersistenceOptions>(new PersistenceOptions()),
            NullLogger<IncidentSearch>.Instance);

    /// <summary>
    /// A digest whose text reads like a real one, because the failure is about tokenisation.
    /// </summary>
    /// <remarks>
    /// A fixture that said "the pod is crashing" would pass against the broken code, since
    /// <c>crashing</c> stems to <c>crash</c>. The Kubernetes reason strings are camelCase single
    /// tokens, and that is the entire problem.
    /// </remarks>
    private int _seeded;

    private async Task<Guid> SeedAsync(
        string digest,
        SignalKind kind,
        string workload,
        bool resolved = true,
        float[]? embedding = null)
    {
        // Each digest gets its own timestamp. The final ORDER BY breaks ties on created_at, so
        // a corpus written at one instant orders equally-scored hits arbitrarily - which makes
        // any ranking assertion flaky in a way that looks like a ranking bug.
        var at = Now.AddMinutes(_seeded++);

        var incident = new Incident
        {
            Title = digest[..Math.Min(60, digest.Length)],
            Kind = kind,
            Severity = Severity.Critical,
            OpenedAt = at,
            LastSignalAt = at,
            State = resolved ? IncidentState.Resolved : IncidentState.Escalated,
            Target = new TargetRef
            {
                Namespace = "hephaisto-chaos",
                Kind = "Pod",
                Name = workload + "-abc123",
                OwnerKind = "Deployment",
                OwnerName = workload,
            },
        };

        await using var db = pg.CreateContext();

        db.Incidents.Add(incident);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = new IncidentDigest
        {
            IncidentId = incident.Id,
            Digest = digest,
            DigestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(digest))),
            Namespace = "hephaisto-chaos",
            WorkloadKey = incident.Target.WorkloadKey,
            Kind = kind,
            Resolved = resolved,
            CreatedAt = at,
            Embedding = embedding,
        };

        db.IncidentDigests.Add(row);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return row.Id;
    }

    private async Task SeedCorpusAsync()
    {
        await pg.ResetAsync();
        _seeded = 0;

        await SeedAsync(
            "incident: CrashLoopBackOff on c2-crashloop (hephaisto-chaos). The container exits 1 at "
            + "startup after failing to reach mongo.infra-db:27017.",
            SignalKind.CrashLoopBackOff,
            "c2-crashloop");

        await SeedAsync(
            "incident: OomKilled on c1-oomkill (hephaisto-chaos). The container allocates about "
            + "200Mi against a 64Mi limit and the kernel kills it.",
            SignalKind.OomKilled,
            "c1-oomkill");

        await SeedAsync(
            "incident: ImagePullBackOff on c4-imagepull (hephaisto-chaos). The pod references "
            + "busybox:this-tag-does-not-exist, which is not a published tag.",
            SignalKind.ImagePullBackOff,
            "c4-imagepull");
    }

    // ---------------------------------------------------------------- the reproduction

    [Fact]
    public async Task Crash_finds_CrashLoopBackOff_which_the_lexical_arm_alone_cannot()
    {
        await SeedCorpusAsync();

        await using var db = pg.CreateContext();

        var lexicalOnly = await Search(db).SearchAsync(
            "crash", queryEmbedding: null, new SearchFilter(), 10, TestContext.Current.CancellationToken);

        // Red before the trigram arm existed, and the assertion that documents why: every hit
        // here comes from trigrams, and none of them from full text.
        lexicalOnly.Hits.Should().NotBeEmpty();
        lexicalOnly.Hits.Should().Contain(h => h.Kind == SignalKind.CrashLoopBackOff);
        lexicalOnly.Hits.Should().OnlyContain(h => h.LexicalRank == null);
        lexicalOnly.Hits.Should().Contain(h => h.TrigramRank != null);
    }

    [Fact]
    public async Task The_full_text_arm_still_answers_what_it_was_always_good_at()
    {
        // The trigram arm is an addition, not a replacement. Prose queries must still reach the
        // lexical arm, which is the one that ranks them well.
        await SeedCorpusAsync();

        await using var db = pg.CreateContext();

        var hits = await Search(db).SearchAsync(
            "container exits at startup", queryEmbedding: null, new SearchFilter(), 10,
            TestContext.Current.CancellationToken);

        hits.Hits.Should().Contain(h => h.LexicalRank != null);
    }

    [Fact]
    public async Task An_exact_identifier_is_found_by_the_literal_an_SRE_would_paste()
    {
        await SeedCorpusAsync();

        await using var db = pg.CreateContext();

        var hits = await Search(db).SearchAsync(
            "this-tag-does-not-exist", queryEmbedding: null, new SearchFilter(), 10,
            TestContext.Current.CancellationToken);

        hits.Hits.Should().ContainSingle().Which.Kind.Should().Be(SignalKind.ImagePullBackOff);
    }

    // ---------------------------------------------------------------- the arms report themselves

    [Fact]
    public async Task A_search_reports_which_arms_ran_rather_than_leaving_it_to_be_inferred()
    {
        await SeedCorpusAsync();

        await using var db = pg.CreateContext();

        var withoutVector = await Search(db).SearchAsync(
            "crash", queryEmbedding: null, new SearchFilter(), 10, TestContext.Current.CancellationToken);

        withoutVector.SemanticArmRan.Should().BeFalse();
        withoutVector.TrigramArmRan.Should().BeTrue();
    }

    [Fact]
    public async Task The_vector_arm_runs_and_ranks_when_an_embedding_is_supplied()
    {
        // The arm that had never executed. A synthetic embedding is enough: the assertion is
        // that the CTE runs, joins and produces a rank - not that Gemini has good taste.
        await pg.ResetAsync();
        _seeded = 0;

        var near = Vector(0.9f);
        var far = Vector(-0.9f);

        await SeedAsync(
            "incident: CrashLoopBackOff on c2-crashloop (hephaisto-chaos).",
            SignalKind.CrashLoopBackOff, "c2-crashloop", embedding: near);

        await SeedAsync(
            "incident: PvcNearlyFull on c6-diskfill (hephaisto-chaos).",
            SignalKind.PvcNearlyFull, "c6-diskfill", embedding: far);

        await using var db = pg.CreateContext();

        var hits = await Search(db).SearchAsync(
            "pods keep dying after the deploy", near, new SearchFilter(), 10,
            TestContext.Current.CancellationToken);

        hits.SemanticArmRan.Should().BeTrue();
        hits.Hits.Should().Contain(h => h.SemanticRank != null);

        // The nearer vector ranks first in the semantic arm, which is the only claim a
        // synthetic embedding can support.
        hits.Hits.OrderBy(h => h.SemanticRank ?? int.MaxValue).First()
            .Kind.Should().Be(SignalKind.CrashLoopBackOff);
    }

    [Fact]
    public async Task A_digest_with_no_embedding_is_still_reachable_when_the_vector_arm_runs()
    {
        // The mixed corpus every real deployment has: digests written before embedding worked
        // sit alongside embedded ones. The semantic CTE filters them out with
        // `WHERE embedding IS NOT NULL`, so the other two arms are what keeps them findable.
        await pg.ResetAsync();
        _seeded = 0;

        await SeedAsync(
            "incident: ImagePullBackOff on c4-imagepull (hephaisto-chaos). The pod references "
            + "busybox:this-tag-does-not-exist.",
            SignalKind.ImagePullBackOff, "c4-imagepull", embedding: null);

        await using var db = pg.CreateContext();

        var hits = await Search(db).SearchAsync(
            "this-tag-does-not-exist", Vector(0.5f), new SearchFilter(), 10,
            TestContext.Current.CancellationToken);

        hits.Hits.Should().ContainSingle();
        hits.Hits[0].SemanticRank.Should().BeNull();
    }

    // ---------------------------------------------------------------- fusion and filters

    [Fact]
    public async Task A_digest_matched_by_three_arms_outranks_one_matched_by_a_single_arm()
    {
        // What RRF is for. The fold sums each arm's contribution, so agreement between arms is
        // what moves a result up - and a fold that dropped one arm's contribution would still
        // return plausible-looking rows in the wrong order, which is the failure nobody notices.
        await pg.ResetAsync();
        _seeded = 0;

        var embedding = Vector(0.8f);

        // Matched by all three: the text carries the query string, and it is embedded.
        await SeedAsync(
            "incident: CrashLoopBackOff on c2-crashloop (hephaisto-chaos). The container exits 1.",
            SignalKind.CrashLoopBackOff, "c2-crashloop", embedding: embedding);

        // Matched by the vector arm alone: the same embedding, and text that shares neither a
        // lexeme nor a trigram extent with the query.
        await SeedAsync(
            "incident: PvcNearlyFull on c6-diskfill (hephaisto-chaos). The volume is nearly full.",
            SignalKind.PvcNearlyFull, "c6-diskfill", embedding: embedding);

        await using var db = pg.CreateContext();

        var hits = await Search(db).SearchAsync(
            "CrashLoopBackOff", embedding, new SearchFilter(), 10, TestContext.Current.CancellationToken);

        var agreed = hits.Hits.Single(h => h.Kind == SignalKind.CrashLoopBackOff);
        var semanticOnly = hits.Hits.Single(h => h.Kind == SignalKind.PvcNearlyFull);

        // A single camelCase reason string is the one query shape that reaches both text arms:
        // full text lexes it to one token the digest also contains, and it is short enough to
        // clear the word-similarity threshold. A multi-word query reaches the lexical arm and
        // misses the trigram one - correct behaviour, and a useless fixture for testing fusion.
        agreed.LexicalRank.Should().NotBeNull();
        agreed.TrigramRank.Should().NotBeNull();
        agreed.SemanticRank.Should().NotBeNull();

        semanticOnly.LexicalRank.Should().BeNull();
        semanticOnly.TrigramRank.Should().BeNull();
        semanticOnly.SemanticRank.Should().NotBeNull();

        agreed.Score.Should().BeGreaterThan(semanticOnly.Score);
        hits.Hits[0].Kind.Should().Be(SignalKind.CrashLoopBackOff);
    }

    [Fact]
    public async Task Filters_narrow_after_fusion_rather_than_inside_an_arm()
    {
        await SeedCorpusAsync();

        await SeedAsync(
            "incident: CrashLoopBackOff on other-app (default). Unrelated.",
            SignalKind.CrashLoopBackOff, "other-app", resolved: false);

        await using var db = pg.CreateContext();

        var resolvedOnly = await Search(db).SearchAsync(
            "crash",
            queryEmbedding: null,
            new SearchFilter { ResolvedOnly = true },
            10,
            TestContext.Current.CancellationToken);

        resolvedOnly.Hits.Should().NotBeEmpty();
        resolvedOnly.Hits.Should().OnlyContain(h => h.Resolved);
    }

    [Fact]
    public async Task A_kind_filter_is_what_runbook_memory_will_retrieve_through()
    {
        await SeedCorpusAsync();

        await using var db = pg.CreateContext();

        var hits = await Search(db).SearchAsync(
            "hephaisto-chaos",
            queryEmbedding: null,
            new SearchFilter { Kinds = [SignalKind.OomKilled], ResolvedOnly = true },
            10,
            TestContext.Current.CancellationToken);

        hits.Hits.Should().OnlyContain(h => h.Kind == SignalKind.OomKilled);
    }

    [Fact]
    public async Task An_empty_query_with_no_embedding_returns_empty_rather_than_everything()
    {
        await SeedCorpusAsync();

        await using var db = pg.CreateContext();

        var hits = await Search(db).SearchAsync(
            "   ", queryEmbedding: null, new SearchFilter(), 10, TestContext.Current.CancellationToken);

        hits.Hits.Should().BeEmpty();
        hits.SemanticArmRan.Should().BeFalse();
    }

    /// <summary>
    /// A vector of the configured width, filled with one value.
    /// </summary>
    /// <remarks>
    /// The width comes from <c>LlmOptions</c> rather than a literal 768, because the column was
    /// created from that same default - hard-coding it here would pass until someone changed the
    /// option, then fail with a pgvector width error that says nothing about this test.
    /// </remarks>
    private static float[] Vector(float value)
    {
        var v = new float[new LlmOptions().EmbeddingDimensions];
        Array.Fill(v, value);
        return v;
    }
}

/// <summary>A monitor over a fixed value; nothing here exercises reload.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
