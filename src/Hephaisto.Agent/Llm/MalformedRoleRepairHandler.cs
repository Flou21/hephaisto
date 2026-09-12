using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// Repairs a chat completion whose message carries an empty <c>role</c>, which the OpenAI SDK
/// cannot deserialise.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fault this fixes has been discarding investigations since v0.7.0.</b> It surfaces as
/// <c>ArgumentOutOfRangeException: Unknown ChatMessageRole value. (Parameter 'value') Actual value
/// was .</c> thrown from <c>ChatMessageRoleExtensions.ToChatMessageRole</c> while deserialising a
/// response choice - the role is the empty string. The whole investigation then faults and its
/// work is thrown away, which on a real incident means a lost diagnosis with no explanation.
/// </para>
/// <para>
/// <b>Why a repair rather than a retry.</b> v0.7.0 added retry for a provider's HTTP 500, and
/// widening that to cover this was the obvious next move and the wrong one. This is not a
/// transport failure: the request succeeded, the model answered, and one field of the answer is
/// malformed. Retrying discards a complete and usable response, spends the tokens again, and
/// succeeds only by chance - it converts a deterministic defect into an intermittent one, which
/// is the harder kind to ever diagnose.
/// </para>
/// <para>
/// <b>Observed on Ollama's OpenAI-compatible endpoint</b> serving gpt-oss:120b, intermittently
/// and usually mid-conversation - one recording got through nine tool calls before hitting it.
/// Every provider in this codebase's seam speaks the OpenAI wire format, and this repairs the
/// wire format rather than naming a provider, so a second implementation with the same quirk
/// needs no second fix.
/// </para>
/// <para>
/// <b>It repairs exactly one thing.</b> An absent or empty role becomes <c>assistant</c>, which
/// is the only role a completion choice can carry. Anything else malformed still throws, loudly,
/// because a handler that silently normalised whatever it did not understand would hide the next
/// defect of this class instead of surfacing it.
/// </para>
/// <para>
/// Streaming responses are passed through untouched. The fault is on the non-streaming path -
/// the stack names <c>InternalCreateChatCompletionResponseChoice</c> - and rewriting a
/// <c>text/event-stream</c> body would break the stream to fix a bug that is not in it.
/// </para>
/// </remarks>
internal sealed class MalformedRoleRepairHandler(ILogger<MalformedRoleRepairHandler> logger)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode || !IsJson(response.Content.Headers.ContentType))
        {
            return response;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!TryRepair(body, out var repaired))
        {
            // Untouched, including the original content headers - anything else would risk
            // changing a response that was never broken.
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return response;
        }

        logger.LogWarning(
            "The provider returned a completion choice with an empty role; repaired it to "
            + "'assistant'. Without this the SDK throws and the whole investigation is discarded.");

        response.Content = new StringContent(repaired, Encoding.UTF8, "application/json");

        return response;
    }

    private static bool IsJson(MediaTypeHeaderValue? contentType) =>
        contentType?.MediaType is "application/json";

    /// <summary>
    /// Returns true only when something was actually changed, so the caller can leave a
    /// well-formed body completely alone.
    /// </summary>
    internal static bool TryRepair(string body, out string repaired)
    {
        repaired = body;

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            // Not JSON after all. Let the SDK produce its own error rather than inventing one.
            return false;
        }

        if (root?["choices"] is not JsonArray choices)
        {
            return false;
        }

        var changed = false;

        foreach (var choice in choices)
        {
            if (choice?["message"] is not JsonObject message)
            {
                continue;
            }

            // Absent and empty are the same defect: the SDK maps both onto the empty string and
            // then refuses it.
            if (message.TryGetPropertyValue("role", out var role)
                && !string.IsNullOrEmpty(role?.GetValue<string>()))
            {
                continue;
            }

            message["role"] = "assistant";
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        repaired = root!.ToJsonString();

        return true;
    }
}
