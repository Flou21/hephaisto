using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Notifications;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.Notifications.TeamsBot;

/// <summary>
/// How one call to Teams ended. Never an exception: a failure is a value, so that one bad call
/// cannot take down the loop that maintains every other card.
/// </summary>
/// <param name="Status">Null when there was no answer at all.</param>
/// <param name="Detail">What the endpoint said. Never a metric label, and never the credential.</param>
public readonly record struct TeamsBotResult<T>(HttpStatusCode? Status, T? Value, string? Detail)
{
    public bool Ok => Status is { } s && (int)s is >= 200 and < 300;

    /// <summary>The message is not there any more, so editing it will never work.</summary>
    public bool Gone => Status is HttpStatusCode.NotFound or HttpStatusCode.Gone;

    /// <summary>The same classification every other channel uses, so they cannot disagree.</summary>
    public DeliveryResult ToDelivery() => Status is { } s
        ? DeliveryResult.FromStatus(s, Detail)
        : DeliveryResult.Retry(Detail ?? "no answer");
}

/// <summary>A message in a channel, as Teams names it.</summary>
public readonly record struct TeamsPosted(string ConversationId, string ActivityId);

/// <summary>A member of the team, as the roster names them.</summary>
/// <param name="Id">The Teams user id, <c>29:...</c>.</param>
/// <param name="AadObjectId">The Microsoft Entra object id: what a click carries in <c>from.aadObjectId</c>.</param>
public sealed record TeamsMember(string Id, string? AadObjectId, string? Name, string? Email, string? UserPrincipalName)
{
    /// <summary>
    /// Who a click is recorded as: the login name, then the mailbox, then the display name. Read
    /// from the roster, never from the click - the click says who it claims to be, the roster says
    /// who that is.
    /// </summary>
    public string Actor => UserPrincipalName ?? Email ?? Name ?? Id;
}

/// <summary>
/// What the bot can do in Teams.
/// </summary>
/// <remarks>
/// <b>There is no delete here, and that is the design rather than an omission.</b> Teams leaves
/// "This message has been deleted." in place of a deleted channel post and of a deleted reply
/// alike, so the way a message goes away is by being edited into something smaller. A test
/// asserts this interface never grows the method.
/// </remarks>
public interface ITeamsBotClient
{
    /// <summary>Starts a new thread in the configured channel.</summary>
    Task<TeamsBotResult<TeamsPosted>> PostToChannelAsync(JsonObject activity, CancellationToken ct);

    /// <summary>
    /// The Teams user id of a team member, found by mailbox or login name. A successful result
    /// with a null value means Teams answered and the person is not in the team.
    /// </summary>
    Task<TeamsBotResult<string>> FindMemberAsync(string email, CancellationToken ct);

    /// <summary>
    /// A member of the team, found by Microsoft Entra object id. A successful result with a null
    /// value means Teams answered and nobody in the team has that id.
    /// </summary>
    Task<TeamsBotResult<TeamsMember>> FindMemberByObjectIdAsync(string aadObjectId, CancellationToken ct);

    /// <summary>The bot's personal chat with a user. Asking twice returns the same chat.</summary>
    Task<TeamsBotResult<string>> OpenChatAsync(string userId, CancellationToken ct);

    /// <summary>Posts into a conversation and returns the new message's id.</summary>
    Task<TeamsBotResult<string>> SendAsync(string conversationId, JsonObject activity, CancellationToken ct);

    /// <summary>Replaces a message the bot sent earlier.</summary>
    Task<TeamsBotResult<bool>> UpdateAsync(
        string conversationId,
        string activityId,
        JsonObject activity,
        CancellationToken ct);
}

/// <summary>
/// The bot's access token, shared by every client instance.
/// </summary>
/// <remarks>
/// A singleton because the typed client is transient: without it every scope would ask Microsoft
/// for a new token, which is slow, rate limited, and makes an identity-provider hiccup into a
/// failed delivery.
/// </remarks>
public sealed class TeamsBotTokenCache
{
    private readonly Lock gate = new();
    private string? token;
    private DateTimeOffset validUntil;

    public string? Get(DateTimeOffset now)
    {
        lock (gate)
        {
            return token is not null && now < validUntil ? token : null;
        }
    }

    public void Set(string value, DateTimeOffset until)
    {
        lock (gate)
        {
            token = value;
            validUntil = until;
        }
    }

    public void Forget()
    {
        lock (gate)
        {
            token = null;
        }
    }
}

