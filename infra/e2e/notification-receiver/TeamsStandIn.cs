using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotificationReceiver;

// A stand-in for Microsoft Teams, as the bot sees it.
//
// It answers the five calls the agent makes - a token, a post to a channel, a personal chat, a
// message, an edit, the member list - and keeps every message it was given, so the harness can
// read back what a person looking at Teams would see RIGHT NOW: one board, and what it says.
//
// It is here for the same reason the rest of this service is: so the path can be asserted end to
// end without a tenant. It is not a model of Teams. What it copies are the two behaviours the
// agent's design rests on, both measured against a real tenant on 2026-09-28:
//
//   - an id with ':' ';' '=' '@' in it has to arrive percent-encoded, or the path is cut short
//   - a DELETE leaves "This message has been deleted." behind - so here it is REFUSED and
//     COUNTED, and the count being zero is an assertion the harness makes
//
// It also stands in for the other direction, a click on a button (#124): it publishes a Bot
// Framework key document with two keys - one endorsed for msteams, one for another channel - and
// POST /teams/click signs an invoke activity the way Microsoft would and delivers it to the
// agent's actions port. What can be varied is exactly what the agent must refuse: another app id,
// another tenant, somebody outside the team, a key not endorsed for Teams, another serviceUrl.
//
// Who clicks is an address, and an address has one Entra object id here for ever (ObjectId): so
// "an approver" is whoever the agent's notifications.teamsBot.actions.approvers names by that id,
// and every other member of the team is one who is not. A click can carry what a card's inputs
// and buttons would: the reason typed into the Close card, the id of the action being decided.
public static class TeamsStandIn
{
    private const string Token = "stand-in-token";

    private sealed record Message(string Id, string Conversation, string Kind)
    {
        public JsonNode? Activity { get; set; }

        public int Edits { get; set; }

        public DateTimeOffset PostedAt { get; init; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? EditedAt { get; set; }
    }

    public static void Map(WebApplication app, IConfiguration configuration)
    {
        var messages = new ConcurrentDictionary<string, Message>();
        var deletes = 0;
        var next = 0;

        // Who is "in the team". A person outside it cannot be written to, which is a path the
        // agent has to survive.
        var members = (configuration["TEAMS_MEMBERS"] ?? "oncall@example.com,dev@example.com")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string NextId() => Interlocked.Increment(ref next).ToString(System.Globalization.CultureInfo.InvariantCulture);

        MapClicks(app, configuration, members);

        app.MapPost("/{tenant}/oauth2/v2.0/token", async (string tenant, HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();

            if (form["grant_type"] != "client_credentials"
                || string.IsNullOrEmpty(form["client_id"])
                || string.IsNullOrEmpty(form["client_secret"])
                || form["scope"] != "https://api.botframework.com/.default")
            {
                Console.WriteLine($"TEAMS token REFUSED for tenant {tenant}: the request is not the shape Microsoft accepts");

                return Results.Json(
                    new { error = "invalid_request", error_description = "AADSTS900144: the request body is incomplete." },
                    statusCode: 400);
            }

            Console.WriteLine($"TEAMS token issued for tenant {tenant}");

            return Results.Json(new { token_type = "Bearer", expires_in = 3600, access_token = Token });
        });

        var teams = app.MapGroup("/teams/v3/conversations").AddEndpointFilter(async (ctx, nextFilter) =>
        {
            var header = ctx.HttpContext.Request.Headers.Authorization.ToString();

            return header == $"Bearer {Token}"
                ? await nextFilter(ctx)
                : Results.Json(new { error = new { code = "Unauthorized", message = "no valid token" } }, statusCode: 401);
        });

        teams.MapPost("", async (HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);

            if (body?["isGroup"]?.GetValue<bool>() == true)
            {
                var channel = body["channelData"]?["channel"]?["id"]?.GetValue<string>();

                if (string.IsNullOrEmpty(channel))
                {
                    return Refuse(400, "BadSyntax", "no channel named");
                }

                var id = NextId();
                var conversation = $"{channel};messageid={id}";

                messages[id] = new Message(id, conversation, "channel") { Activity = body["activity"]?.DeepClone() };

                Console.WriteLine($"TEAMS posted {id} to channel {channel}");

                return Results.Json(new { id = conversation, activityId = id }, statusCode: 201);
            }

            var user = body?["members"]?[0]?["id"]?.GetValue<string>();

            if (string.IsNullOrEmpty(user))
            {
                return Refuse(400, "BadSyntax", "no member named");
            }

            Console.WriteLine($"TEAMS opened the personal chat with {user}");

            return Results.Json(new { id = $"a:{user}" }, statusCode: 201);
        });

        teams.MapGet("/{conversation}/pagedmembers", (string conversation) =>
        {
            Console.WriteLine($"TEAMS listed the members of {conversation}");

            return Results.Json(new
            {
                continuationToken = (string?)null,
                members = members.Select(m => new
                {
                    id = $"29:{m.Split('@')[0]}",
                    aadObjectId = ObjectId(m),
                    name = m.Split('@')[0],
                    email = m,
                    userPrincipalName = m,
                }),
            });
        });

