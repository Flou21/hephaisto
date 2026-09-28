using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotificationReceiver;

// A stand-in for the model, as the agent's openai-compatible client sees it.
//
// It exists for the pager suite (scripts/e2e/pager), whose assertions are about WHO WAS TOLD
// WHAT AND WHEN - not about how good a diagnosis is. A real model makes those assertions slow,
// paid and different every run; this one answers every investigation the same way, at once or
// when told to, and keeps every request it was sent so a scenario can read back what the agent
// asked: which tools it offered, what the prompt said, and whether it asked at all.
//
// It is not a model of a model. Every answer is one `conclude` call whose single finding cites
// nothing, so the grounding verifier discards it and the incident escalates. That is the one
// outcome every scenario can rely on without depending on a model's judgement.
//
//   GET    /v1/models                 one model, "stand-in"
//   POST   /v1/chat/completions       conclude, after the configured delay or hold
//   POST   /v1/embeddings             a deterministic unit vector per input
//   GET    /llm/requests              every chat request, newest last, with the tool names
//   DELETE /llm/requests              forget them
//   POST   /llm/delay/{ms}            answer after ms milliseconds (0 = at once)
//   POST   /llm/hold                  answer nothing until /llm/release, capped at five minutes
//   POST   /llm/release               answer everything held, and stop holding
public static class LlmStandIn
{
    private const int Kept = 500;

    public static void Map(WebApplication app)
    {
        var requests = new ConcurrentQueue<JsonObject>();
        var delayMs = 0;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        held.SetResult();
        var gate = new object();

        app.MapGet("/v1/models", () => Results.Json(new
        {
            @object = "list",
            data = new[] { new { id = "stand-in", @object = "model", owned_by = "hephaisto-e2e" } },
        }));

        app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);
            var tools = (body?["tools"] as JsonArray ?? [])
                .Select(t => t?["function"]?["name"]?.GetValue<string>())
                .Where(n => n is not null)
                .ToArray();

            var record = new JsonObject
            {
                ["at"] = DateTimeOffset.UtcNow.ToString("O"),
                ["model"] = body?["model"]?.DeepClone(),
                ["tools"] = new JsonArray([.. tools.Select(n => (JsonNode?)JsonValue.Create(n))]),
                ["text"] = Text(body?["messages"]),
            };

            requests.Enqueue(record);

            while (requests.Count > Kept)
            {
                requests.TryDequeue(out _);
            }

            Task hold;
            lock (gate)
            {
                hold = held.Task;
            }

            await Task.WhenAny(hold, Task.Delay(TimeSpan.FromMinutes(5), ctx.RequestAborted));

            var delay = Volatile.Read(ref delayMs);
            if (delay > 0)
            {
                await Task.Delay(delay, ctx.RequestAborted);
            }

            Console.WriteLine($"LLM answered a request offering {tools.Length} tools");

