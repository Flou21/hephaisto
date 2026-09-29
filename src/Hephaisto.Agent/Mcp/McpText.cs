using System.Text.Json;
using System.Text.Json.Serialization;
using Hephaisto.Core.Safety;

namespace Hephaisto.Agent.Mcp;

/// <summary>
/// A string in an MCP answer that somebody other than Hephaisto may have written: an annotation,
/// a log line, a hypothesis, a title - or a name, which goes out plain only when it looks like one.
/// </summary>
/// <remarks>
/// The only way to put such text into a result record (<c>Hephaisto.Agent.Mcp.Results</c>) is
/// through this type, whose factories run <see cref="UntrustedText"/>; a plain <c>string</c>
/// there must say it is Hephaisto's own words with <see cref="ServerAuthoredAttribute"/>, or the
/// call fails when it is serialized. A field somebody forgets is a failed call, not a leak.
/// </remarks>
[JsonConverter(typeof(McpTextConverter))]
public sealed record McpText
{
    private McpText(string value) => Value = value;

    /// <summary>What is written: an envelope, or a plain name.</summary>
    public string Value { get; }

    /// <summary>Redacted, cleaned, escaped, cut and inside <c>&lt;untrusted-evidence&gt;</c>.</summary>
    public static McpText Untrusted(string? text, int maxChars = 2_000) => new(UntrustedText.Wrap(text, maxChars));

    public static McpText? UntrustedOrNull(string? text, int maxChars = 2_000) =>
        string.IsNullOrEmpty(text) ? null : Untrusted(text, maxChars);

    /// <summary>A namespace, an alert name, a workload, a person: plain when it looks like one.</summary>
    public static McpText Name(string? value) => new(UntrustedText.Name(value));

    public static McpText? NameOrNull(string? value) => string.IsNullOrEmpty(value) ? null : Name(value);

    public override string ToString() => Value;
}

/// <summary>Labels or annotations: every key a name, every value untrusted text.</summary>
public sealed class McpMap : Dictionary<string, McpText>
{
    private McpMap()
        : base(StringComparer.Ordinal)
    {
    }

    public static McpMap From(IReadOnlyDictionary<string, string>? values, int maxValueChars = 1_000)
    {
        var map = new McpMap();

        foreach (var (key, value) in values ?? new Dictionary<string, string>())
        {
            map[UntrustedText.Name(key, 128)] = UntrustedText.Name(value, 128) == value
                ? McpText.Name(value)
                : McpText.Untrusted(value, maxValueChars);
        }

        return map;
    }
}

/// <summary>Marks a string in an MCP result as Hephaisto's own words: an id, a hint, a reason it wrote.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ServerAuthoredAttribute : Attribute;

internal sealed class McpTextConverter : JsonConverter<McpText>
{
    public override McpText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("An MCP answer is written, never read back.");

    public override void Write(Utf8JsonWriter writer, McpText value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