        teams.MapPost("/{conversation}/activities", async (string conversation, HttpContext ctx) =>
        {
            if (!Wellformed(conversation))
            {
                return Refuse(400, "BadSyntax", "Bad format of conversation ID");
            }

            var id = NextId();

            messages[id] = new Message(id, conversation, "chat") { Activity = await ReadAsync(ctx) };

            Console.WriteLine($"TEAMS sent {id} into {conversation}");

            return Results.Json(new { id }, statusCode: 201);
        });

        teams.MapPut("/{conversation}/activities/{id}", async (string conversation, string id, HttpContext ctx) =>
        {
            if (!Wellformed(conversation))
            {
                return Refuse(400, "BadSyntax", "Bad format of conversation ID");
            }

            if (!messages.TryGetValue(id, out var message) || message.Conversation != conversation)
            {
                return Refuse(404, "NotFound", "no such message in this conversation");
            }

            message.Activity = await ReadAsync(ctx);
            message.Edits++;
            message.EditedAt = DateTimeOffset.UtcNow;

            Console.WriteLine($"TEAMS edited {id} (edit {message.Edits})");

            return Results.Json(new { id });
        });

        // Refused and counted, never performed. In Teams this would leave a line behind that
        // nothing can remove; here it leaves a number the harness asserts is zero.
        teams.MapDelete("/{conversation}/activities/{id}", (string conversation, string id) =>
        {
            Interlocked.Increment(ref deletes);
            Console.WriteLine($"TEAMS REFUSED a delete of {id} in {conversation}");

            return Refuse(405, "MethodNotAllowed", "the agent must never delete a message");
        });

        // What somebody looking at Teams would see now. Not authenticated: it is the harness's
        // window, not part of the thing being imitated.
        app.MapGet("/teams/messages", () => Results.Json(new
        {
            deletes = Volatile.Read(ref deletes),
            messages = messages.Values
                .OrderBy(m => int.Parse(m.Id, System.Globalization.CultureInfo.InvariantCulture))
                .Select(m => new
                {
                    id = m.Id,
                    conversation = m.Conversation,
                    kind = m.Kind,
                    edits = m.Edits,
                    postedAt = m.PostedAt,
                    editedAt = m.EditedAt,
                    summary = m.Activity?["summary"]?.GetValue<string>(),
                    text = Text(m.Activity),
                    activity = m.Activity,
                }),
        }));

        app.MapDelete("/teams/messages", () =>
        {
            messages.Clear();
            Interlocked.Exchange(ref deletes, 0);

            return Results.NoContent();
        });
    }

