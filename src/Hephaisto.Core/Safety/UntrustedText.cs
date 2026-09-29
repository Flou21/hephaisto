using System.Text;
using System.Text.RegularExpressions;

namespace Hephaisto.Core.Safety;

/// <summary>
/// Text somebody else wrote, made safe to hand to a model as data.
/// </summary>
/// <remarks>
/// <para>
/// An incident is made of alert annotations, log lines, event messages and hypotheses - written
/// by a workload, an alert rule, or a model - and the MCP endpoint hands them to another model,
/// which may hold a shell. So every such field goes out the way the coder's request already sends
/// its evidence: redacted, stripped of the characters that hide text from a reader, escaped, cut
/// to a length, and inside <c>&lt;untrusted-evidence&gt;</c>. The tag is the promise a caller's
/// system prompt can rely on: what is inside was not written by Hephaisto.
/// </para>
/// <para>
/// Escaping is what makes the envelope hold: a log line containing
/// <c>&lt;/untrusted-evidence&gt;</c> arrives as <c>&amp;lt;/untrusted-evidence&amp;gt;</c> and cannot
/// close it.
/// </para>
/// </remarks>
public static partial class UntrustedText
{
    public const string Open = "<untrusted-evidence>";
    public const string Close = "</untrusted-evidence>";

    /// <summary>The marker a cut leaves, so a reader knows something is missing and how much.</summary>
    public static string CutMarker(int cut) => $" [... {cut} characters cut]";

    /// <summary>Redacted, cleaned, escaped, cut to <paramref name="maxChars"/> and enveloped.</summary>
    public static string Wrap(string? text, int maxChars)
    {
        var inner = Escape(Clean(SecretRedactor.Redact(text)));

        return Open + Cut(inner, maxChars) + Close;
    }

    /// <summary>Whether <paramref name="value"/> is an envelope <see cref="Wrap"/> made.</summary>
    public static bool IsWrapped(string? value) =>
        value is not null
        && value.StartsWith(Open, StringComparison.Ordinal)
        && value.EndsWith(Close, StringComparison.Ordinal);

    /// <summary>
    /// A name - a namespace, an alert name, a workload, a person - goes out plain when it looks
    /// like one, and enveloped when it does not: a "namespace" with spaces and a sentence in it
    /// is not a namespace.
    /// </summary>
    public static string Name(string? value, int maxChars = 256)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxChars && NamePattern().IsMatch(value) ? value : Wrap(value, maxChars);
    }

    /// <summary>
    /// Shortens an envelope (or plain text) to about <paramref name="keep"/> characters of
    /// content, keeping the envelope closed and saying how much went.
    /// </summary>
    public static string Shorten(string value, int keep)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (IsWrapped(value))
        {
            var inner = value[Open.Length..^Close.Length];
            return Open + Cut(inner, keep) + Close;
        }

        return Cut(value, keep);
    }

    /// <summary>Drops control characters (except newline and tab) and the bidirectional overrides.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (c is '\n' or '\t')
            {
                sb.Append(c);
            }
            else if (char.IsControl(c) || IsBidiControl(c))
            {
                continue;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>
    /// Cuts escaped text without splitting an entity (<c>&amp;lt;</c>) or a surrogate pair, and says
    /// how much it cut.
    /// </summary>
    private static string Cut(string text, int max)
    {
        max = Math.Max(0, max);

        if (text.Length <= max)
        {
            return text;
        }

        var at = max;

        // Back off out of an entity: an '&' with no ';' after it before the cut.
        var amp = text.LastIndexOf('&', Math.Max(0, at - 1));
        if (amp >= 0 && amp > at - 6 && text.IndexOf(';', amp) >= at)
        {
            at = amp;
        }

        if (at > 0 && char.IsHighSurrogate(text[at - 1]))
        {
            at--;
        }

        return text[..at] + CutMarker(text.Length - at);
    }

    private static bool IsBidiControl(char c) =>
        c is '‎' or '‏' or '؜'
        || (c >= '‪' && c <= '‮')
        || (c >= '⁦' && c <= '⁩');

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.:/@+=-]{0,255}$")]
    private static partial Regex NamePattern();
}