            return Results.Json(Answer(body, tools));
        });

        app.MapPost("/v1/embeddings", async (HttpContext ctx) =>
        {
            var body = await ReadAsync(ctx);
            var dimensions = body?["dimensions"]?.GetValue<int>() ?? 768;
            var inputs = body?["input"] switch
            {
                JsonArray a => a.Select(i => i?.ToString() ?? string.Empty).ToArray(),
                JsonNode n => [n.ToString()],
                _ => [string.Empty],
            };

            return Results.Json(new
            {
                @object = "list",
                model = body?["model"]?.ToString() ?? "stand-in",
                data = inputs.Select((input, i) => new { @object = "embedding", index = i, embedding = Vector(input, dimensions) }),
                usage = new { prompt_tokens = inputs.Sum(i => i.Length / 4 + 1), total_tokens = inputs.Sum(i => i.Length / 4 + 1) },
            });
        });

        app.MapGet("/llm/requests", () => Results.Text(
            new JsonArray([.. requests.Select(r => (JsonNode)r.DeepClone())]).ToJsonString(),
            "application/json"));

        app.MapDelete("/llm/requests", () =>
        {
            requests.Clear();
            return Results.NoContent();
        });

        app.MapPost("/llm/delay/{ms:int}", (int ms) =>
        {
            Volatile.Write(ref delayMs, Math.Max(0, ms));
            Console.WriteLine($"LLM delay set to {ms} ms");
            return Results.Ok(new { delayMs = ms });
        });

        app.MapPost("/llm/hold", () =>
        {
            lock (gate)
            {
                if (held.Task.IsCompleted)
                {
                    held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            Console.WriteLine("LLM holding every answer");
            return Results.Ok(new { holding = true });
        });

        app.MapPost("/llm/release", () =>
        {
            lock (gate)
            {
                held.TrySetResult();
            }

            Console.WriteLine("LLM released");
            return Results.Ok(new { holding = false });
        });
    }

    /// <summary>
    /// One <c>conclude</c> call when the agent offered it and none has been made yet; a line of
    /// text once one has, which is how a model ends its turn; and an empty JSON object when no
    /// tool was offered - what a planning call with a response schema gets, and which plans
    /// nothing.
    /// </summary>
    /// <remarks>
    /// The second case is not a nicety. The agent's tool-calling client answers a tool call by
    /// calling the model again with the result, and a stand-in that concluded every time it was
    /// asked would conclude until the step budget ran out - every investigation would end
    /// <c>StepBudgetExhausted</c>, and every scenario reading an escalation reason would be
    /// reading the harness.
    /// </remarks>
    private static object Answer(JsonNode? body, string?[] tools)
    {
        var model = body?["model"]?.ToString() ?? "stand-in";
        object message;
        string finish;

        var concluded = (body?["messages"] as JsonArray ?? [])
            .Any(m => (m?["tool_calls"] as JsonArray ?? [])
                .Any(c => c?["function"]?["name"]?.GetValue<string>() == "conclude"));

        if (concluded)
        {
            message = new { role = "assistant", content = "Concluded." };
            finish = "stop";
        }
        else if (tools.Contains("conclude"))
        {
            var arguments = new JsonObject
            {
                ["summary"] = "The model stand-in does not investigate. This conclusion is the pager suite's.",
                ["confidence"] = 0.1,
                ["findings"] = new JsonArray(new JsonObject
                {
                    ["category"] = "unknown",
                    ["hypothesis"] = "Nothing was investigated: this is the pager suite's model stand-in.",
                    ["confidence"] = 0.1,
                    ["primary"] = true,
                    ["evidence"] = new JsonArray(),
                }),
            };

            message = new
            {
                role = "assistant",
                content = (string?)null,
                tool_calls = new[]
                {
                    new
                    {
                        id = "call_" + Guid.NewGuid().ToString("N")[..12],
                        type = "function",
                        function = new { name = "conclude", arguments = arguments.ToJsonString() },
                    },
                },
            };
            finish = "tool_calls";
        }
        else
        {
            message = new { role = "assistant", content = "{}" };
            finish = "stop";
        }

        return new
        {
            id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..16],
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[] { new { index = 0, message, finish_reason = finish } },
            usage = new { prompt_tokens = 1000, completion_tokens = 100, total_tokens = 1100 },
        };
    }

    /// <summary>Every string content in the conversation, joined, so an assertion can be a grep.</summary>
    private static string Text(JsonNode? messages)
    {
        var sb = new StringBuilder();

        foreach (var m in messages as JsonArray ?? [])
        {
            switch (m?["content"])
            {
                case JsonValue v when v.TryGetValue<string>(out var s):
                    sb.AppendLine(s);
                    break;
                case JsonArray parts:
                    foreach (var p in parts)
                    {
                        if (p?["text"] is JsonValue t && t.TryGetValue<string>(out var s2))
                        {
                            sb.AppendLine(s2);
                        }
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>A unit vector derived from the input's hash: equal text, equal vector.</summary>
    private static float[] Vector(string input, int dimensions)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var random = new Random(BitConverter.ToInt32(seed, 0));
        var v = new float[Math.Clamp(dimensions, 1, 4096)];
        double norm = 0;

        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)(random.NextDouble() - 0.5);
            norm += v[i] * v[i];
        }

        var scale = (float)(1 / Math.Sqrt(norm));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] *= scale;
        }

        return v;
    }

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
