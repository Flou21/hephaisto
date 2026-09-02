using System.Text;
using System.Text.RegularExpressions;

namespace Hephaisto.Core.Digest;

public sealed class LogDigestOptions
{
    /// <summary>How many repeated patterns to summarise. Past ten the tail is worth more.</summary>
    public int TopClusters { get; set; } = 10;

    /// <summary>The most recent lines, kept verbatim. What happened last is what happened.</summary>
    public int TailLines { get; set; } = 40;

    /// <summary>Lines either side of a notable line. A stack trace's first frames live here.</summary>
    public int ContextLines { get; set; } = 3;

    /// <summary>
    /// Roughly 2k tokens. Chosen so that a ten-step investigation reading logs at every step
    /// still fits its context window with room for the incident, the runbook and the plan.
    /// </summary>
    public int MaxBytes { get; set; } = 8 * 1024;

    public static LogDigestOptions Default { get; } = new();
}

/// <summary>
/// A group of lines that normalise to the same shape. <paramref name="FirstSeen"/> and
/// <paramref name="LastSeen"/> are the source lines' own timestamp text, kept verbatim rather
/// than parsed: container logs carry at least five timestamp formats and none of them matter
/// enough to justify parsing them all. Lines with no timestamp report their line number.
/// </summary>
public sealed record LogCluster(int Count, string FirstSeen, string LastSeen, string Exemplar);

public sealed record LogDigest(string Text, bool Truncated, int OmittedLineCount, int OriginalLineCount, int OriginalBytes);

