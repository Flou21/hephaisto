using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hephaisto.Core.Domain;

namespace Hephaisto.Eval.Scoring;

/// <summary>One judged diagnosis. Null anywhere means the judge could not be asked.</summary>
public sealed record JudgeVerdict(bool Correct, string Reason);

/// <summary>
/// Grades a diagnosis against the known-correct answer.
/// </summary>
/// <remarks>
/// An interface so a run can be scored without a network call and without spending money - the
/// tests substitute it, and <c>--no-judge</c> omits it entirely.
/// </remarks>
public interface IRootCauseJudge
{
    /// <summary>
    /// Returns null when the judge could not be reached or did not answer usably. Null is not a
    /// verdict of "incorrect": a judge that failed to respond has said nothing.
    /// </summary>
    Task<JudgeVerdict?> AskAsync(string truth, string diagnosis, CancellationToken ct);
}

/// <summary>
/// The LLM judge, ported from <c>scripts/e2e/lib/judge.sh</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The prompt is copied verbatim</b> from that script, and should stay that way. Two graders
/// scoring the same fixture with differently worded prompts produce two numbers that cannot be
/// compared, and the e2e harness will keep reporting its own.
/// </para>
/// <para>
/// <b>This never gates a run</b>, exactly as the shell version does not: <i>"A judge is another
/// language model having an opinion, and a release must not be blocked by one."</i> Its verdict
/// is reported next to the deterministic one from <see cref="StructuralGrader"/>, and where the
/// two disagree the run says so rather than picking a winner.
/// </para>
/// <para>
/// Deliberately a different and cheaper model than the agent's. A judge that is the same model
/// reasoning about its own output is closer to self-assessment than to review.
/// </para>
/// </remarks>
public sealed class GeminiRootCauseJudge(HttpClient http, string apiKey, string model) : IRootCauseJudge
{
    public const string DefaultModel = "gemini-3.7-flash";

    /// <summary>
    /// Reads the same environment variables the shell judge reads, so the two are configured
    /// identically. Returns null when no key is available - the caller then skips grading rather
    /// than failing, because "no API key" is a missing instrument, not a bad agent.
    /// </summary>
    public static GeminiRootCauseJudge? FromEnvironment(HttpClient http)
    {
        var key = Environment.GetEnvironmentVariable("HEPHAISTO_GEMINI_API_KEY");

        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var model = Environment.GetEnvironmentVariable("JUDGE_MODEL");

        return new GeminiRootCauseJudge(
            http,
            key,
            string.IsNullOrWhiteSpace(model) ? DefaultModel : model);
    }

    /// <summary>
    /// Renders a finding into the exact text the shell judge sends.
    /// </summary>
    /// <remarks>
    /// Format and 4000-character cap copied from <c>judge.sh</c>, whose comment calls this "the
    /// primary hypothesis plus its evidence excerpts - what a human would read". Sending a
    /// differently shaped diagnosis to the same prompt would make the two harnesses'
    /// numbers incomparable just as surely as rewording the prompt would.
    /// </remarks>
    public static string Describe(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var text = $"HYPOTHESIS: {finding.Hypothesis}\nEVIDENCE: "
            + string.Join(" | ", finding.Evidence.Select(e => e.Excerpt));

        return text.Length <= 4000 ? text : text[..4000];
    }

    /// <summary>
    /// The grading prompt, shared by every judge implementation.
    /// </summary>
    /// <remarks>
    /// Shared rather than duplicated for the same reason it is copied verbatim from
    /// <c>judge.sh</c>: two graders scoring the same fixture with differently worded prompts
    /// produce two numbers that cannot be compared. A second provider must not become a second
    /// question.
    /// </remarks>
    internal static string BuildPrompt(string truth, string diagnosis) =>
        "You are grading an SRE agent's incident diagnosis against a known-correct answer.\n\n"
        + "KNOWN CORRECT ANSWER:\n" + truth + "\n\n"
        + "THE AGENT SAID:\n" + diagnosis + "\n\n"
        + "Did the agent identify the same underlying root cause? Judge the CAUSE, not the wording, "
        + "and not whether it restated the Kubernetes symptom. Restating the symptom "
        + "(\"the pod is in CrashLoopBackOff\") without identifying why is NOT correct. "
        + "Answer strictly as JSON: {\"correct\": true|false, \"reason\": \"<one sentence>\"}";

