using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hephaisto.Agent.Mcp;

/// <summary>
/// How every MCP tool answers: one compact JSON document as one text block.
/// </summary>
/// <remarks>
/// Not structured content. A gateway forwards text reliably and a model reads JSON well; a second,
/// schema-typed copy of every answer would double what counts against the size budget. Enums are
/// their names, as in the REST API, and absent values are left out rather than written as null.
/// The encoder leaves <c>&lt;</c> and <c>&gt;</c> alone, so the <c>&lt;untrusted-evidence&gt;</c>
/// envelope reaches a model as the tag it is, not as <c><</c>.
/// </remarks>
public static class McpAnswer
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Of(object value) => JsonSerializer.Serialize(value, Json);
}
