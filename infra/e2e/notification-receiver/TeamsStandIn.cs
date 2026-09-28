using System.Collections.Concurrent;
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