    public async Task<JudgeVerdict?> AskAsync(string truth, string diagnosis, CancellationToken ct)
    {
        var prompt = BuildPrompt(truth, diagnosis);

        var payload = new
        {
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = prompt } } },
            },
            generationConfig = new { temperature = 0, responseMimeType = "application/json" },
        };

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent")
            {
                Content = JsonContent.Create(payload),
            };

            request.Headers.Add("x-goog-api-key", apiKey);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<GeminiResponse>(ct).ConfigureAwait(false);
            var text = body?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            return string.IsNullOrWhiteSpace(text) ? null : Parse(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // A judge that cannot be reached says nothing. It must not be able to fail a run, and
            // it must not be silently recorded as a verdict of "incorrect".
            return null;
        }
    }

    internal static JudgeVerdict? Parse(string text)
    {
        try
        {
            var verdict = JsonSerializer.Deserialize<RawVerdict>(text, Cassette.Json);

            return verdict is null
                ? null
                : new JudgeVerdict(verdict.Correct, verdict.Reason ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal sealed record RawVerdict
    {
        [JsonPropertyName("correct")]
        public bool Correct { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }

    private sealed record GeminiResponse
    {
        [JsonPropertyName("candidates")]
        public List<Candidate>? Candidates { get; init; }
    }

    private sealed record Candidate
    {
        [JsonPropertyName("content")]
        public Content? Content { get; init; }
    }

    private sealed record Content
    {
        [JsonPropertyName("parts")]
        public List<Part>? Parts { get; init; }
    }

    private sealed record Part
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }
}

/// <summary>
/// The same judge against any OpenAI-compatible server - hosted, or a local Ollama.
/// </summary>
/// <remarks>
/// <para>
/// Added because the Gemini-only judge had a consequence that only showed up when it mattered:
/// with no Gemini credit the judge could not run at all, so a comparison between two models was
/// scored deterministically and was quietly <b>not comparable</b> to the published 22/24, which
/// was judged. A grading instrument reachable through exactly one vendor disappears at the moment
/// you switch vendors, which is the moment you most need to grade something.
/// </para>
/// <para>
/// <b>The self-assessment caveat still applies and gets sharper here.</b> Pointing this at the
/// same endpoint and model the agent ran on makes the judge the agent marking its own homework.
/// That is weaker than two independent models, though not worthless - the grade is against a
/// fixed answer key rather than against the agent's own reasoning. Set <c>JUDGE_ENDPOINT</c> and
/// <c>JUDGE_MODEL</c> to something else when a second model is available.
/// </para>
/// </remarks>
public sealed class OpenAiRootCauseJudge(HttpClient http, string endpoint, string? apiKey, string model)
    : IRootCauseJudge
{
    public async Task<JudgeVerdict?> AskAsync(string truth, string diagnosis, CancellationToken ct)
    {
        var payload = new
        {
            model,
            messages = new[]
            {
                new { role = "user", content = GeminiRootCauseJudge.BuildPrompt(truth, diagnosis) },
            },
            temperature = 0,
            response_format = new { type = "json_object" },
        };

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{endpoint.TrimEnd('/')}/chat/completions")
            {
                Content = JsonContent.Create(payload),
            };

            // A local server needs no credential, and sending an empty bearer token is worse
            // than sending none: some gateways reject it outright rather than ignoring it.
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
            }

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<OpenAiResponse>(ct).ConfigureAwait(false);
            var text = body?.Choices?.FirstOrDefault()?.Message?.Content;

            return string.IsNullOrWhiteSpace(text) ? null : GeminiRootCauseJudge.Parse(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Same contract as the Gemini arm: a judge that cannot be reached says nothing, and
            // must never be recorded as a verdict of "incorrect".
            return null;
        }
    }

    private sealed record OpenAiResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChoice>? Choices);

    private sealed record OpenAiChoice(
        [property: JsonPropertyName("message")] OpenAiMessage? Message);

    private sealed record OpenAiMessage(
        [property: JsonPropertyName("content")] string? Content);
}

/// <summary>
/// Picks the judge from the environment, mirroring <c>scripts/e2e/lib/judge.sh</c> exactly so the
/// two harnesses are configured the same way and their numbers stay comparable.
/// </summary>
/// <remarks>
/// Returns null when nothing is reachable. That is a missing instrument, not a bad agent, and the
/// caller scores deterministically rather than failing.
/// </remarks>
public static class RootCauseJudgeFactory
{
    public static IRootCauseJudge? FromEnvironment(HttpClient http)
    {
        var provider = Environment.GetEnvironmentVariable("JUDGE_PROVIDER")
            ?? Environment.GetEnvironmentVariable("LLM_PROVIDER")
            ?? Environment.GetEnvironmentVariable("HEPHAISTO_LLM_PROVIDER");

        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = Environment.GetEnvironmentVariable("JUDGE_ENDPOINT")
                ?? Environment.GetEnvironmentVariable("HEPHAISTO_LLM_ENDPOINT");

            var model = Environment.GetEnvironmentVariable("JUDGE_MODEL")
                ?? Environment.GetEnvironmentVariable("HEPHAISTO_LLM_MODEL");

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
            {
                return null;
            }

            var key = Environment.GetEnvironmentVariable("JUDGE_API_KEY")
                ?? Environment.GetEnvironmentVariable("HEPHAISTO_LLM_API_KEY");

            return new OpenAiRootCauseJudge(http, endpoint, key, model);
        }

        return GeminiRootCauseJudge.FromEnvironment(http);
    }
}
