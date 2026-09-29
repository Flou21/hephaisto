using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Hephaisto.Core.Safety;

namespace Hephaisto.Agent.Mcp;

/// <summary>
/// How every MCP tool answers: one compact JSON document as one text block, under a size budget.
/// </summary>
/// <remarks>
/// <para>
/// Not structured content. A gateway forwards text reliably and a model reads JSON well; a second,
/// schema-typed copy of every answer would double what counts against the budget. Enums are their
/// names, as in the REST API, and absent values are left out rather than written as null. The
/// encoder leaves <c>&lt;</c> and <c>&gt;</c> alone, so the <c>&lt;untrusted-evidence&gt;</c> envelope
/// reaches a model as the tag it is.
/// </para>
/// <para>
/// <b>The result records are guarded.</b> A <c>string</c> property of a type in
/// <c>Hephaisto.Agent.Mcp.Answers</c> must carry <see cref="ServerAuthoredAttribute"/>; anything
/// else is <see cref="McpText"/>. The resolver refuses the type otherwise, so the call fails.
/// </para>
/// <para>
/// <b>The budget.</b> A gateway cuts a result at a fixed length, and a result cut mid-JSON is
/// worse than none. <see cref="Fit"/> shortens the longest string, then the longest list, until
/// the answer fits - and every cut says what it cut.
/// </para>
/// </remarks>
public static class McpAnswer
{
    public const string AnswersNamespace = "Hephaisto.Agent.Mcp.Answers";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RefusePlainStrings } },
    };

    public static string Of(object value) => JsonSerializer.Serialize(value, Json);

    /// <summary>
    /// The answer, shortened until it is at most <paramref name="budget"/> characters. Text that
    /// is not a JSON object is cut plainly, with the marker.
    /// </summary>
    public static string Fit(string text, int budget)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length <= budget)
        {
            return text;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is not JsonObject obj)
        {
            return UntrustedText.Shorten(text, Math.Max(0, budget - 40));
        }

        var omitted = 0;

        for (var round = 0; round < 400 && text.Length > budget; round++)
        {
            var over = text.Length - budget;

            if (Longest(obj) is { } longest && longest.Length > 400)
            {
                var keep = Math.Max(200, Math.Min(longest.Length / 2, longest.Length - over - 60));
                longest.Replace(UntrustedText.Shorten(longest.Value, keep));
            }
            else if (LongestArray(obj) is { Count: > 1 } list)
            {
                // As many items as the overshoot needs, at once: one per round would take
                // thousands of rounds for a long list of small items.
                var each = Math.Max(1, list.ToJsonString(Json).Length / list.Count);
                var drop = Math.Clamp((over / each) + 1, 1, list.Count - 1);

                for (var i = 0; i < drop; i++)
                {
                    list.RemoveAt(list.Count - 1);
                }

                omitted += drop;
                obj["omitted"] = $"{omitted} list item(s) left out to stay under {budget} characters; ask for fewer or page on.";
            }
            else
            {
                break;
            }

            text = obj.ToJsonString(Json);
        }

        return text.Length <= budget ? text : UntrustedText.Shorten(text, Math.Max(0, budget - 40));
    }

    private sealed record StringSlot(string Value, Action<string> Replace)
    {
        public int Length => Value.Length;
    }

    private static StringSlot? Longest(JsonNode? node)
    {
        StringSlot? best = null;

        void Visit(JsonNode? n, Action<string> replace)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToList())
                    {
                        Visit(o[key], v => o[key] = v);
                    }

                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        var index = i;
                        Visit(a[index], v => a[index] = v);
                    }

                    break;
                case JsonValue v when v.TryGetValue<string>(out var s):
                    if (best is null || s.Length > best.Length)
                    {
                        best = new StringSlot(s, replace);
                    }

                    break;
            }
        }

        Visit(node, _ => { });
        return best;
    }

    private static JsonArray? LongestArray(JsonNode? node)
    {
        JsonArray? best = null;
        var bestSize = 0;

        void Visit(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var p in o)
                    {
                        Visit(p.Value);
                    }

                    break;
                case JsonArray a:
                    var size = a.ToJsonString().Length;
                    if (a.Count > 1 && size > bestSize)
                    {
                        best = a;
                        bestSize = size;
                    }

                    foreach (var item in a)
                    {
                        Visit(item);
                    }

                    break;
            }
        }

        Visit(node);
        return best;
    }

    private static void RefusePlainStrings(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || info.Type.Namespace != AnswersNamespace)
        {
            return;
        }

        foreach (var property in info.Properties)
        {
            var type = property.PropertyType;
            var plain = type == typeof(string)
                || (type != typeof(McpMap) && typeof(IEnumerable<string>).IsAssignableFrom(type) && type != typeof(string));

            if (!plain)
            {
                continue;
            }

            var authored = property.AttributeProvider is ICustomAttributeProvider provider
                && provider.IsDefined(typeof(ServerAuthoredAttribute), inherit: true);

            if (!authored)
            {
                throw new InvalidOperationException(
                    $"{info.Type.Name}.{property.Name} is a plain string in an MCP answer. Make it McpText, "
                    + "or mark it [ServerAuthored] if Hephaisto wrote every character of it.");
            }
        }
    }
}