/// <summary>
/// The Bot Connector REST API, spoken directly.
/// </summary>
/// <remarks>
/// <para>
/// No SDK. The Bot Framework SDK reached end of life on 2025-12-31 and its successor is an agent
/// framework; what is needed here is a token request and four calls, and a dependency that can
/// receive messages is a larger thing to have in this process than one that cannot.
/// </para>
/// <para>
/// <b>It does not retry.</b> The outbox and the reconciler's next tick are the retry authorities,
/// for the reason every channel here gives: they survive a restart and this does not.
/// </para>
/// </remarks>
public sealed class TeamsBotClient(
    HttpClient http,
    TeamsBotTokenCache tokens,
    IClock clock,
    IOptionsMonitor<NotificationOptions> options,
    ILogger<TeamsBotClient> logger) : ITeamsBotClient
{
    private const string Scope = "https://api.botframework.com/.default";

    /// <summary>The roster's largest page. A team larger than forty of them is not searched further.</summary>
    private const int PageSize = 500;

    private const int MaxPages = 40;

    /// <summary>Renewed this long before it expires, so a call never starts with a token about to lapse.</summary>
    private static readonly TimeSpan RenewAhead = TimeSpan.FromMinutes(5);

    private TeamsBotOptions Bot => options.CurrentValue.TeamsBot;

    public async Task<TeamsBotResult<TeamsPosted>> PostToChannelAsync(JsonObject activity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var bot = Bot;

        var body = new JsonObject
        {
            ["isGroup"] = true,
            ["tenantId"] = bot.TenantId,
            ["bot"] = new JsonObject { ["id"] = bot.AppId, ["name"] = "Hephaisto" },
            ["channelData"] = new JsonObject
            {
                ["channel"] = new JsonObject { ["id"] = bot.ChannelId },
                ["tenant"] = new JsonObject { ["id"] = bot.TenantId },
            },
            ["activity"] = activity.DeepClone(),
        };

        var answer = await CallAsync(HttpMethod.Post, "v3/conversations", body, ct).ConfigureAwait(false);

        if (!answer.Ok)
        {
            return new TeamsBotResult<TeamsPosted>(answer.Status, default, answer.Detail);
        }

        var conversation = Text(answer.Value, "id");
        var posted = Text(answer.Value, "activityId");

        return conversation is null || posted is null
            ? new TeamsBotResult<TeamsPosted>(HttpStatusCode.BadGateway, default, "Teams accepted the post and named no message")
            : new TeamsBotResult<TeamsPosted>(answer.Status, new TeamsPosted(conversation, posted), null);
    }

    public async Task<TeamsBotResult<string>> FindMemberAsync(string email, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var found = await SearchRosterAsync(
            m => Same(Text(m, "email"), email) || Same(Text(m, "userPrincipalName"), email), ct).ConfigureAwait(false);

        return found.Ok && found.Value is null
            ? new TeamsBotResult<string>(HttpStatusCode.OK, null, $"{email} is not a member of the team")
            : new TeamsBotResult<string>(found.Status, found.Value?.Id, found.Detail);
    }

    public async Task<TeamsBotResult<TeamsMember>> FindMemberByObjectIdAsync(string aadObjectId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aadObjectId);

        var found = await SearchRosterAsync(m => Same(Text(m, "aadObjectId"), aadObjectId), ct).ConfigureAwait(false);

        return found.Ok && found.Value is null
            ? new TeamsBotResult<TeamsMember>(HttpStatusCode.OK, null, "nobody in the team has that object id")
            : found;
    }

    /// <summary>The first member of the team the predicate accepts, paging through the roster.</summary>
    private async Task<TeamsBotResult<TeamsMember>> SearchRosterAsync(Func<JsonNode, bool> match, CancellationToken ct)
    {
        var roster = $"v3/conversations/{Uri.EscapeDataString(Bot.ChannelId ?? string.Empty)}/pagedmembers";
        string? continuation = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var path = continuation is null
                ? $"{roster}?pageSize={PageSize}"
                : $"{roster}?pageSize={PageSize}&continuationToken={Uri.EscapeDataString(continuation)}";

            var answer = await CallAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);

            if (!answer.Ok)
            {
                return new TeamsBotResult<TeamsMember>(answer.Status, null, answer.Detail);
            }

            foreach (var member in answer.Value?["members"]?.AsArray() ?? [])
            {
                if (member is not null && match(member) && Text(member, "id") is { } id)
                {
                    return new TeamsBotResult<TeamsMember>(
                        answer.Status,
                        new TeamsMember(
                            id,
                            Text(member, "aadObjectId"),
                            Text(member, "name"),
                            Text(member, "email"),
                            Text(member, "userPrincipalName")),
                        null);
                }
            }

            continuation = Text(answer.Value, "continuationToken");

            if (string.IsNullOrEmpty(continuation))
            {
                break;
            }
        }

        // Teams answered, and the person is not a member. Not an error of the transport.
        return new TeamsBotResult<TeamsMember>(HttpStatusCode.OK, null, null);
    }

    public async Task<TeamsBotResult<string>> OpenChatAsync(string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var bot = Bot;

        var body = new JsonObject
        {
            ["bot"] = new JsonObject { ["id"] = bot.AppId, ["name"] = "Hephaisto" },
            ["members"] = new JsonArray(new JsonObject { ["id"] = userId }),
            ["channelData"] = new JsonObject { ["tenant"] = new JsonObject { ["id"] = bot.TenantId } },
            ["tenantId"] = bot.TenantId,
        };

        var answer = await CallAsync(HttpMethod.Post, "v3/conversations", body, ct).ConfigureAwait(false);

        return Named(answer, "Teams opened the chat and named no conversation");
    }

    public async Task<TeamsBotResult<string>> SendAsync(string conversationId, JsonObject activity, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(activity);

        var answer = await CallAsync(
            HttpMethod.Post,
            $"v3/conversations/{Uri.EscapeDataString(conversationId)}/activities",
            activity,
            ct).ConfigureAwait(false);

        return Named(answer, "Teams accepted the message and named no id");
    }

    public async Task<TeamsBotResult<bool>> UpdateAsync(
        string conversationId,
        string activityId,
        JsonObject activity,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        ArgumentNullException.ThrowIfNull(activity);

        var answer = await CallAsync(
            HttpMethod.Put,
            $"v3/conversations/{Uri.EscapeDataString(conversationId)}/activities/{Uri.EscapeDataString(activityId)}",
            activity,
            ct).ConfigureAwait(false);

        return new TeamsBotResult<bool>(answer.Status, answer.Ok, answer.Detail);
    }

    private static TeamsBotResult<string> Named(TeamsBotResult<JsonNode> answer, string unnamed)
    {
        if (!answer.Ok)
        {
            return new TeamsBotResult<string>(answer.Status, null, answer.Detail);
        }

        return Text(answer.Value, "id") is { } id
            ? new TeamsBotResult<string>(answer.Status, id, null)
            : new TeamsBotResult<string>(HttpStatusCode.BadGateway, null, unnamed);
    }

    private async Task<TeamsBotResult<JsonNode>> CallAsync(
        HttpMethod method,
        string path,
        JsonObject? body,
        CancellationToken ct)
    {
        try
        {
            var token = await TokenAsync(ct).ConfigureAwait(false);

            if (!token.Ok || token.Value is null)
            {
                return new TeamsBotResult<JsonNode>(token.Status, null, token.Detail);
            }

            using var request = new HttpRequestMessage(method, $"{Bot.ServiceUrl.TrimEnd('/')}/{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                // The token was refused, so the next call must not present it again.
                tokens.Forget();
            }

            if (!response.IsSuccessStatusCode)
            {
                return new TeamsBotResult<JsonNode>(response.StatusCode, null, Short(text));
            }

            return new TeamsBotResult<JsonNode>(response.StatusCode, Parse(text), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A call to Teams failed before it was answered.");

            return new TeamsBotResult<JsonNode>(null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<TeamsBotResult<string>> TokenAsync(CancellationToken ct)
    {
        if (tokens.Get(clock.UtcNow) is { } cached)
        {
            return new TeamsBotResult<string>(HttpStatusCode.OK, cached, null);
        }

        var bot = Bot;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{bot.LoginUrl.TrimEnd('/')}/{Uri.EscapeDataString(bot.TenantId ?? string.Empty)}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = bot.AppId ?? string.Empty,
                ["client_secret"] = bot.ClientSecret ?? string.Empty,
                ["scope"] = Scope,
            }),
        };

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var answer = Parse(text);

        if (!response.IsSuccessStatusCode || Text(answer, "access_token") is not { } token)
        {
            // Microsoft's error names what is wrong and never echoes the secret. The body is
            // still not passed on whole: only the two fields written for a person.
            var why = Text(answer, "error_description") ?? Text(answer, "error") ?? "no token in the answer";

            return new TeamsBotResult<string>(
                response.IsSuccessStatusCode ? HttpStatusCode.BadGateway : response.StatusCode,
                null,
                $"token request refused: {Short(why)}");
        }

        var seconds = answer?["expires_in"] is JsonValue v && v.TryGetValue<int>(out var s) ? s : 3600;
        var lifetime = TimeSpan.FromSeconds(seconds) - RenewAhead;

        tokens.Set(token, clock.UtcNow + (lifetime > TimeSpan.Zero ? lifetime : TimeSpan.Zero));

        return new TeamsBotResult<string>(HttpStatusCode.OK, token, null);
    }

    private static bool Same(string? a, string b) =>
        a is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static JsonNode? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Short(string text) => text.Length > 300 ? text[..300] : text;
}
