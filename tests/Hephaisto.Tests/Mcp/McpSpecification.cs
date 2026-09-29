using System.Text.Json;

namespace Hephaisto.Tests.Mcp;

/// <summary>
/// The reviewed MCP tool list and the files that go with it, as the tests read them.
/// </summary>
/// <remarks>
/// <c>scripts/e2e/mcp/</c> is the specification: <c>tools.golden.json</c> (names, order, who may
/// call each, descriptions), <c>findability.tsv</c> (what a gateway's tool search must find) and
/// <c>neighbour-tools.json</c> (other servers' tools a gateway lists beside them). The pager
/// suite, the gateway tier and these tests all read the same three files.
/// </remarks>
internal static class McpSpecification
{
    public sealed record Tool(string Name, string Needs, bool ReadOnly, string Description);

    public static IReadOnlyList<Tool> Golden() => Tools(Path.Combine(Dir(), "tools.golden.json"));

    public static IReadOnlyList<Tool> Neighbours() => Tools(Path.Combine(Dir(), "neighbour-tools.json"));

    public static IReadOnlyList<(string Query, string Tool)> Findability() =>
        [.. File.ReadLines(Path.Combine(Dir(), "findability.tsv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .Select(p => (p[0], p[1]))];

    /// <summary>
    /// The tool search of the MCP gateway in front of production, as measured: every tool is
    /// indexed as <c>&lt;server&gt;-&lt;tool&gt; &lt;description&gt;</c>, lowercased; a query is
    /// split on whitespace, lowercased, and a tool scores one per query word that occurs in its
    /// index as a substring. Zero scores are dropped, ties keep registry order, the top
    /// <paramref name="top"/> are returned.
    /// </summary>
    public static IReadOnlyList<string> Search(string query, IEnumerable<(string Server, Tool Tool)> registry, int top = 5)
    {
        var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return [.. registry
            .Select((entry, order) =>
            {
                var name = $"{entry.Server}-{entry.Tool.Name}";
                var index = $"{name} {entry.Tool.Description}".ToLowerInvariant();
                return (name, order, score: words.Count(w => index.Contains(w, StringComparison.Ordinal)));
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.order)
            .Take(top)
            .Select(x => x.name)];
    }

    public static string Dir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "scripts", "e2e", "mcp");
    }

    private static List<Tool> Tools(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        return [.. doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => new Tool(
            t.GetProperty("name").GetString()!,
            t.TryGetProperty("needs", out var needs) ? needs.GetString()! : "reader",
            !t.TryGetProperty("readOnly", out var ro) || ro.GetBoolean(),
            t.GetProperty("description").GetString()!))];
    }
}
