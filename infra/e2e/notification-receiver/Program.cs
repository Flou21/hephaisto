using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

// The e2e harness's outbound receiver.
//
// It exists so the notification path can be asserted end to end without a third-party
// account: the agent posts here, and the harness reads back exactly what arrived.
//
// It is NOT called a "sink". In this repository ISignalSink is the INBOUND seam behind the
// Alertmanager webhook, and reusing the word for the opposite direction is how somebody
// later reads one thing and applies it to the other.
//
// Everything is in memory and single-replica on purpose. Persistence would be a second
// thing that can fail in a component whose entire job is to be the trustworthy half of an
// assertion.

var builder = WebApplication.CreateBuilder(args);

var received = new ConcurrentQueue<JsonObject>();

// The switch the restart test turns. While true every delivery is refused with 503, which
// the agent must classify as retryable and keep in its outbox rather than discard.
var failing = false;

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapPost("/hooks/hephaisto", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    if (failing)
    {
        // Recorded even when refused, so the harness can prove the agent DID try during the
        // outage rather than only that it succeeded afterwards.
        Console.WriteLine($"REFUSED (503) delivery {Header(ctx, "X-Hephaisto-Delivery-Id")}");
        return Results.StatusCode(503);
    }

    var entry = new JsonObject
    {
        ["deliveryId"] = Header(ctx, "X-Hephaisto-Delivery-Id"),
        ["event"] = Header(ctx, "X-Hephaisto-Event"),
        ["signature"] = Header(ctx, "X-Hephaisto-Signature"),
        ["receivedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        ["body"] = SafeParse(body),
    };

    received.Enqueue(entry);

    Console.WriteLine($"ACCEPTED delivery {entry["deliveryId"]} ({entry["event"]})");

    return Results.Accepted();
});

// What arrived, newest last. The harness asserts over this.
app.MapGet("/received", () => Results.Text(
    new JsonArray([.. received.Select(e => (JsonNode)e.DeepClone())]).ToJsonString(),
    "application/json"));

app.MapGet("/received/count", () => Results.Ok(received.Count));

app.MapDelete("/received", () =>
{
    received.Clear();
    return Results.NoContent();
});

// POST /mode/fail then /mode/ok. Deliberately a verb rather than a config value: the point of
// the test is that the outage starts and ends while the agent is running.
app.MapPost("/mode/{mode}", (string mode) =>
{
    failing = string.Equals(mode, "fail", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"mode set to {(failing ? "FAILING (503)" : "OK")}");

    return Results.Ok(new { failing });
});

app.Run();

static string Header(HttpContext ctx, string name) =>
    ctx.Request.Headers.TryGetValue(name, out var v) ? v.ToString() : string.Empty;

// A body that is not JSON is still worth recording - it is evidence about what the agent
// actually sent, which is the whole point of this service.
static JsonNode? SafeParse(string body)
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
