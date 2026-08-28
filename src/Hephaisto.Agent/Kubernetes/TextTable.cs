using System.Globalization;
using System.Text;

namespace Hephaisto.Agent.Kubernetes;

/// <summary>
/// Renders tool results as fixed-width text tables.
/// </summary>
/// <remarks>
/// <para>
/// Not JSON, and the difference is not cosmetic. The JSON of a single pod spec is several
/// thousand tokens of <c>terminationMessagePolicy</c>, <c>dnsPolicy</c> and
/// <c>schedulerName</c>, none of which has ever explained a failure; a ten-step investigation
/// reading pods at every step spends most of its context window on punctuation. A table also
/// puts the same field at the same place on every row, which is what lets a model compare
/// twenty pods instead of describing one.
/// </para>
/// <para>
/// The empty case is a sentence, never <c>[]</c>. "No pods matched" is a finding; an empty
/// bracket pair reads as a tool that did not work, and the model's next move is to call it
/// again with different arguments.
/// </para>
/// </remarks>
public static class TextTable
{
    /// <summary>Beyond this a cell is truncated. Long enough for an image reference with a tag.</summary>
    private const int MaxCellWidth = 96;

    public static string Render(
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<string?>> rows,
        string emptyMessage,
        int maxRows = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var materialised = rows.ToList();
        if (materialised.Count == 0)
        {
            return emptyMessage;
        }

        var shown = materialised.Count <= maxRows ? materialised : materialised.GetRange(0, maxRows);

        var widths = new int[headers.Count];
        for (var i = 0; i < headers.Count; i++)
        {
            widths[i] = headers[i].Length;
        }

        var cells = new List<string[]>(shown.Count);
        foreach (var row in shown)
        {
            var line = new string[headers.Count];
            for (var i = 0; i < headers.Count; i++)
            {
                line[i] = Cell(i < row.Count ? row[i] : null);
                widths[i] = Math.Max(widths[i], line[i].Length);
            }

            cells.Add(line);
        }

        var sb = new StringBuilder();
        AppendRow(sb, headers.Select(h => h).ToArray(), widths);
        AppendRow(sb, widths.Select(w => new string('-', w)).ToArray(), widths);

        foreach (var line in cells)
        {
            AppendRow(sb, line, widths);
        }

        if (materialised.Count > shown.Count)
        {
            sb.Append(CultureInfo.InvariantCulture, $"... {materialised.Count - shown.Count} more rows not shown ({materialised.Count} total)\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Kubernetes-style compact age: <c>3d4h</c>, <c>17m</c>. Absolute timestamps make a model
    /// do date arithmetic, and "how long has this been broken" is the question being asked.
    /// </summary>
    public static string Age(DateTime? from, DateTimeOffset now)
    {
        if (from is null)
        {
            return "<unknown>";
        }

        return Age(new DateTimeOffset(DateTime.SpecifyKind(from.Value, DateTimeKind.Utc)), now);
    }

    public static string Age(DateTimeOffset from, DateTimeOffset now)
    {
        var span = now - from;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span switch
        {
            { TotalDays: >= 1 } => $"{(int)span.TotalDays}d{span.Hours}h",
            { TotalHours: >= 1 } => $"{(int)span.TotalHours}h{span.Minutes}m",
            { TotalMinutes: >= 1 } => $"{(int)span.TotalMinutes}m",
            _ => $"{(int)span.TotalSeconds}s",
        };
    }

    public static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var flat = value.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : string.Concat(flat.AsSpan(0, max - 1), "…");
    }

    private static string Cell(string? value) => Truncate(value, MaxCellWidth);

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> values, int[] widths)
    {
        for (var i = 0; i < values.Count; i++)
        {
            sb.Append(values[i]);

            // No trailing padding on the last column: it is pure waste in every row of every
            // table, and it adds up across an investigation.
            if (i < values.Count - 1)
            {
                sb.Append(new string(' ', widths[i] - values[i].Length + 2));
            }
        }

        sb.Append('\n');
    }
}
