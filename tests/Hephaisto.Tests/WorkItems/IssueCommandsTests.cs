using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.WorkItems;

namespace Hephaisto.Tests.WorkItems;

/// <summary>
/// Which comment answers a plan, by its text: the whole grammar as a table. A comment that is
/// not a command changes nothing; a comment wrongly read as one starts a Job that pushes a
/// branch - so every row that could be somebody TALKING about a command is "not one".
/// </summary>
public sealed class IssueCommandsTests
{
    public static TheoryData<string, string> Approvals => new()
    {
        { "exactly", "/approve" },
        { "with a newline", "/approve\n" },
        { "as Windows writes it", "/approve\r\n" },
        { "after blank lines", "\n\n  \n/approve" },
        { "with spaces around it", "  /approve   " },
        { "with a remark after it", "/approve\n\nLooks right to me. /reject would have been wrong." },
        { "with a second command after it", "/approve\n/reject no" },
    };

    [Theory]
    [MemberData(nameof(Approvals))]
    public void AnApproval(string _, string body) =>
        IssueCommands.Parse(body).Should().Be(new IssueCommand(IssueCommandKind.Approve, null));

    public static TheoryData<string, string?> NotCommands => new()
    {
        { "nothing", null },
        { "empty", "" },
        { "blank", " \n\t\n" },
        { "a sentence", "This looks good to me." },
        { "text before it on the line", "LGTM /approve" },
        { "text before it in the comment", "LGTM\n/approve" },
        { "a word after it", "/approve please" },
        { "punctuation after it", "/approve." },
        { "another word", "/approved" },
        { "upper case", "/APPROVE" },
        { "capitalised", "/Approve" },
        { "capitalised rejection", "/Reject not this way" },
        { "without the slash", "approve" },
        { "two slashes", "//approve" },
        { "a quotation", "> /approve" },
        { "a list item", "- /approve" },
        { "a code span", "`/approve`" },
        { "inside a fence", "```\n/approve\n```" },
        { "inside a fence with a language", "```text\n/approve\n```" },
        { "an indented code block", "    /approve" },
        { "behind a tab", "\t/approve" },
        { "bold", "**/approve**" },
        { "an HTML comment before it", "<!-- -->\n/approve" },
        { "a zero-width space before it", "​/approve" },
        { "a mention before it", "@hephaisto-bot /approve" },
        { "the plan's own words", "**To go ahead,** an approver replies `/approve`. **To refuse it,** an approver replies `/reject <reason>`." },
        { "a rejection run together", "/rejected" },
        { "a rejection with a colon", "/reject: no" },
        { "a rejection with a hyphen", "/reject-this" },
        { "another command", "/close" },
    };

    [Theory]
    [MemberData(nameof(NotCommands))]
    public void NotACommand(string _, string? body) => IssueCommands.Parse(body).Should().BeNull();

    public static TheoryData<string, string, string> Rejections => new()
    {
        { "with a reason", "/reject not this way: the null is the caller's", "not this way: the null is the caller's" },
        { "without one", "/reject", IssueCommands.NoReason },
        { "without one, and spaces", "  /reject   \n\n", IssueCommands.NoReason },
        { "with the reason on the next line", "/reject\nThe null is the caller's.", "The null is the caller's." },
        { "over several lines", "/reject not this way\n\n1. the null is the caller's\n2. the test is wrong\r\n", "not this way\n\n1. the null is the caller's\n2. the test is wrong" },
        { "behind a tab", "/reject\tnot this way", "not this way" },
        { "after blank lines", "\n\n/reject  twice  spaced ", "twice  spaced" },
        { "whose reason is an approval", "/reject\n/approve", "/approve" },
        { "whose reason is a fence", "/reject\n```\nrm -rf /\n```", "```\nrm -rf /\n```" },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void ARejection_AndItsReason(string _, string body, string reason) =>
        IssueCommands.Parse(body).Should().Be(new IssueCommand(IssueCommandKind.Reject, reason));

    [Fact]
    public void AReasonIsCapped_WithoutSplittingACharacter()
    {
        var command = IssueCommands.Parse("/reject " + new string('a', IssueCommands.MaxReason - 1) + "\U0001F600 and a tail")!;

        command.Kind.Should().Be(IssueCommandKind.Reject);
        command.Reason.Should().EndWith("…").And.HaveLength(IssueCommands.MaxReason);
        command.Reason![^2].Should().Be('a', "the pair that would have been cut in half is left out whole");

        IssueCommands.Parse("/reject " + new string('b', IssueCommands.MaxReason))!.Reason.Should().HaveLength(IssueCommands.MaxReason).And.NotEndWith("…");
    }

    private static GitHubComment By(string login, long id, string body) =>
        new(1791308488290, body, new GitHubAccount(login, id), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "https://github.com/octo/shop/issues/12#issuecomment-1791308488290");

    [Fact]
    public void TheAccountHephaistoWritesAs_NeverGivesACommand()
    {
        // Whatever it says, and however its login is spelled: its own plan comment names both words.
        IssueCommands.Read(By("hephaisto-bot", 9001, "/approve"), "hephaisto-bot").Should().BeNull();
        IssueCommands.Read(By("Hephaisto-Bot", 9001, "/reject no"), "hephaisto-bot").Should().BeNull();

        // Anybody else's is read - whether they may answer is the caller's question, by number.
        IssueCommands.Read(By("passerby", 2002, "/approve"), "hephaisto-bot").Should().Be(new IssueCommand(IssueCommandKind.Approve, null));
        IssueCommands.Read(By("ghost", 0, "/reject gone"), "hephaisto-bot").Should().Be(new IssueCommand(IssueCommandKind.Reject, "gone"));
    }

    [Fact]
    public void TheTwoWords_AreTheOnesThePlanCommentNames()
    {
        IssueCommands.Approve.Should().Be("/approve");
        IssueCommands.Reject.Should().Be("/reject");
        IssueCommands.NoReason.Should().Be("no reason given");
    }
}
