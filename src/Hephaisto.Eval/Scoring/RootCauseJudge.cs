using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public async Task<JudgeVerdict?> AskAsync(string truth, string diagnosis, CancellationToken ct)
    {
        var prompt =
            "You are grading an SRE agent's incident diagnosis against a known-correct answer.\n\n"
            + "KNOWN CORRECT ANSWER:\n" + truth + "\n\n"
            + "THE AGENT SAID:\n" + diagnosis + "\n\n"
            + "Did the agent identify the same underlying root cause? Judge the CAUSE, not the wording, "
            + "and not whether it restated the Kubernetes symptom. Restating the symptom "
            + "(\"the pod is in CrashLoopBackOff\") without identifying why is NOT correct. "
            + "Answer strictly as JSON: {\"correct\": true|false, \"reason\": \"<one sentence>\"}";

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
