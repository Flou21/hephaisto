using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotificationReceiver;

// A second MCP server, known to work, that is not Hephaisto.
//
// Two jobs. The pager suite's P29 asks it first, so a red MCP scenario can be told apart from a
// broken driver: if the suite cannot list and call a tool here, nothing it says about the agent's
// endpoint means anything. And the gateway tier (scripts/e2e/mcp-litellm-local.sh) registers it
// beside the agent as `neighbours`, so tool search ranks Hephaisto's tools among others the way
// it will in production.
//
// It is the protocol at its smallest: JSON-RPC over POST, answered as one SSE event when the
// client accepts one and as plain JSON otherwise. Stateless - no session id is issued or read.
// The tools are scripts/e2e/mcp/neighbour-tools.json, copied into the image, and every call
// echoes its arguments back.
//
//   POST /decoy/mcp    initialize, notifications/*, ping, tools/list, tools/call
public static class DecoyMcp
{
    public static void Map(WebApplication app)
    {
        var tools = LoadTools();

        app.MapPost("/decoy/mcp", async (HttpContext ctx) =>
        {
            JsonNode? request;
            try
            {
                request = await JsonNode.ParseAsync(ctx.Request.Body);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            var method = request?["method"]?.GetValue<string>() ?? string.Empty;
            var id = request?["id"]?.DeepClone();

            // A notification has no id and gets no answer.
            if (id is null)
            {
                return Results.Accepted();
            }

            JsonObject result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = request?["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "neighbours", ["version"] = "1.0.0" },
                },
                "tools/list" => new JsonObject { ["tools"] = tools.DeepClone() },
                "tools/call" => new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = new JsonObject
                        {
                            ["tool"] = request?["params"]?["name"]?.DeepClone(),
                            ["arguments"] = request?["params"]?["arguments"]?.DeepClone(),
                        }.ToJsonString(),
                    }),
                },
                _ => [],
            };

            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

            if (ctx.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.Ordinal))
            {
                return Results.Text($"event: message\ndata: {response.ToJsonString()}\n\n", "text/event-stream");
            }

            return Results.Text(response.ToJsonString(), "application/json");
        });
    }

    private static JsonArray LoadTools()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "neighbour-tools.json");
        var doc = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        var tools = new JsonArray();

        foreach (var t in doc?["tools"] as JsonArray ?? [])
        {
            tools.Add(new JsonObject
            {
                ["name"] = t?["name"]?.DeepClone(),
                ["description"] = t?["description"]?.DeepClone(),
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } },
                },
            });
        }

        return tools;
    }
}