    /// <summary>
    /// The Bot Framework's side of a click: a key document, and a signer that delivers.
    /// </summary>
    private static void MapClicks(WebApplication app, IConfiguration configuration, string[] members)
    {
        // Generated at startup and never persisted: a restart is a key rotation, which the agent
        // has to survive by re-reading the document - as it would Microsoft's.
        var teamsKey = RSA.Create(2048);
        var otherKey = RSA.Create(2048);

        var appId = configuration["TEAMS_APP_ID"] ?? "00000000-0000-0000-0000-0000000000d2";
        var tenantId = configuration["TEAMS_TENANT_ID"] ?? "00000000-0000-0000-0000-0000000000d1";
        var actionsUrl = configuration["AGENT_ACTIONS_URL"] ?? "http://hephaisto.hephaisto:8082/api/teams/messages";

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        app.MapGet("/teams/openid/.well-known/openidconfiguration", (HttpContext ctx) => Results.Json(new
        {
            issuer = "https://api.botframework.com",
            authorization_endpoint = "https://invalid.example/unused",
            jwks_uri = $"{ctx.Request.Scheme}://{ctx.Request.Host}/teams/openid/keys",
            id_token_signing_alg_values_supported = new[] { "RS256" },
            token_endpoint_auth_methods_supported = new[] { "private_key_jwt" },
        }));

        app.MapGet("/teams/openid/keys", () => Results.Text(
            new JsonObject
            {
                ["keys"] = new JsonArray(Jwk(teamsKey, "stand-in-msteams", "msteams"), Jwk(otherKey, "stand-in-webchat", "webchat")),
            }.ToJsonString(),
            "application/json"));

        // {incidentId, verb, user, reason?, actionId?, tenant?, appId?, endorse?, serviceUrl?,
        //  claimedServiceUrl?}
        // user is an email: a member of the team, or anybody else. reason is what the person typed
        // into the Close card's Input.Text, which Teams merges into the button's data under the
        // input's id; actionId is what an Approve or Deny button carries. endorse=false signs with
        // the key endorsed for another channel. Answers {status, body, objectId} - what the agent
        // said, and the Entra object id the click was sent as.
        app.MapPost("/teams/click", async (HttpContext ctx) =>
        {
            var request = await ReadAsync(ctx);
            string? Field(string name) => request?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

            var user = Field("user") ?? members.FirstOrDefault() ?? "oncall@example.com";
            var serviceUrl = Field("serviceUrl") ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}/teams";
            var endorsed = request?["endorse"] is not JsonValue e || !e.TryGetValue<bool>(out var endorse) || endorse;

            var token = Sign(
                endorsed ? teamsKey : otherKey,
                endorsed ? "stand-in-msteams" : "stand-in-webchat",
                new JsonObject
                {
                    ["iss"] = "https://api.botframework.com",
                    ["aud"] = Field("appId") ?? appId,
                    ["serviceurl"] = Field("claimedServiceUrl") ?? serviceUrl,
                    ["nbf"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
                    ["exp"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
                });

            var activity = new JsonObject
            {
                ["type"] = "invoke",
                ["name"] = "adaptiveCard/action",
                ["channelId"] = "msteams",
                ["serviceUrl"] = serviceUrl,
                ["from"] = new JsonObject
                {
                    ["id"] = $"29:{user.Split('@')[0]}",
                    ["aadObjectId"] = ObjectId(user),
                    // Deliberately not the roster's name: the agent must record who the roster
                    // says this is, never what the click claims.
                    ["name"] = "Whoever The Click Says",
                },
                ["conversation"] = new JsonObject { ["id"] = $"a:{user.Split('@')[0]}" },
                ["channelData"] = new JsonObject { ["tenant"] = new JsonObject { ["id"] = Field("tenant") ?? tenantId } },
                ["value"] = new JsonObject
                {
                    ["action"] = new JsonObject
                    {
                        ["type"] = "Action.Execute",
                        ["verb"] = Field("verb") ?? "acknowledge",
                        ["data"] = Data(Field("incidentId"), Field("reason"), Field("actionId")),
                    },
                },
            };

            using var post = new HttpRequestMessage(HttpMethod.Post, actionsUrl)
            {
                Content = new StringContent(activity.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            post.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            try
            {
                using var answer = await http.SendAsync(post, ctx.RequestAborted);
                var body = await answer.Content.ReadAsStringAsync(ctx.RequestAborted);

                Console.WriteLine($"TEAMS click {Field("verb")} by {user} -> {(int)answer.StatusCode}");

                return Results.Json(new
                {
                    status = (int)answer.StatusCode,
                    body = string.IsNullOrWhiteSpace(body) ? null : SafeParse(body),
                    objectId = ObjectId(user),
                });
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { status = 0, body = (JsonNode?)JsonValue.Create(ex.Message) }, statusCode: 502);
            }
        });
    }

    /// <summary>
    /// What Teams sends as <c>action.data</c>: the button's own data, with the card's inputs merged
    /// in under their ids. A field nobody gave is absent, as it would be from a real card.
    /// </summary>
    private static JsonObject Data(string? incidentId, string? reason, string? actionId)
    {
        var data = new JsonObject { ["incidentId"] = incidentId };

        if (reason is not null)
        {
            data["reason"] = reason;
        }

        if (actionId is not null)
        {
            data["actionId"] = actionId;
        }

        return data;
    }

    /// <summary>A member's Entra object id: stable per address, and different for everybody else.</summary>
    private static string ObjectId(string email) =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()))[..16]).ToString();

    private static JsonObject Jwk(RSA key, string kid, string channel)
    {
        var p = key.ExportParameters(false);

        return new JsonObject
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["kid"] = kid,
            ["n"] = Base64Url(p.Modulus!),
            ["e"] = Base64Url(p.Exponent!),
            ["endorsements"] = new JsonArray(channel),
        };
    }

    /// <summary>RS256 by hand: the stand-in carries no token library, and needs none for one algorithm.</summary>
    private static string Sign(RSA key, string kid, JsonObject claims)
    {
        var header = new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = kid };
        var unsigned = $"{Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString()))}.{Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()))}";
        var signature = key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{unsigned}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonNode? SafeParse(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return JsonValue.Create(body);
        }
    }

    /// <summary>
    /// Every string a card would show, in order, so an assertion can be a grep.
    /// </summary>
    private static string Text(JsonNode? activity)
    {
        var found = new List<string>();

        Walk(activity);

        return string.Join(" | ", found);

        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (key is "text" or "title" or "value" && value is JsonValue v && v.TryGetValue<string>(out var s))
                        {
                            found.Add(s);
                        }
                        else
                        {
                            Walk(value);
                        }
                    }

                    break;

                case JsonArray a:
                    foreach (var item in a)
                    {
                        Walk(item);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// A channel message is <c>19:...@thread.tacv2;messageid=N</c> and a chat is <c>a:...</c>.
    /// An id that lost everything after its ';' to an unescaped path is neither.
    /// </summary>
    private static bool Wellformed(string conversation) =>
        conversation.StartsWith("a:", StringComparison.Ordinal)
        || (conversation.StartsWith("19:", StringComparison.Ordinal)
            && conversation.Contains(";messageid=", StringComparison.Ordinal));

    private static IResult Refuse(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);

    private static async Task<JsonNode?> ReadAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();

        try
        {
            return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
