using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hephaisto.Agent.CodeFix.Contract;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.WorkItems;

/// <summary>What the status comment is written from: the work item, and its newest attempt if it has one.</summary>
public sealed record IssueStatus(
    Guid WorkItemId,
    WorkItemState State,
    string? StateReason,
    string? DeclineCodes,
    string? DeclineReason,
    string IssueUrl,
    IssueAttempt? Attempt);

/// <summary>The columns of an attempt the status comment reads. Never its plan or its request.</summary>
public sealed record IssueAttempt(
    Guid Id,
    CodeFixState State,
    string? FailureReason,
    string? Summary,
    string? ApprovedBy,
    string? PrUrl,
    long? PlanCommentId,
    string Branch,
    string DefaultBranch);

/// <summary>
/// The two comments Hephaisto writes on an issue it was handed (v0.14.0): where the work stands,
/// edited in place, and the plan, once per attempt. Text only - pure, so every sentence a person
/// will read on their issue is a unit test.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing of the issue is repeated.</b> Not its title, not its body: the reader is looking at
/// both, and a comment that echoes them repeats whatever somebody planted there under the bot's
/// name.
/// </para>
/// <para>
/// <b>What a model wrote is shown as text and can do nothing.</b> A plan's summary, root cause and
/// steps, and the reason an attempt failed, are a model's or a runner's words about text a
/// stranger wrote. On GitHub a comment is not inert: <c>@name</c> notifies a person,
/// <c>#12</c> and an issue's URL write a cross-reference into another issue's timeline, an image
/// is fetched from wherever it points when anybody opens the page. So every such string goes
/// through <see cref="Neutralise"/>:
/// </para>
/// <list type="bullet">
/// <item>it becomes one paragraph - no line of it can start a heading, a list, a quote or a fence;</item>
/// <item><c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> are written as entities, so it holds no HTML and
/// no entity that would decode into one of the characters below;</item>
/// <item>a backslash and both square brackets are backslash-escaped, so it holds no link, no image,
/// no footnote and no task box;</item>
/// <item>a zero-width space follows every <c>@</c> before a letter or digit, every <c>#</c> and
/// <c>GH-</c> before a digit, and sits inside every <c>://</c> and after <c>www</c>: it reads the
/// same, and it mentions nobody, references nothing and is not turned into a link.</item>
/// </list>
/// <para>
/// A file path is a code span, where GitHub links and notifies nothing, with its backticks
/// taken out so it cannot end the span.
/// </para>
/// <para>
/// <b>Each comment carries an invisible marker</b> with the id of what it belongs to. Writing a
/// comment and recording its id are two steps with a network between them; a process that dies
/// there finds its own comment by the marker - written by its own account - instead of writing a
/// second one.
/// </para>
/// </remarks>
public static partial class IssueComments
{
    /// <summary>GitHub refuses a comment beyond 65,536 characters. Kept well under it.</summary>
    public const int MaxBody = 60_000;

    private const char ZeroWidthSpace = '​';

    public static string StatusMarker(Guid workItemId) => $"<!-- hephaisto:status:{workItemId:N} -->";

    public static string PlanMarker(Guid attemptId) => $"<!-- hephaisto:plan:{attemptId:N} -->";

