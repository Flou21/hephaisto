using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hephaisto.Agent.CodeFix;
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
/// Everything Hephaisto writes on an issue it was handed (v0.14.0): where the work stands, edited
/// in place; the plan, once per attempt; and the few one-time answers to somebody who answered
/// the plan and was not heard. Text only - pure, so every sentence a person will read on their
/// issue is a unit test.
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
/// <c>GH-</c> before a digit, and every <c>/</c> before a digit, and sits inside every
/// <c>://</c> and after <c>www</c>: it reads the same, and it mentions nobody, references nothing
/// and is not turned into a link.</item>
/// </list>
/// <para>
/// <b>A code span of the text's own is left exactly as it was written.</b> Between two runs of
/// backticks of the same length GitHub acts on nothing - asked of github.com with a mention, a
/// reference, an address, an image and a tag inside one - and it shows every character,
/// including a backslash. The first plan in production named <c>children: [...]</c> in a code
/// span and was posted as <c>children: \[...\]</c>, because the brackets were escaped where an
/// escape is not one. The one span that is not passed on is one that holds <c>&lt;!--</c>: a
/// marker of Hephaisto's own must not be something a model can write into a comment.
/// </para>
/// <para>
/// <b>The slash before a digit was missing until GitHub was asked</b> (the live tier,
/// <c>scripts/e2e/github-live.sh</c>, L04). GitHub reads <c>/issues/12</c>, <c>/pull/12</c> and
/// <c>/discussions/12</c> as references by themselves - with no scheme and no host before them,
/// in any case of letters - and <c>owner/repo/issues/12</c> as one in another repository. An
/// address with only its scheme broken was still a link to the issue, and still wrote
/// "mentioned this issue" into that issue's timeline under Hephaisto's name. What is left
/// standing on purpose: a commit id and an advisory's id (GHSA-, CVE-) are still linked by
/// GitHub, which notifies nobody and writes into no timeline.
/// </para>
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

    /// <summary>
    /// The most comments Hephaisto ever writes on one issue for one work item, whatever anybody
    /// does there. By construction it writes one status comment, one plan per attempt, one answer
    /// to people who may not answer, and one per cause an approval was refused for; this is the
    /// ceiling above that, for the day one of those rules is wrong. Beyond it Hephaisto only
    /// edits its status comment. <c>ISSUES_COMMENT_CAP</c> in <c>scripts/e2e/lib/issues.sh</c>
    /// is this number, and a test holds the two together.
    /// </summary>
    public const int MaxPerWorkItem = 6;

    /// <summary>The key of the one answer to everybody who answered a plan and is not an approver.</summary>
    public const string NotApproverKey = "not-approver";

    private const char ZeroWidthSpace = '​';

    public static string StatusMarker(Guid workItemId) => $"<!-- hephaisto:status:{workItemId:N} -->";

    public static string PlanMarker(Guid attemptId) => $"<!-- hephaisto:plan:{attemptId:N} -->";

    /// <summary>The marker of a one-time answer: the attempt it is about, and which answer it is.</summary>
    public static string AnswerMarker(Guid attemptId, string key) => $"<!-- hephaisto:answer:{attemptId:N}:{key} -->";

    /// <summary>
    /// The answers of an attempt that a list of comments already holds, by their markers - what
    /// a process that wrote one and died before recording it finds on its next pass.
    /// </summary>
    public static IEnumerable<string> AnswerKeysIn(Guid attemptId, string body)
    {
        var prefix = $"<!-- hephaisto:answer:{attemptId:N}:";

        for (var at = body.IndexOf(prefix, StringComparison.Ordinal); at >= 0; at = body.IndexOf(prefix, at + 1, StringComparison.Ordinal))
        {
            var end = body.IndexOf(" -->", at + prefix.Length, StringComparison.Ordinal);

            if (end > at + prefix.Length)
                yield return body[(at + prefix.Length)..end];
        }
    }

    /// <summary>
    /// Which one-time answer a refusal is: one per cause, and for the mode one per mode - "the
    /// mode is Plan" and "the mode is Off" are two things to be told.
    /// </summary>
    public static string AnswerKey(CodeFixRefusal refusal, CodeFixMode? mode) => refusal switch
    {
        CodeFixRefusal.ModeBelowPr => mode == CodeFixMode.Plan ? "mode-plan" : "mode-off",
        CodeFixRefusal.EmergencyStop => "emergency-stop",
        CodeFixRefusal.KillSwitch => "kill-switch",
        CodeFixRefusal.NeedsSecondRepository => "second-repository",
        CodeFixRefusal.NotWaiting => "not-waiting",
        CodeFixRefusal.SubjectTakenBack => "taken-back",
        _ => "refused",
    };

    /// <summary>
    /// To somebody who answered a plan and is not an approver. Once per attempt, whoever it was
    /// and however many follow: it names the first by login, in a code span - which notifies
    /// nobody - and nobody else, and it does not say who the approvers are.
    /// </summary>
    public static string NotApprover(Guid attemptId, string login) =>
        $"**Not counted.** {Code(login)} is not one of the approvers of this install, and only they can answer this plan here. "
        + "Nothing was changed.\n\n<sub>Hephaisto says this once per plan.</sub>\n"
        + AnswerMarker(attemptId, NotApproverKey);

    /// <summary>
    /// To an approver whose answer the door refused, with the reason in a sentence. Once per
    /// attempt and cause. The console's own message names arms and ConfigMaps; an issue anybody
    /// can read is told this instead.
    /// </summary>
    /// <param name="mode">For <see cref="CodeFixRefusal.ModeBelowPr"/>: the mode the install declares.</param>
    public static string Refused(Guid attemptId, string login, IssueCommandKind command, CodeFixRefusal refusal, CodeFixMode? mode)
    {
        const string again = " The plan still stands: reply `/approve` again once that has changed.";
        const string withdrawn = " No Job is started, and with nothing allowed to run this plan is withdrawn.";

        var why = refusal switch
        {
            CodeFixRefusal.ModeBelowPr when mode == CodeFixMode.Plan =>
                "the code-fix mode of this install is Plan, which plans and changes nothing. Implementing needs an operator to set it to Pr." + again,
            CodeFixRefusal.ModeBelowPr =>
                "the code-fix mode of this install is Off." + withdrawn,
            CodeFixRefusal.EmergencyStop =>
                "an operator has engaged Hephaisto's emergency stop." + withdrawn,
            CodeFixRefusal.KillSwitch =>
                "Hephaisto's kill switch is holding it back." + withdrawn,
            CodeFixRefusal.NeedsSecondRepository =>
                "this plan needs a change in a second repository first, which a person makes. It cannot be approved here; "
                + "reply `/reject <reason>` to close it.",
            CodeFixRefusal.NotWaiting =>
                "this plan is no longer waiting for an answer. The comment above says what became of it.",
            CodeFixRefusal.SubjectTakenBack =>
                "this issue is no longer Hephaisto's.",
            _ =>
                "it could not be recorded. An operator finds the reason in Hephaisto's console.",
        };

        var word = command == IssueCommandKind.Approve ? IssueCommands.Approve : IssueCommands.Reject;

        return $"**Not done.** {Code(login)}'s `{word}` was read and refused: {why}"
            + "\n\n<sub>Hephaisto says this once per plan and cause.</sub>\n"
            + AnswerMarker(attemptId, AnswerKey(refusal, mode));
    }

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

        if (s.State == WorkItemState.Cancelled && string.Equals(s.StateReason, WorkItemReasons.PullRequestClosed, StringComparison.Ordinal))
        {
            return "**Hephaisto has let go of this issue:** its pull request was closed without merging"
                + (a?.PrUrl is { Length: > 0 } closed ? $": {Link(closed)}\n\n" : ". ")
                + "To hand the issue back, unassign Hephaisto and assign it again.";
        }

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
            return "**Done.** The pull request was merged"
                + (a?.PrUrl is { Length: > 0 } merged ? $": {Link(merged)}" : ".")
                + "\n\nFor more work on this issue, reopen it, or unassign Hephaisto and assign it again.";
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
    /// <param name="answerable">
    /// Whether the install names anybody who may answer on the issue (<c>GitHub:Approvers</c>).
    /// With nobody listed a comment is never an answer, and the plan says where it is answered
    /// instead of inviting a reply that will not be read.
    /// </param>
    public static string Plan(CodeFixAttempt attempt, CodeFixPlanResult? plan, CodeFixMode mode, bool answerable)
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

        if (!answerable)
        {
            text.Append("**This plan is not answered on the issue.** This install names nobody who may approve a plan in a comment, "
                + "so a reply here is not read as an answer. An operator approves or refuses it in Hephaisto's console.\n\n");

            if (attempt.NeedsCait)
            {
                text.Append("It also needs a change in a shared library first; a person makes that change, and this issue is then planned again.\n\n");
            }
            else if (mode != CodeFixMode.Pr)
            {
                text.Append("Implementing is switched off on this install: when this was written its code-fix mode was ")
                    .Append(mode)
                    .Append(", which plans and changes nothing. An approval is refused until an operator sets the mode to Pr.\n\n");
            }
        }
        else if (attempt.NeedsCait)
        {
            text.Append("**This plan cannot be approved here.** It needs a change in a shared library first; a person makes that "
                + "change, and this issue is then planned again. Reply `/reject <reason>` to close the plan.\n\n");
        }
        else
        {
            text.Append("**To go ahead,** an approver replies `/approve`. **To refuse it,** an approver replies `/reject <reason>`. "
                + "The command is the first line of the comment, and only an approver of this install is heard.\n\n");

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

        // Code spans stay as they were written, and everything between them is made inert.
        // The spans are found the way GitHub finds them - a run of backticks, closed by the
        // next run of the same length - and in the text as it leaves here: outside a span no
        // "<" survives and every backslash is doubled, so nothing that comes before a run of
        // backticks can stop it from opening one, and the two readings cannot differ.
        var inert = new StringBuilder(one.Length + 16);
        var at = 0;

        while (at < one.Length)
        {
            var open = one.IndexOf('`', at);

            if (open < 0)
            {
                inert.Append(Inert(one[at..]));
                break;
            }

            var run = 1;

            while (open + run < one.Length && one[open + run] == '`')
                run++;

            var close = ClosingRun(one, open + run, run);

            // Unclosed, the backticks are characters. And a span that holds the start of an
            // HTML comment is not passed on as one: a marker of Hephaisto's own, shown as code,
            // would still be found by the process that looks for its markers.
            if (close < 0 || one.AsSpan(open + run, close - open - run).Contains("<!--", StringComparison.Ordinal))
            {
                inert.Append(Inert(one[at..(open + run)]));
                at = open + run;
                continue;
            }

            inert.Append(Inert(one[at..open])).Append(one, open, close + run - open);
            at = close + run;
        }

        return inert.ToString();
    }

    /// <summary>Where the run of exactly <paramref name="length"/> backticks that closes a code span starts, or -1.</summary>
    private static int ClosingRun(string text, int from, int length)
    {
        for (var i = text.IndexOf('`', from); i >= 0; i = text.IndexOf('`', i))
        {
            var run = 1;

            while (i + run < text.Length && text[i + run] == '`')
                run++;

            if (run == length)
                return i;

            i += run;

            if (i >= text.Length)
                break;
        }

        return -1;
    }

    /// <summary>Text outside a code span: no HTML, no link, no mention, no reference, no address.</summary>
    private static string Inert(string text)
    {
        if (text.Length == 0)
            return text;

        var one = text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("://", ":" + ZeroWidthSpace + "//", StringComparison.Ordinal);

        one = Mention().Replace(one, "@" + ZeroWidthSpace);
        one = Reference().Replace(one, "$1" + ZeroWidthSpace);
        one = NumberedPath().Replace(one, "/" + ZeroWidthSpace);

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

    // /issues/12, /pull/12, /discussions/12 - and whatever GitHub comes to read the same way.
    [GeneratedRegex(@"/(?=[0-9])")]
    private static partial Regex NumberedPath();
}
