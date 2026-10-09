using Hephaisto.Agent.GitHub;

namespace Hephaisto.Agent.WorkItems;

public enum IssueCommandKind
{
    Approve = 0,
    Reject = 1,

    /// <summary>Plan it again, with what was written on the issue since (v0.14.0).</summary>
    Replan = 2,
}

/// <param name="Reason">For a rejection: what was written after the word, or <see cref="IssueCommands.NoReason"/>. Null for an approval.</param>
public sealed record IssueCommand(IssueCommandKind Kind, string? Reason);

/// <summary>
/// The three things an approver can say on an issue (v0.14.0), and nothing else: which comment
/// is one, by its text alone. Pure, so the whole grammar is a table in a test.
/// </summary>
/// <remarks>
/// <para><b>The grammar.</b> A comment is a command when its FIRST NON-BLANK LINE, with the white
/// space around it taken off, is</para>
/// <list type="bullet">
/// <item><c>/approve</c> - exactly that, and nothing else on the line; or</item>
/// <item><c>/replan</c> - exactly that, likewise. What the comment says after that line is not
/// part of the command: it is an answer, like any other comment its author writes on the issue,
/// and reaches the replanning Job with them; or</item>
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
/// <c>/approved</c>, <c>/replan with X</c>. What comes AFTER an <c>/approve</c> line is the
/// writer's own remark and does not matter. A <c>/replan</c> wrongly read as one ends a plan
/// that was waiting and starts a Job: it is held to the same line.
/// </para>
/// <para>
/// <b>Who wrote it is not this type's question</b> beyond one case: a comment of the account
/// Hephaisto writes as is never a command, whatever it says - its own plan comment spells all
/// three words out. Whether the author may answer is decided by the caller, by account NUMBER.
/// </para>
/// </remarks>
public static class IssueCommands
{
    public const string Approve = "/approve";
    public const string Reject = "/reject";
    public const string Replan = "/replan";

    /// <summary>The word a command is given by, for a sentence about it.</summary>
    public static string Word(IssueCommandKind kind) => kind switch
    {
        IssueCommandKind.Approve => Approve,
        IssueCommandKind.Reject => Reject,
        _ => Replan,
    };

    /// <summary>The same as a metric's <c>verb</c> label: a closed set.</summary>
    public static string Verb(IssueCommandKind kind) => Word(kind)[1..];

    /// <summary>What a rejection without a reason is recorded as.</summary>
    public const string NoReason = "no reason given";

    /// <summary>
    /// The reaction that approves the plan it is set on (#298): GitHub's word for it. Not the
    /// thumbs-up, which people set on a comment to say they have read it or like it - and an
    /// approval starts a Job that pushes a branch.
    /// </summary>
    public const string ApproveReaction = "rocket";

    /// <summary>The reaction that rejects the plan it is set on.</summary>
    public const string RejectReaction = "-1";

    /// <summary>What a rejection by reaction is recorded as: a reaction carries no reason.</summary>
    public const string NoReasonByReaction = "no reason given: rejected with a thumbs-down on the plan";

    /// <summary>
    /// The answer a reaction on a plan comment is, or null: two of GitHub's eight, and no
    /// reaction asks for a new plan - what that needs is the answers, and a reaction has none.
    /// Whose reaction it is, and on which comment, is the caller's question.
    /// </summary>
    public static IssueCommand? ReadReaction(string? content) => content switch
    {
        ApproveReaction => new IssueCommand(IssueCommandKind.Approve, null),
        RejectReaction => new IssueCommand(IssueCommandKind.Reject, NoReasonByReaction),
        _ => null,
    };

    /// <summary>A reaction as a person sees it on the page, for a sentence about it.</summary>
    public static string Emoji(IssueCommandKind kind) => kind == IssueCommandKind.Approve ? "🚀" : "👎";

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

        if (string.Equals(line, Replan, StringComparison.Ordinal))
            return new IssueCommand(IssueCommandKind.Replan, null);

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