    /// <summary>What is compared to decide whether the comment has to be edited.</summary>
    public static string Digest(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    /// <summary>Where a comment is on the issue's page. GitHub's own anchor.</summary>
    public static string CommentUrl(string issueUrl, long commentId) =>
        $"{issueUrl}#issuecomment-{commentId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Where the work stands, in one comment that is edited as it moves. A function of what is
    /// stored and nothing else - no clock, no mode - so the same state is the same text, and the
    /// same text is not written twice.
    /// </summary>
    public static string Status(IssueStatus s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var text = new StringBuilder();

        text.Append("### Hephaisto\n\n");
        text.Append(StatusLine(s));
        text.Append("\n\n<sub>Hephaisto edits this one comment as the work moves.</sub>\n");
        text.Append(StatusMarker(s.WorkItemId));

        return Cap(text.ToString());
    }

    private static string StatusLine(IssueStatus s)
    {
        var a = s.Attempt;

        if (s.State == WorkItemState.Cancelled)
        {
            return $"**Hephaisto has let go of this issue:** {Clause(s.StateReason, 300)}."
                + (a is { State: CodeFixState.PrOpened, PrUrl: { Length: > 0 } left }
                    ? $" The draft pull request stays as it is: {Link(left)}"
                    : " Anything that was running for it was stopped.")
                + " Assign it again to hand it back.";
        }

        if (s.State == WorkItemState.Done)
        {
            return "**Done.** The pull request was merged."
                + (a?.PrUrl is { Length: > 0 } merged ? $" {Link(merged)}" : string.Empty);
        }

        if (a is null)
        {
            if (string.IsNullOrWhiteSpace(s.DeclineReason))
                return "**Taken.** Hephaisto has this issue and has not started on it yet.";

            return string.Equals(s.DeclineCodes, nameof(CodeFixReasonCode.ModeOff), StringComparison.Ordinal)
                ? "**Not planned.** The code-fix mode of this install is Off, so no Job is started for this issue. "
                    + "When an operator turns it on, Hephaisto plans it without being asked again."
                : $"**Waiting.** No plan has been started: {Clause(s.DeclineReason, 500)}. "
                    + "Hephaisto asks again by itself; nothing has to be done on this issue.";
        }

        const string again = " To have it tried again, unassign Hephaisto and assign it again.";

        return a.State switch
        {
            CodeFixState.Eligible or CodeFixState.Planning =>
                $"**Planning.** A read-only Job is reading the code on branch {Code(a.DefaultBranch)} to write a plan. Nothing is changed.",

            CodeFixState.PlanReady =>
                "**A plan is ready**"
                + (a.PlanCommentId is { } plan ? $": [read the plan]({CommentUrl(s.IssueUrl, plan)})." : ".")
                + " It waits for an approver's answer; nothing is changed until then.",

            CodeFixState.Implementing =>
                $"**Implementing.** {Clause(a.ApprovedBy, 100)} approved the plan. A Job is making the change on branch "
                + $"{Code(a.Branch)} and will open a draft pull request.",

            CodeFixState.PrOpened =>
                $"**A draft pull request is open:** {Link(a.PrUrl)}\n\nA person reviews and merges it; Hephaisto does neither.",

            CodeFixState.Denied =>
                $"**The plan was rejected** by {Clause(a.ApprovedBy, 100)}: {Clause(a.FailureReason, 500)}. Nothing was changed." + again,

            CodeFixState.Expired =>
                "**The plan expired.** Nobody answered it in time, and a plan that old is not implemented. Nothing was changed." + again,

            CodeFixState.Cancelled =>
                $"**Stopped.** {Clause(a.FailureReason, 500)}. Nothing was changed." + again,

            _ =>
                $"**It did not work.** {Clause(a.FailureReason, 500)}."
                + (string.IsNullOrWhiteSpace(a.Summary) ? string.Empty : $"\n\n**What it found.** {Neutralise(a.Summary, 1500)}")
                + "\n\nNothing was changed." + again,
        };
    }

    /// <summary>
    /// The plan, as an approver reads it. Once per attempt and never edited: what is approved is
    /// the plan in the database, and a comment that could change after it was read would be a
    /// second, weaker copy of it.
    /// </summary>
    /// <param name="mode">The install's effective code-fix mode when this is written.</param>
    public static string Plan(CodeFixAttempt attempt, CodeFixPlanResult? plan, CodeFixMode mode)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        var text = new StringBuilder();

        text.Append("## Hephaisto's plan for this issue\n\n");
        text.Append("**Summary.** ").Append(Neutralise(plan?.Summary ?? attempt.Summary, 2000)).Append("\n\n");
        text.Append("**What is wrong, and what will change.** ").Append(Neutralise(plan?.RootCause ?? attempt.RootCause, 4000)).Append("\n\n");

        text.Append("**Files**\n");

        if (plan is { Files.Count: > 0 })
        {
            foreach (var file in plan.Files.Take(50))
                text.Append("- ").Append(Code(file)).Append('\n');
        }
        else
        {
            text.Append("- (the plan names none)\n");
        }

        if (plan is { Steps.Count: > 0 })
        {
            text.Append("\n**Steps**\n");

            for (var i = 0; i < Math.Min(plan.Steps.Count, 20); i++)
                text.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". ").Append(Neutralise(plan.Steps[i], 1000)).Append('\n');
        }

        text.Append("\n**Verification.** ").Append(VerificationSentence(attempt.VerificationLevel ?? plan?.Verification.Level));

        if (plan is { Verification.NotVerifiable.Count: > 0 })
        {
            text.Append(" What only production can show:\n");

            foreach (var item in plan.Verification.NotVerifiable.Take(10))
                text.Append("- ").Append(Neutralise(item, 500)).Append('\n');
        }
        else
        {
            text.Append('\n');
        }

        text.Append("\n**Cost of planning.** $")
            .Append(attempt.PlanCostUsd.ToString("0.00", CultureInfo.InvariantCulture))
            .Append(attempt.Confidence is { } confidence
                ? $" · the plan's own confidence is {confidence.ToString("0.00", CultureInfo.InvariantCulture)}"
                : string.Empty)
            .Append("\n\n---\n\n");

