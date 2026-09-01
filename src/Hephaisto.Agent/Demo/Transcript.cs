using System.Text.Json;
using System.Text.Json.Serialization;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Demo;

/// <summary>
/// One replayed investigation, kept: the steps, the findings, the evidence and the verdict.
/// </summary>
/// <remarks>
/// <para>
/// <b>A cassette is the input half of a run; this is the output half.</b> The distinction is
/// the whole reason this type exists. A cassette deliberately does not record the model - the
/// model is the thing under test, so replay serves recorded tool output to a live one - which
/// means replaying the corpus is a live, paid, non-deterministic model run and can never be a
/// demo. Everything a reader would actually want to look at was computed by
/// <c>hephaisto-eval run</c> and thrown away after scoring: <c>results/*.json</c> keeps
/// tallies and one hypothesis string, with no step log, no evidence and no citations.
/// </para>
/// <para>
/// Recording the output once turns a paid non-deterministic run into a committed artifact, and
/// every demo shape becomes key-free at the same moment: a console seeded with real
/// investigations, a terminal transcript, a rendered page. None of them need a model, a
/// cluster, or an account.
/// </para>
/// <para>
/// <b>The verdict travels with the transcript, including when it is bad.</b> A transcript where
/// the agent reached the wrong conclusion is still publishable and should still ship - the
/// corpus is 8-of-10, and a demo that quietly showed only the eight would misrepresent the
/// number this project publishes. <see cref="Score"/> is the same <see cref="ScenarioScore"/>
/// the run report carries, so the grade shown beside an incident is the graded result and not
/// a second opinion.
/// </para>
/// <para>
/// <b>What this is not.</b> It is not a fixture for asserting against, and nothing in the test
/// suite should grow to depend on its contents. It records what one model did on one day; the
/// instrument that decides whether that was correct is the answer key, which lives elsewhere
/// and is not derived from this.
/// </para>
/// </remarks>
public sealed record Transcript
{
    /// <summary>The cassette this was replayed from - <c>c4-imagepull</c>.</summary>
    public required string CassetteId { get; init; }

    /// <summary>What was broken, in one line.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// The known-correct root cause from the answer key. Present so a reader can check the
    /// agent's conclusion against it without the eval harness, and never shown to a model.
    /// </summary>
    public required string ExpectedRootCause { get; init; }

    /// <summary>
    /// The incident the investigation ran against, exactly as the loop received it.
    /// </summary>
    /// <remarks>
    /// The domain type rather than the harness's <c>RecordedIncident</c>, which belongs to the
    /// eval assembly - and this one has to be readable by the shipped image, which does not
    /// reference it. It is also the type the seeder wants: no second mapping step between the
    /// artifact and the database.
    /// </remarks>
    public required Incident Incident { get; init; }

    /// <summary>
    /// The investigation graph: steps, findings, plan. Back-references are dropped on write
    /// (see <see cref="Json"/>) and must be re-linked by whatever loads this.
    /// </summary>
    public required Investigation Investigation { get; init; }

    /// <summary>
    /// The untruncated tool output the steps cite. Without these, every "view raw" link on a
    /// seeded incident is a 404 - which is precisely the provenance chain a demo exists to
    /// show, so they are part of the artifact rather than an optional extra.
    /// </summary>
    public required IReadOnlyList<EvidenceBlob> Blobs { get; init; }

    /// <summary>How this was graded, by the run that produced it.</summary>
    public required TranscriptGrade Score { get; init; }

    public required TranscriptOrigin Origin { get; init; }

    /// <summary>
    /// Cycle-tolerant, unlike <see cref="Cassette.Json"/>. The investigation graph is made of
    /// EF entities with parent back-references - a step knows its investigation, which knows
    /// its steps - so a serializer without this throws on the first one.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    public static Transcript Load(string path) =>
        JsonSerializer.Deserialize<Transcript>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} deserialised to null");

    /// <summary>
    /// Writes the transcript, redacted.
    /// </summary>
    /// <remarks>
    /// Redaction happens here rather than at the call site so a transcript cannot reach disk
    /// un-scrubbed by someone adding a second writer. See <see cref="TranscriptRedactor"/> for
    /// what is removed and why it is only that.
    /// </remarks>
    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(TranscriptRedactor.Redact(this), Json));
}

/// <summary>
/// The grade, reduced to what a reader is shown.
/// </summary>
/// <remarks>
/// <b>Deliberately not the eval harness's <c>ScenarioScore</c>.</b> That is the scorer's
/// internal shape and changes when the scorer does; this is a published artifact read by the
/// shipped image, and it should not have to version with an instrument that lives on the far
/// side of the project reference - which points from the harness to the agent, never back.
/// </remarks>
public sealed record TranscriptGrade
{
    /// <summary>Correct, Incorrect or NoFinding, from the deterministic grader.</summary>
    public required string Verdict { get; init; }

    /// <summary>What it proposed to do, graded separately against the answer key.</summary>
    public string? PlanVerdict { get; init; }

    /// <summary>The primary hypothesis, verbatim.</summary>
    public string? Hypothesis { get; init; }

    /// <summary>The judge's sentence, when one ran. Usually absent: the judge never gates.</summary>
    public string? JudgeReason { get; init; }

    /// <summary>True when every deterministic assertion passed.</summary>
    public bool StructurallySound { get; init; }

    public int StepsUsed { get; init; }

    public decimal CostUsd { get; init; }

    public string? TerminationReason { get; init; }
}

/// <summary>
/// Which model produced this, and against which recording - so a transcript can be traced and
/// re-recorded rather than trusted.
/// </summary>
/// <remarks>
/// <b>Two model ids, deliberately.</b> Nine of the ten cassettes were recorded against Gemini,
/// and a transcript replayed against a local open-weights model is that model's answer to
/// Gemini's tool trace. Collapsing the two into one field would hide a real property of the
/// artifact, and this repository has already been bitten by exactly that shape - the corpus
/// grading the model that recorded it, backlog #55.
/// </remarks>
public sealed record TranscriptOrigin
{
    /// <summary>The model that produced this investigation.</summary>
    public required string ModelId { get; init; }

    /// <summary>The model whose tool trace it was replayed against, when the cassette says.</summary>
    public string? RecordedAgainstModelId { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>The agent build that ran the loop.</summary>
    public required string AgentVersion { get; init; }

    /// <summary>
    /// The prompt-freshness line for the cassette at record time. A transcript recorded against
    /// a prompt that has since changed is stale in the same way a cassette is, and should say
    /// so rather than be silently re-read as current.
    /// </summary>
    public string? PromptFreshness { get; init; }
}