/// <summary>
/// Turns a container log into something worth paying for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raw logs never reach the model.</b> Not as a cost optimisation first - though a
/// CrashLoopBackOff pod can emit several megabytes of the same line, which at ~4 characters per
/// token is a four-figure bill for one investigation - but because it is the difference between
/// an agent that can reason and one that cannot. Ten thousand copies of one message bury the
/// three lines that explain the fault, and a model given the whole thing attends to the bulk.
/// Collapsing repetition and preserving the notable lines is what makes the fault visible.
/// </para>
/// <para>
/// The digest is also the grounding surface: <c>InvestigationStep.ResultDigest</c> holds exactly
/// this text, and evidence is checked against it, because the model cannot honestly cite a line
/// it was never shown. The untruncated blob is kept separately for the audit trail.
/// </para>
/// <para>
/// Priority under the byte cap is notable lines, then the tail, then the repetition summary. A
/// digest that dropped the panic to make room for a hundred identical health-check lines would
/// be worse than no digest at all.
/// </para>
/// </remarks>
public static partial class LogDigester
{
    /// <summary>
    /// The words that mean something went wrong, in the vocabularies of the runtimes that
    /// actually run in this cluster. Deliberately broad: a false positive costs a few bytes,
    /// a false negative costs the diagnosis.
    /// </summary>
    [GeneratedRegex(@"panic|fatal|exception|OOM|refused|timeout|unauthorized|denied",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotablePattern();

    [GeneratedRegex(@"\x1B(?:\[[0-9;?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))",
        RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(
        @"^\s*\[?(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,9})?(?:Z|[+-]\d{2}:?\d{2})?|[A-Z][a-z]{2} +\d{1,2} +\d{2}:\d{2}:\d{2}(?:[.,]\d{1,9})?|\d{2}:\d{2}:\d{2}(?:[.,]\d{1,9})?)\]?\s*",
        RegexOptions.CultureInvariant)]
    private static partial Regex LeadingTimestamp();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();

    [GeneratedRegex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?::\d{1,5})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex IpAddress();

    [GeneratedRegex(@"\b\d+(?:\.\d+)?(?:ns|µs|us|ms|s|m|h)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Duration();

    /// <summary>
    /// Hex runs of eight or more, and only those containing a letter - a pure digit run is a
    /// number and reads better as one. Catches container ids, image digests and the hash in a
    /// ReplicaSet-generated pod name, which is the single biggest source of spurious variety.
    /// </summary>
    [GeneratedRegex(@"\b(?=[0-9a-fA-F]{8,}\b)[0-9a-fA-F]*[a-fA-F][0-9a-fA-F]*\b", RegexOptions.CultureInvariant)]
    private static partial Regex HexId();

    /// <summary>Four digits or more. Below that the number is usually a code or a count worth keeping.</summary>
    [GeneratedRegex(@"\b\d{4,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumber();

    private const int MaxExemplarLength = 300;

    /// <summary>Room for the trailing "N of M lines omitted" note, which must never be the thing cut.</summary>
    private const int FooterReserveBytes = 96;

    public static LogDigest Digest(string raw, LogDigestOptions options)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(options);

        var originalBytes = Encoding.UTF8.GetByteCount(raw);
        var lines = SplitLines(raw);

        if (lines.Length == 0)
        {
            return new LogDigest("log digest: 0 lines, 0 bytes", false, 0, 0, originalBytes);
        }

        var clean = new string[lines.Length];
        var body = new string[lines.Length];
        var normalised = new string[lines.Length];
        var stamps = new string[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            clean[i] = AnsiEscape().Replace(lines[i], string.Empty).TrimEnd();

            var match = LeadingTimestamp().Match(clean[i]);
            stamps[i] = match.Success ? match.Groups[1].Value : $"#{i + 1}";
            body[i] = match.Success ? clean[i][match.Length..] : clean[i];

            normalised[i] = Normalise(body[i]);
        }

        var clusters = BuildClusters(normalised, body, stamps, options.TopClusters);
        var notableIndices = NotableIndices(clean, options.ContextLines);
        var tailIndices = Enumerable
            .Range(Math.Max(0, lines.Length - options.TailLines), Math.Min(options.TailLines, lines.Length))
            .ToArray();

        return Assemble(clean, lines.Length, originalBytes, clusters, notableIndices, tailIndices, options);
    }

    /// <summary>
    /// The whole point of the digester. Everything that varies between two occurrences of the
    /// same event - the request id, the peer address, how long it took - becomes a placeholder,
    /// so ten thousand distinct strings become one line and a count.
    /// </summary>
    private static string Normalise(string line)
    {
        // Order matters: a UUID and an IPv4 address both contain runs the later patterns would
        // otherwise chew into pieces, and a duration's digits must be claimed before LongNumber.
        var text = Uuid().Replace(line, "<id>");
        text = IpAddress().Replace(text, "<ip>");
        text = Duration().Replace(text, "<dur>");
        text = HexId().Replace(text, "<id>");
        text = LongNumber().Replace(text, "<num>");
        return text.Trim();
    }

    private static IReadOnlyList<(LogCluster Cluster, int ExemplarIndex)> BuildClusters(
        string[] normalised,
        string[] body,
        string[] stamps,
        int topK)
    {
        var groups = new Dictionary<string, (int Count, int First, int Last)>(StringComparer.Ordinal);

        for (var i = 0; i < normalised.Length; i++)
        {
            if (normalised[i].Length == 0)
            {
                continue;
            }

            if (groups.TryGetValue(normalised[i], out var existing))
            {
                groups[normalised[i]] = (existing.Count + 1, existing.First, i);
            }
            else
            {
                groups[normalised[i]] = (1, i, i);
            }
        }

        return groups
            // A cluster of one is not a pattern, it is a line - and the tail or the notable
            // section will carry it verbatim if it matters.
            .Where(g => g.Value.Count > 1)
            .OrderByDescending(g => g.Value.Count)
            .ThenBy(g => g.Value.First)
            .Take(topK)
            .Select(g => (
                new LogCluster(
                    g.Value.Count,
                    stamps[g.Value.First],
                    stamps[g.Value.Last],
                    Shorten(body[g.Value.First])),
                g.Value.First))
            .ToArray();
    }

    private static int[] NotableIndices(string[] clean, int contextLines)
    {
        var selected = new SortedSet<int>();

        for (var i = 0; i < clean.Length; i++)
        {
            if (!NotablePattern().IsMatch(clean[i]))
            {
                continue;
            }

            var from = Math.Max(0, i - contextLines);
            var to = Math.Min(clean.Length - 1, i + contextLines);
            for (var j = from; j <= to; j++)
            {
                selected.Add(j);
            }
        }

        return [.. selected];
    }

    /// <summary>
    /// Inclusion is decided under the byte cap in priority order, then rendered in reading
    /// order. Doing it the other way round - rendering and then cutting - is what produces a
    /// digest whose last section is a half-line and whose panic never made it in.
    /// </summary>
    private static LogDigest Assemble(
        string[] clean,
        int originalLineCount,
        int originalBytes,
        IReadOnlyList<(LogCluster Cluster, int ExemplarIndex)> clusters,
        int[] notableIndices,
        int[] tailIndices,
        LogDigestOptions options)
    {
        var header = $"log digest: {originalLineCount} lines, {originalBytes} bytes";
        var budget = Math.Max(options.MaxBytes - FooterReserveBytes, 0);
        var used = Cost(header);

        var represented = new HashSet<int>();

        var notableOut = new List<string>();
        const string notableTitle = "-- notable lines --";
        foreach (var index in notableIndices)
        {
            var cost = Cost(clean[index]) + (notableOut.Count == 0 ? Cost(notableTitle) + 1 : 0);
            if (used + cost > budget)
            {
                break;
            }

            used += cost;
            notableOut.Add(clean[index]);
            represented.Add(index);
        }

        // Walked newest-first so that a squeezed budget costs the oldest of the last forty,
        // never the newest - the line immediately before the process died is the one that matters.
        var tailAccepted = new List<int>();
        var tailTitle = $"-- last {tailIndices.Length} lines --";
        for (var i = tailIndices.Length - 1; i >= 0; i--)
        {
            var index = tailIndices[i];
            // Charged even when the notable section already carried this line: the tail is
            // rendered contiguously, so the line really is written twice and really does cost
            // twice. It counts once towards `represented`, which is about coverage, not bytes.
            var cost = Cost(clean[index]) + (tailAccepted.Count == 0 ? Cost(tailTitle) + 1 : 0);

            if (used + cost > budget)
            {
                break;
            }

            used += cost;
            tailAccepted.Add(index);
            represented.Add(index);
        }

        tailAccepted.Reverse();
        var tailOut = tailAccepted.Select(i => clean[i]).ToList();

        var clusterOut = new List<string>();
        const string clusterTitle = "-- repeated patterns --";
        foreach (var (cluster, exemplarIndex) in clusters)
        {
            var summary = $"x{cluster.Count} [{cluster.FirstSeen} .. {cluster.LastSeen}] {cluster.Exemplar}";
            var cost = Cost(summary) + (clusterOut.Count == 0 ? Cost(clusterTitle) + 1 : 0);
            if (used + cost > budget)
            {
                break;
            }

            used += cost;
            clusterOut.Add(summary);

            // Only the exemplar is shown verbatim; the other N-1 lines of the cluster are
            // genuinely omitted, and the count says so.
            represented.Add(exemplarIndex);
        }

        var omitted = originalLineCount - represented.Count;
        var truncated = omitted > 0;

        var sb = new StringBuilder();
        sb.Append(header).Append('\n');
        AppendSection(sb, clusterTitle, clusterOut);
        AppendSection(sb, notableTitle, notableOut);
        AppendSection(sb, tailTitle, tailOut);

        if (truncated)
        {
            sb.Append('\n').Append($"[truncated: {omitted} of {originalLineCount} lines omitted]").Append('\n');
        }

        var text = sb.ToString();
        if (Encoding.UTF8.GetByteCount(text) > options.MaxBytes)
        {
            // Belt and braces. The accounting above should make this unreachable, but the cap is
            // a promise made to the context window and a promise that only usually holds is not one.
            text = ClampToBytes(text, options.MaxBytes);
            truncated = true;
        }

        return new LogDigest(text, truncated, omitted, originalLineCount, originalBytes);
    }

    private static void AppendSection(StringBuilder sb, string title, List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        sb.Append('\n').Append(title).Append('\n');
        foreach (var line in lines)
        {
            sb.Append(line).Append('\n');
        }
    }

    /// <summary>
    /// Downsamples a PromQL range to something a model can actually read, keeping the four
    /// numbers that carry the story. A model handed 1440 raw samples reports the shape of the
    /// last few it happened to attend to; handed min, max, last and delta it reports the shape.
    /// </summary>
    public static string DigestPromQlRange(
        string query,
        IReadOnlyList<SeriesPoint> points,
        int maxPoints = 50)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return $"query: {query}\nno data points";
        }

        var values = points.Select(p => p.Value).ToArray();
        var first = values[0];
        var last = values[^1];
        var delta = last - first;

        var sb = new StringBuilder();
        sb.Append("query: ").Append(query).Append('\n');

        var stride = Math.Max(1, (int)Math.Ceiling((double)points.Count / Math.Max(1, maxPoints)));
        var kept = new List<SeriesPoint>();
        for (var i = 0; i < points.Count; i += stride)
        {
            kept.Add(points[i]);
        }

        // The last sample is "how things are right now" and is never allowed to fall off the
        // end of the stride.
        if (kept[^1].At != points[^1].At)
        {
            kept.Add(points[^1]);
        }

        sb.Append($"points: {points.Count} sampled to {kept.Count}\n");
        sb.Append($"min={values.Min():G6} max={values.Max():G6} first={first:G6} last={last:G6} delta={delta:+0.######;-0.######;0}\n");

        foreach (var point in kept)
        {
            sb.Append(point.At.ToString("O")).Append("  ").Append(point.Value.ToString("G6")).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Strips the parts of a <c>describe</c> or object dump that are pure server bookkeeping.
    /// <c>managedFields</c> alone is routinely larger than everything a human would look at, and
    /// <c>last-applied-configuration</c> is a second full copy of the spec. Best-effort text
    /// mangling on purpose: it must degrade to "kept too much", never to "dropped the Events".
    /// </summary>
    public static string DigestDescribe(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var lines = SplitLines(raw);
        var sb = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (trimmed.StartsWith("managedFields:", StringComparison.OrdinalIgnoreCase))
            {
                i = SkipBlock(lines, i, indent);
                continue;
            }

            if (trimmed.StartsWith("resourceVersion:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.StartsWith("annotations:", StringComparison.OrdinalIgnoreCase))
            {
                // The key's own casing is preserved: `Annotations:` in describe output,
                // `annotations:` in YAML, and rewriting either one breaks a reader's expectations.
                var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
                var inline = trimmed[(colon + 1)..].Trim();
                sb.Append(line[..indent]).Append(trimmed[..(colon + 1)]);
                if (inline.Length > 0 && IsKeptAnnotation(inline))
                {
                    sb.Append(' ').Append(inline);
                }

                sb.Append('\n');

                // Continuation lines are everything indented further; keep only the few keys
                // that ever explain anything.
                var end = SkipBlock(lines, i, indent);
                for (var j = i + 1; j <= end; j++)
                {
                    var candidate = lines[j].TrimEnd();
                    if (IsKeptAnnotation(candidate.TrimStart()))
                    {
                        sb.Append(candidate).Append('\n');
                    }
                }

                i = end;
                continue;
            }

            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Annotations worth their bytes: the revision a Deployment is on, and why it changed.
    /// Everything else - especially <c>last-applied-configuration</c> - is dropped.
    /// </summary>
    private static bool IsKeptAnnotation(string text) =>
        text.Contains("deployment.kubernetes.io/revision", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("kubernetes.io/change-cause", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("hephaisto.dev/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the index of the last line belonging to the block opened at <paramref name="start"/>.</summary>
    private static int SkipBlock(string[] lines, int start, int indent)
    {
        var end = start;
        for (var j = start + 1; j < lines.Length; j++)
        {
            var candidate = lines[j];
            if (candidate.Trim().Length == 0)
            {
                end = j;
                continue;
            }

            var trimmed = candidate.TrimStart();
            var candidateIndent = candidate.Length - trimmed.Length;

            // A YAML sequence under a key is conventionally written at the key's own
            // indentation, so "same indent" is only the end of the block when the line is not
            // a list item - otherwise managedFields' entries would all survive the strip.
            if (candidateIndent < indent ||
                (candidateIndent == indent && !trimmed.StartsWith("- ", StringComparison.Ordinal)))
            {
                break;
            }

            end = j;
        }

        return end;
    }

    private static string[] SplitLines(string raw)
    {
        if (raw.Length == 0)
        {
            return [];
        }

        var split = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        // A trailing newline is punctuation, not a line.
        return split.Length > 1 && split[^1].Length == 0 ? split[..^1] : split;
    }

    private static string Shorten(string text) =>
        text.Length <= MaxExemplarLength ? text : string.Concat(text.AsSpan(0, MaxExemplarLength), "…");

    private static int Cost(string line) => Encoding.UTF8.GetByteCount(line) + 1;

    private static string ClampToBytes(string text, int maxBytes)
    {
        // Rune-wise, not char-wise: cutting between the halves of a surrogate pair produces a
        // string that is not valid UTF-8 and that no downstream consumer can parse.
        var used = 0;
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maxBytes)
            {
                return text[..index];
            }

            used += rune.Utf8SequenceLength;
            index += rune.Utf16SequenceLength;
        }

        return text;
    }
}

/// <summary>One sample of a PromQL range query.</summary>
public readonly record struct SeriesPoint(DateTimeOffset At, double Value);