        if (attempt.NeedsCait)
        {
            text.Append("**This plan cannot be approved here.** It needs a change in a shared library first; a person makes that "
                + "change, and this issue is then planned again. Reply `/reject <reason>` to close the plan.\n\n");
        }
        else
        {
            text.Append("**To go ahead,** an approver replies `/approve`. **To refuse it,** an approver replies `/reject <reason>`.\n\n");

            if (mode != CodeFixMode.Pr)
            {
                text.Append("Implementing is switched off on this install: when this was written its code-fix mode was ")
                    .Append(mode)
                    .Append(", which plans and changes nothing. An approval is refused until an operator sets the mode to Pr.\n\n");
            }
        }

        text.Append("What is approved is this plan as Hephaisto stored it - not the text of this comment, and not the issue as it may read by then. ")
            .Append("An approval starts a Job that works on branch ").Append(Code(attempt.Branch))
            .Append(" and opens a draft pull request; a person reviews and merges it.\n\n");

        text.Append("<sub>Attempt ").Append(Code(attempt.Id.ToString()));

        if (!string.IsNullOrWhiteSpace(attempt.AnalysedRef))
            text.Append(" · analysed at ").Append(Code(attempt.AnalysedRef.Length > 12 ? attempt.AnalysedRef[..12] : attempt.AnalysedRef));

        text.Append("</sub>\n").Append(PlanMarker(attempt.Id));

        return Cap(text.ToString(), PlanMarker(attempt.Id));
    }

    private static string VerificationSentence(string? level) => level switch
    {
        "tests" => "The change will be covered by tests that the Job runs before it opens the pull request.",
        "build-only" => "Build only: no test reaches the change, so the pull request will say that verification is weak.",
        "typecheck-only" => "Type check only: no test reaches the change, so the pull request will say that verification is weak.",
        "none" => "None: nothing can be run for this change, so the pull request will say that verification is weak.",
        _ => $"As the plan states it: {Code(level)}.",
    };

    /// <summary>
    /// A string somebody else wrote, as one paragraph of text that GitHub renders and does
    /// nothing with. See the remarks on the type for what each step is for.
    /// </summary>
    public static string Neutralise(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "(nothing was said)";

        // No control character but white space, and no format character at all: a zero-width
        // space of the text's own, a direction override that makes a line read as another one.
        var one = Whitespace().Replace(new string([.. text.Where(Printable)]), " ").Trim();

        if (one.Length > max)
        {
            var cut = char.IsHighSurrogate(one[max - 1]) ? max - 1 : max;
            one = one[..cut].TrimEnd() + "…";
        }

        one = one
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("://", ":" + ZeroWidthSpace + "//", StringComparison.Ordinal);

        one = Mention().Replace(one, "@" + ZeroWidthSpace);
        one = Reference().Replace(one, "$1" + ZeroWidthSpace);

        return Www().Replace(one, "$1" + ZeroWidthSpace);
    }

    private static bool Printable(char c) =>
        char.IsWhiteSpace(c) || (!char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format);

    /// <summary>The same, for the middle of a sentence of Hephaisto's own: without the full stop it may end in.</summary>
    private static string Clause(string? text, int max) => Neutralise(text, max).TrimEnd('.');

    /// <summary>A path, a branch, an id: a code span, which GitHub neither links nor notifies from.</summary>
    public static string Code(string? text)
    {
        var one = Whitespace().Replace(new string([.. (text ?? string.Empty).Where(Printable)]), " ").Replace('`', '\'').Trim();

        if (one.Length == 0)
            return "`(none)`";

        return "`" + (one.Length > 300 ? one[..300] + "…" : one) + "`";
    }

    /// <summary>
    /// A pull request's address. It was checked against the repository's own host and path before
    /// it was stored, and it is the one thing here that is meant to be a link - so it is written
    /// bare, on its own, where anything that is not a URL is shown as the text it is.
    /// </summary>
    private static string Link(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http"
            ? parsed.AbsoluteUri
            : Code(url);

    private static string Cap(string body, string? marker = null)
    {
        if (body.Length <= MaxBody)
            return body;

        // The marker is what a restart finds the comment by; it is kept whatever is cut.
        return body[..MaxBody].TrimEnd() + "\n\n…(shortened)\n" + (marker ?? string.Empty);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"@(?=[A-Za-z0-9_])")]
    private static partial Regex Mention();

    [GeneratedRegex(@"(#|\bGH-)(?=\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Reference();

    [GeneratedRegex(@"(\bwww)(?=\.)", RegexOptions.IgnoreCase)]
    private static partial Regex Www();
}
