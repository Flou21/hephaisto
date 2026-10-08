using Hephaisto.Agent.GitHub;

namespace Hephaisto.Agent.WorkItems;

public enum IssueCommandKind
{
    Approve = 0,
    Reject = 1,
}

/// <param name="Reason">For a rejection: what was written after the word, or <see cref="IssueCommands.NoReason"/>. Null for an approval.</param>
public sealed record IssueCommand(IssueCommandKind Kind, string? Reason);

/// <summary>
/// The two answers a plan can be given on its issue (v0.14.0), and nothing else: which comment is
/// one, by its text alone. Pure, so the whole grammar is a table in a test.
/// </summary>
/// <remarks>
/// <para><b>The grammar.</b> A comment is a command when its FIRST NON-BLANK LINE, with the white
/// space around it taken off, is</para>
/// <list type="bullet">
/// <item><c>/approve</c> - exactly that, and nothing else on the line; or</item>
/// <item><c>/reject</c>, alone or followed by white space and a reason. The reason is the rest of
/// that line and every line after it, trimmed and capped at <see cref="MaxReason"/> characters;
/// without one it is recorded as <see cref="NoReason"/>.</item>
/// </list>
/// <para>
/// Lower case, as written here: <c>/Approve</c> and <c>/APPROVE</c> are sentences, not commands.
/// </para>
/// <para>
/// <b>Strict on purpose, and in one direction.</b> A comment that is not a command changes
/// nothing; a comment wrongly read as one starts a Job that pushes a branch. So everything that
/// could be somebody TALKING about the command is not the command: text before it ("LGTM,
/// /approve"), a quotation of somebody else's (<c>&gt; /approve</c>), a code fence around it or
/// the four spaces that make it a code block, a list item, <c>/approve please</c>,
/// <c>/approved</c>. What comes AFTER an <c>/approve</c> line is the writer's own remark and
/// does not matter.
/// </para>
/// <para>
/// <b>Who wrote it is not this type's question</b> beyond one case: a comment of the account
/// Hephaisto writes as is never a command, whatever it says - its own plan comment spells both
/// words out. Whether the author may answer is decided by the caller, by account NUMBER.
/// </para>
/// </remarks>
public static class IssueCommands
{
    public const string Approve = "/approve";
    public const string Reject = "/reject";

    /// <summary>What a rejection without a reason is recorded as.</summary>
    public const string NoReason = "no reason given";

    /// <summary>A reason is a sentence or a paragraph, kept with the attempt and shown on the issue.</summary>
    public const int MaxReason = 1000;

    /// <summary>The command a comment is, or null. <paramref name="bot"/> is the login Hephaisto writes as.</summary>
    public static IssueCommand? Read(GitHubComment comment, string bot)
    {
        ArgumentNullException.ThrowIfNull(comment);

        return string.Equals(comment.Author.Login, bot, StringComparison.OrdinalIgnoreCase) ? null : Parse(comment.Body);
    }

    /// <summary>The command a comment's text is, or null.</summary>
    public static IssueCommand? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var lines = body.ReplaceLineEndings("\n").Split('\n');
        var first = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));

        if (first < 0)
            return null;

        // Four spaces or a tab make the line a code block in Markdown: the command shown, not given.
        var indent = lines[first].Length - lines[first].TrimStart().Length;

        if (indent > 3 || lines[first][..indent].Contains('\t', StringComparison.Ordinal))
            return null;

        var line = lines[first].Trim();

        if (string.Equals(line, Approve, StringComparison.Ordinal))
            return new IssueCommand(IssueCommandKind.Approve, null);

        if (!line.StartsWith(Reject, StringComparison.Ordinal))
            return null;

        // "/rejected", "/reject:" and "/reject-this" are words of their own.
        if (line.Length > Reject.Length && !char.IsWhiteSpace(line[Reject.Length]))
            return null;

        var reason = string.Join('\n', lines.Skip(first + 1).Prepend(line[Reject.Length..])).Trim();

        if (reason.Length > MaxReason)
        {
            var cut = char.IsHighSurrogate(reason[MaxReason - 1]) ? MaxReason - 1 : MaxReason;
            reason = reason[..cut].TrimEnd() + "…";
        }

        return new IssueCommand(IssueCommandKind.Reject, reason.Length == 0 ? NoReason : reason);
    }
}
