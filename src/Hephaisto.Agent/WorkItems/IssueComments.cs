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
/// <param name="Attempts">How many attempts the work item has had, the newest included.</param>
/// <param name="ReplanRequestedBy">
/// Who asked for a new plan after the newest attempt, while that plan has not been started
/// (<c>github:&lt;login&gt;</c>); null when nobody did, and once the new attempt exists.
/// </param>
/// <param name="Answerable">
/// Whether the install names anybody who may answer on the issue. Configuration, which a
/// running process does not change - so it may be in the text, where a clock or a mode may not.
/// </param>
public sealed record IssueStatus(
    Guid WorkItemId,
    WorkItemState State,
    string? StateReason,
    string? DeclineCodes,
    string? DeclineReason,
    string IssueUrl,
    IssueAttempt? Attempt,
    int Attempts = 1,
    string? ReplanRequestedBy = null,
    bool Answerable = true);

/// <summary>
/// What the status comment reads of an attempt. Never its plan or its request - with one
/// exception: for an attempt that did not work there is no plan comment, so what the planner
/// asked and noted is said here, beside what it found.
/// </summary>
/// <param name="Questions">For an attempt that failed: what its planner asked of a person. Null otherwise.</param>
/// <param name="Notes">For an attempt that failed: its planner's notes. Null otherwise.</param>
public sealed record IssueAttempt(
    Guid Id,
    CodeFixState State,
    string? FailureReason,
    string? Summary,
    string? ApprovedBy,
    string? PrUrl,
    long? PlanCommentId,
    string Branch,
    string DefaultBranch,
    IReadOnlyList<string>? Questions = null,
    IReadOnlyList<string>? Notes = null);

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
    /// The most comments Hephaisto ever writes for ONE ATTEMPT, whatever anybody does on the
    /// issue: its plan, and its one-time answers. By construction it writes one plan per
    /// attempt, one answer to people who may not answer, and one per cause a command was refused
    /// for; this is the ceiling above that, for the day one of those rules is wrong.
    /// <c>ISSUES_ATTEMPT_COMMENT_CAP</c> in <c>scripts/e2e/lib/issues.sh</c> is this number.
    /// </summary>
    public const int MaxPerAttempt = 5;

    /// <summary>How many attempts one work item may have (<see cref="WorkItem.MaxAttempts"/>). <c>ISSUES_ATTEMPT_CAP</c> in the suite.</summary>
    public const int MaxAttemptsPerWorkItem = WorkItem.MaxAttempts;

    /// <summary>
    /// The most comments Hephaisto ever writes on one issue for one work item: the one status
    /// comment, and at most <see cref="MaxPerAttempt"/> for each of at most
    /// <see cref="MaxAttemptsPerWorkItem"/> attempts. It was a flat six while a work item had
    /// one attempt; since an approver can ask for a new plan it grows with the attempts, each of
    /// which a person asked for, and stops with them. Beyond it Hephaisto only edits its status
    /// comment. <c>ISSUES_COMMENT_CAP</c> in <c>scripts/e2e/lib/issues.sh</c> is this number,
    /// and a test holds the three together.
    /// </summary>
    public const int MaxPerWorkItem = 1 + (MaxAttemptsPerWorkItem * MaxPerAttempt);

    /// <summary>How many of a plan's notes a comment holds. The contract's own cap.</summary>
    public const int MaxNotes = 20;

    /// <summary>
    /// How much of one step, and of one note, is shown: all of it. These are the plan result's
    /// own limits (<c>codefix-plan-result.schema.json</c>), so nothing a planner can return is
    /// cut here - what bounds a comment is <see cref="MaxBody"/>. A step was cut at half its
    /// limit until 2026-10-08, and the one plan that had a long step lost the sentence that
    /// said what must NOT be changed (#294).
    /// </summary>
    public const int MaxStepChars = 2000;

    /// <inheritdoc cref="MaxStepChars"/>
    public const int MaxNoteChars = 2000;

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
        CodeFixRefusal.JobRunning => "job-running",
        CodeFixRefusal.PullRequestOpen => "pull-request",
        CodeFixRefusal.TooManyAttempts => "attempts",
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
            CodeFixRefusal.JobRunning =>
                "a Job is running for this issue right now, so there is nothing to plan again yet. "
                + "When it has ended the comment above says so, and a new `/replan` is read then.",
            CodeFixRefusal.PullRequestOpen =>
                "a draft pull request is already open for this issue, and what it still needs is said in its review. "
                + "To start over instead, close the pull request; then " + UnassignAndWait,
            CodeFixRefusal.TooManyAttempts =>
                $"this issue has been planned {MaxAttemptsPerWorkItem.ToString(CultureInfo.InvariantCulture)} times, which is the most for one hand-over. "
                + "To hand it over again, unassign Hephaisto, wait until the comment above says it has let go, and assign it again.",
            _ =>
                "it could not be recorded. An operator finds the reason in Hephaisto's console.",
        };

        return $"**Not done.** {Code(login)}'s `{IssueCommands.Word(command)}` was read and refused: {why}"
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

    /// <summary>
    /// For a work item that ended while its issue stayed assigned - its pull request was merged
    /// or closed. Such an issue is taken again only after one poll found it without Hephaisto
    /// on it, and this comment does not change when that happened: so it says to leave a gap.
    /// </summary>
    private const string UnassignAndWait =
        "unassign Hephaisto, wait a minute or two, and assign it again: Hephaisto has to have seen the issue without itself on it first.";

    private static string StatusLine(IssueStatus s)
    {
        var a = s.Attempt;

        if (s.State == WorkItemState.Cancelled && string.Equals(s.StateReason, WorkItemReasons.PullRequestClosed, StringComparison.Ordinal))
        {
            return "**Hephaisto has let go of this issue:** its pull request was closed without merging"
                + (a?.PrUrl is { Length: > 0 } closed ? $": {Link(closed)}\n\n" : ". ")
                + "To hand the issue back, " + UnassignAndWait;
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
                + "\n\nFor more work on this issue, reopen it, or " + UnassignAndWait;
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

        // A new plan was asked for after this attempt, and has not been started: that is where
        // the work stands, whatever became of the attempt.
        if (s.ReplanRequestedBy is { Length: > 0 } by)
        {
            // A person who replied /replan, or one who assigned the issue again: both asked.
            // Where GitHub did not say who assigned it, nobody is named.
            var asked = by.StartsWith("github:", StringComparison.Ordinal)
                ? $"{Clause(by, 100)} asked for a new plan"
                : "The issue was assigned to Hephaisto again";

            if (string.IsNullOrWhiteSpace(s.DeclineReason))
                return $"**Planning again.** {asked}. The new plan is started on Hephaisto's next pass; nothing is changed.";

            return string.Equals(s.DeclineCodes, nameof(CodeFixReasonCode.ModeOff), StringComparison.Ordinal)
                ? $"**Not planned.** {asked}, and the code-fix mode of this install is Off, so no Job is started for this issue. "
                    + "When an operator turns it on, Hephaisto plans it without being asked again."
                : $"**Waiting.** {asked}, and the new plan has not been started: {Clause(s.DeclineReason, 500)}. "
                    + "Hephaisto asks again by itself; nothing has to be done on this issue.";
        }

        var again = Again(s);
        var replanned = s.Attempts > 1;

        return a.State switch
        {
            CodeFixState.Eligible or CodeFixState.Planning when replanned =>
                $"**Planning again.** A read-only Job is reading the code on branch {Code(a.DefaultBranch)} to write a new plan, "
                + "with the earlier plan and what was answered on this issue. Nothing is changed.",

            CodeFixState.Eligible or CodeFixState.Planning =>
                $"**Planning.** A read-only Job is reading the code on branch {Code(a.DefaultBranch)} to write a plan. Nothing is changed.",

            CodeFixState.PlanReady =>
                (replanned ? "**A new plan is ready**" : "**A plan is ready**")
                + (a.PlanCommentId is { } plan ? $": [read the plan]({CommentUrl(s.IssueUrl, plan)})." : ".")
                + (replanned ? " It replaces the earlier one." : string.Empty)
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
                + AskedAndNoted(a.Questions, a.Notes)
                + "\n\nNothing was changed." + again,
        };
    }

    /// <summary>
    /// How an attempt that has ended is followed by another, in words that are true of this
    /// install and this work item: an approver's <c>/replan</c> where somebody may answer on the
    /// issue at all, or assigning the issue again - and after the last attempt a hand-over has,
    /// only a new hand-over.
    /// </summary>
    /// <remarks>
    /// "Unassign Hephaisto and assign it again" stood here alone until 2026-10-08, when somebody
    /// did exactly that within seven seconds and nothing happened: an unassignment was noticed
    /// only by a poll that found the issue without Hephaisto on it. For an attempt that has
    /// ended the sentence is true since then, however quickly it is done - the assignment is
    /// known by its time on GitHub (<c>GitHubIssuePoller.Assignments.cs</c>). After the last
    /// attempt it is not: that needs a new work item, and so the poll, and it says to wait.
    /// </remarks>
    private static string Again(IssueStatus s)
    {
        if (s.Attempts >= MaxAttemptsPerWorkItem)
        {
            return $" This issue has been planned {s.Attempts.ToString(CultureInfo.InvariantCulture)} times, which is the most for one hand-over. "
                + "To hand it over again, unassign Hephaisto, wait until this comment says it has let go, and assign it again.";
        }

        return s.Answerable
            ? " To have it tried again, an approver replies `/replan` - after answering in a comment, where something was asked. "
                + "Or unassign Hephaisto and assign it again."
            : " To have it tried again, unassign Hephaisto and assign it again.";
    }

    /// <summary>
    /// What the planner asked of a person and what it noted, for a comment: the questions as a
    /// numbered list - they are answered by number - and the notes folded away, since they are
    /// for whoever wants to know what was left out. Nothing at all when there is neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until 2026-10-08 neither was shown anywhere. The first plan in production left an entry
    /// where it was because the issue did not name it, and asked whether it should move too -
    /// in <c>notes</c>, which no comment rendered. The owner asked why Hephaisto had not
    /// suggested it.
    /// </para>
    /// <para>
    /// <b>A note about injected text is counted and not quoted.</b> The plan prompt asks for a
    /// suspected injection to be quoted in <c>notes</c>, so such a note is exactly where a model
    /// repeats what a stranger planted in the issue - and a comment is written under
    /// Hephaisto's name. The console shows those notes, marked, to an operator.
    /// </para>
    /// </remarks>
    private static string AskedAndNoted(IReadOnlyList<string>? questions, IReadOnlyList<string>? notes)
    {
        var text = new StringBuilder();
        var asked = CodeFixContract.Questions(questions);

        if (asked.Count > 0)
        {
            text.Append("\n\n**Questions**\n");

            for (var i = 0; i < asked.Count; i++)
                text.Append('\n').Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". ").Append(Neutralise(asked[i], CodeFixContract.MaxQuestionChars));
        }

        var noted = (notes ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Take(MaxNotes).ToList();

        if (noted.Count > 0)
        {
            var withheld = noted.Count(CodeFixQueries.IsInjectionNote);

            // A blank line after the summary, and one before the closing tag: between them
            // GitHub reads Markdown again, and a list is a list.
            text.Append("\n\n<details>\n<summary>The planner's notes (")
                .Append(noted.Count.ToString(CultureInfo.InvariantCulture))
                .Append(")</summary>\n");

            foreach (var note in noted.Where(n => !CodeFixQueries.IsInjectionNote(n)))
                text.Append("\n- ").Append(Neutralise(note, MaxNoteChars));

            if (withheld > 0)
            {
                // What is known is that the note MENTIONS it, and that is all this says: the
                // sentence used to tell an issue's author that their text read like an
                // instruction, on the strength of one word in a note (#291).
                text.Append("\n- ")
                    .Append(withheld == 1 ? "One note mentions prompt injection and is" : $"{withheld.ToString(CultureInfo.InvariantCulture)} notes mention prompt injection and are")
                    .Append(" not repeated here, because such a note may quote text from the issue; ")
                    .Append("an operator reads it on the attempt's page in Hephaisto's console.");
            }

            text.Append("\n\n</details>");
        }

        return text.ToString();
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
    /// <param name="ordinal">Which attempt of its work item this is, from 1. A later one says that it replaces a plan.</param>
    public static string Plan(CodeFixAttempt attempt, CodeFixPlanResult? plan, CodeFixMode mode, bool answerable, int ordinal = 1)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        var text = new StringBuilder();

        if (ordinal > 1)
        {
            text.Append("## Hephaisto's new plan for this issue\n\n")
                .Append("This plan replaces the earlier one on this issue. It was made with that plan and with what the issue's author and the approvers wrote here since.\n\n");
        }
        else
        {
            text.Append("## Hephaisto's plan for this issue\n\n");
        }
        text.Append("**Summary.** ").Append(NeutraliseBlock(plan?.Summary ?? attempt.Summary, 2000)).Append("\n\n");
        text.Append("**What is wrong, and what will change.** ").Append(NeutraliseBlock(plan?.RootCause ?? attempt.RootCause, 4000)).Append("\n\n");

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
            {
                var number = (i + 1).ToString(CultureInfo.InvariantCulture) + ". ";

                text.Append(number).Append(ItemBlock(plan.Steps[i], MaxStepChars, number.Length)).Append('\n');
            }
        }

        text.Append("\n**Verification.** ").Append(VerificationSentence(attempt.VerificationLevel ?? plan?.Verification.Level));

        if (plan is { Verification.NotVerifiable.Count: > 0 })
        {
            // For an issue the reader is a person at the running application, not a dashboard:
            // an issue names no workload, and what the Job cannot run is somebody looking.
            text.Append(" What only a person looking at the running application can confirm:\n");

            foreach (var item in plan.Verification.NotVerifiable.Take(20))
                text.Append("- ").Append(Neutralise(item, 1000)).Append('\n');
        }
        else
        {
            text.Append('\n');
        }

        // What it asks, and what it noted. Each block brings its own blank line before it, and
        // the line above is already ended.
        if (AskedAndNoted(plan?.Questions, plan?.Notes) is { Length: > 0 } asked)
            text.Append(asked.AsSpan(1)).Append('\n');

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
                + "change, and an approver then replies `/replan` to have this issue planned again. Reply `/reject <reason>` to close the plan instead.\n\n");
        }
        else
        {
            var asks = CodeFixContract.Questions(plan?.Questions).Count > 0;

            // Three things an approver can say, each true of this install as it stands.
            text.Append(asks
                    ? "**To go ahead,** an approver replies `/approve`: that takes the plan as it is, with the assumptions above. "
                        + "**To have it planned again with your answers,** write them in a comment, then reply `/replan`. "
                    : "**To go ahead,** an approver replies `/approve`. "
                        + "**To have it planned again,** say in a comment what should be different, then reply `/replan`. ")
                .Append("**To refuse it,** an approver replies `/reject <reason>`. "
                    + "A command is the first line of its comment, and only an approver of this install is heard.\n\n");

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

    /// <summary>
    /// The same for a text that has paragraphs and list items of its own: the two long fields
    /// of a plan. Each paragraph and each item is made inert as <see cref="Neutralise"/> makes
    /// a string inert, and what is kept between them is a blank line and a list marker -
    /// nothing else of the text's layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until 2026-10-08 these fields were one paragraph whatever they held. A planner that
    /// listed the five entries it would move, one to a line with their line numbers, and then
    /// explained the template in two more paragraphs, was posted as a single run-on one - the
    /// longest part of the plan, and the part a reviewer checks against the code (#292).
    /// </para>
    /// <para>
    /// <b>What a line may start.</b> A blank line ends a paragraph; a line that begins with
    /// <c>-</c>, <c>*</c>, <c>+</c>, a bullet or a number and a space is an item; any other line break is the
    /// writer's wrapping and becomes a space. A paragraph or an item that would begin a
    /// heading (<c>#</c>) or a rule or a setext underline (only <c>- = * _ ~</c>) gets a
    /// backslash in front: a text somebody else wrote does not get
    /// to put a headline into a comment of Hephaisto's, under which an approver looks for how
    /// to answer. A block quote and HTML cannot begin anywhere - no <c>&lt;</c> or
    /// <c>&gt;</c> leaves <see cref="Neutralise"/> - and a code fence over several lines is
    /// backticks on each of them.
    /// </para>
    /// <para>A text of one paragraph comes out as <see cref="Neutralise"/> returns it.</para>
    /// </remarks>
    public static string NeutraliseBlock(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Neutralise(text, max);

        var all = new string([.. text.Where(Printable)]).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        var cut = all.Length > max;

        if (cut)
            all = all[..(char.IsHighSurrogate(all[max - 1]) ? max - 1 : max)].TrimEnd();

        var blocks = new StringBuilder(all.Length + 32);
        var paragraph = new StringBuilder();
        var afterItem = false;

        void Flush()
        {
            if (paragraph.Length == 0)
                return;

            if (blocks.Length > 0)
                blocks.Append("\n\n");

            blocks.Append(LineStart(Neutralise(paragraph.ToString(), int.MaxValue)));
            paragraph.Clear();
            afterItem = false;
        }

        foreach (var raw in all.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0)
            {
                Flush();
                afterItem = false;
                continue;
            }

            if (ListItem().Match(line) is { Success: true } item)
            {
                Flush();

                // Items of one list follow each other; a list follows a paragraph after a
                // blank line, which GitHub does not need and every other renderer does.
                if (blocks.Length > 0)
                    blocks.Append(afterItem ? "\n" : "\n\n");

                // A numbered item keeps its number: "step 3" in the text below it still means one.
                blocks.Append(item.Groups[1].Success ? item.Groups[1].Value + ". " : "- ")
                    .Append(LineStart(Neutralise(item.Groups[2].Value, int.MaxValue)));
                afterItem = true;
                continue;
            }

            if (afterItem)
            {
                // The text goes on after a list without a blank line: a new paragraph, not a
                // lazy continuation of the last item.
                afterItem = false;
            }

            paragraph.Append(paragraph.Length == 0 ? string.Empty : " ").Append(line);
        }

        Flush();

        return cut ? blocks.Append('…').ToString() : blocks.ToString();
    }

    /// <summary>
    /// A model's text as ONE item of a list of Hephaisto's own - a step under its number: its
    /// paragraphs and its list kept (<see cref="NeutraliseBlock"/>), and every line after the
    /// first indented to where the item's text begins, so that they stay inside the item and the
    /// numbering goes on after it. A planner that writes a step as "do this, in this order:"
    /// and six lines of what, then "leave these alone", was posted as one sentence (#294).
    /// </summary>
    /// <param name="indent">The width of the item's marker: 3 for <c>1. </c>, 4 for <c>10. </c>.</param>
    public static string ItemBlock(string? text, int max, int indent)
    {
        var block = NeutraliseBlock(text, max);

        if (!block.Contains('\n', StringComparison.Ordinal))
            return block;

        var pad = new string(' ', indent);

        return string.Join('\n', block.Split('\n').Select((line, at) => at == 0 || line.Length == 0 ? line : pad + line));
    }

    /// <summary>A paragraph or an item that would begin something other than itself, with a backslash in front.</summary>
    private static string LineStart(string inert) =>
        inert.Length > 0 && (inert[0] == '#' || Rule().IsMatch(inert)) ? "\\" + inert : inert;

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

    /// <summary>A line that is a list item: a dash, an asterisk, a plus, a bullet or a number, white space, and something to say.</summary>
    [GeneratedRegex(@"^(?:[-*+\u2022]|(\d{1,3})[.)])\s+(\S.*)$")]
    private static partial Regex ListItem();

    /// <summary>A line of nothing but what a rule or a setext underline is made of.</summary>
    [GeneratedRegex(@"^[-=*_~\s]+$")]
    private static partial Regex Rule();

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
