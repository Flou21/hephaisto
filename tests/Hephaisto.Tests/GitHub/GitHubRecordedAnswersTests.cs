using System.Net;
using System.Text;
using Hephaisto.Agent.GitHub;
using Hephaisto.Tests.TestData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.GitHub;

/// <summary>
/// The client against what github.com actually answered (v0.14.0, #249).
/// </summary>
/// <remarks>
/// <para>
/// Every other test of <see cref="GitHubClient"/> feeds it JSON written by hand from GitHub's
/// documentation - which is how one finds out, months later, that a field is an object where
/// the documentation's example had a string. The files in <c>Recorded/</c> are answers of
/// api.github.com to the calls the client makes, taken on 2026-10-07 from the live tier's
/// sandbox after its first green run (<c>scripts/e2e/github-live.sh</c>): the issue of L01, its
/// three comments, its pull request, an issue the bot was unassigned from, the repository, the
/// bot's account, and a 404.
/// </para>
/// <para>
/// Recorded with a person's <c>gh</c>, not with the agent's token. Scrubbed: every key with
/// "token" in its name is gone, avatar addresses and e-mail fields are blanked, and the two
/// repository objects inside the pull request are cut down to their name and default branch.
/// Nothing else was edited - in particular not the fields the client does not read, which are
/// the point: the first run found no field read wrongly, and this is what it read.
/// </para>
/// </remarks>
public sealed class GitHubRecordedAnswersTests
{
    private const string Repo = "TrueRelevance/hephaisto-sandbox";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_list_of_issues_holds_a_pull_request_too_and_only_the_issue_is_kept()
    {
        var page = await Client("issues.json").ListAssignedIssuesAsync(Repo, "tr-agent-dev", etag: null, Ct);

        page.Ok.Should().BeTrue(page.Detail);

        // Two entries were recorded: pull request 20 and issue 19. GitHub lists both as issues.
        var issue = page.Value!.Issues.Should().ContainSingle("a pull request carries a pull_request key, and is not work").Subject;

        issue.Number.Should().Be(19);
        issue.Url.Should().Be("https://github.com/TrueRelevance/hephaisto-sandbox/issues/19");
        issue.Author.Should().Be(new GitHubAccount("Flou21", 40125985));
        issue.Assignees.Should().Equal(new GitHubAccount("tr-agent-dev", 339094978));
        issue.IsAssignedTo("TR-Agent-Dev").Should().BeTrue("a login is compared without regard to case");
        issue.Labels.Should().Equal(["bug"], "a label is an object with a name");
        issue.Type.Should().BeNull("the sandbox's organisation has set no issue type on it: the key is there, and null");
        issue.NodeId.Should().NotBeEmpty();
        issue.Title.Should().StartWith("[hephaisto-live L01 ");
        issue.Body.Should().Contain("FAKE-SDK-REPEAT:");
        page.Value.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task An_issue_nobody_is_assigned_to_has_an_empty_list_and_a_null_beside_it()
    {
        var issue = await Client("issue-unassigned.json").GetIssueAsync(Repo, 22, Ct);

        issue.Ok.Should().BeTrue(issue.Detail);
        issue.Value!.Assignees.Should().BeEmpty();
        issue.Value.IsAssignedTo("tr-agent-dev").Should().BeFalse();
        issue.Value.IsOpen.Should().BeFalse("it was recorded after the run had closed it");
        issue.Value.Labels.Should().BeEmpty();
    }

    [Fact]
    public async Task Comment_ids_are_beyond_32_bits_and_an_edited_comment_says_so()
    {
        var comments = await Client("comments.json").ListCommentsAsync(Repo, 19, since: null, etag: null, Ct);

        comments.Ok.Should().BeTrue(comments.Detail);
        comments.Value.Should().HaveCount(3);

        var status = comments.Value![0];
        var plan = comments.Value[1];
        var answer = comments.Value[2];

        comments.Value.Should().OnlyContain(c => c.Id > uint.MaxValue, "github.com passed 2^32 comments in 2024");
        comments.Value.Select(c => c.Id).Should().BeInAscendingOrder("the poller knows a newer comment by its larger id");

        status.Author.Should().Be(new GitHubAccount("tr-agent-dev", 339094978));
        status.Body.Should().Contain("<!-- hephaisto:status:");
        status.UpdatedAt.Should().BeAfter(status.CreatedAt, "the one status comment is edited in place");

        plan.Body.Should().Contain("<!-- hephaisto:plan:");
        plan.UpdatedAt.Should().Be(plan.CreatedAt, "a plan is never edited");
        plan.CreatedAt.Offset.Should().Be(TimeSpan.Zero, "GitHub's times are UTC, to the second");
        plan.CreatedAt.Millisecond.Should().Be(0);

        answer.Author.Should().Be(new GitHubAccount("Flou21", 40125985));
        answer.Body.Should().Be("/approve");
        answer.Url.Should().StartWith("https://github.com/TrueRelevance/hephaisto-sandbox/issues/19#issuecomment-");
    }

    [Fact]
    public async Task A_draft_that_was_closed_without_merging_is_neither_open_nor_merged()
    {
        var pull = await Client("pull.json").GetPullRequestAsync(Repo, 20, etag: null, Ct);

        pull.Ok.Should().BeTrue(pull.Detail);
        pull.Value.Should().Be(new GitHubPullRequest(
            20, "closed", Draft: true, Merged: false, MergedAt: null,
            "https://github.com/TrueRelevance/hephaisto-sandbox/pull/20", "hephaisto/codefix-53c8a774878e"));
        pull.Value!.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task The_repository_says_its_default_branch_and_the_account_its_number()
    {
        (await Client("repository.json").GetRepositoryAsync(Repo, Ct)).Value
            .Should().Be(new GitHubRepository(Repo, "main"));

        (await Client("user.json").GetAuthenticatedUserAsync(Ct)).Value
            .Should().Be(new GitHubAccount("tr-agent-dev", 339094978));
    }

    [Fact]
    public async Task What_GitHub_says_of_an_issue_it_does_not_have_is_NotFound_with_its_sentence()
    {
        var gone = await Client("not-found.json", HttpStatusCode.NotFound).GetIssueAsync(Repo, 99999, Ct);

        gone.Outcome.Should().Be(GitHubOutcome.NotFound);
        gone.Detail.Should().Be("HTTP 404: Not Found");
    }

    private static GitHubClient Client(string recorded, HttpStatusCode status = HttpStatusCode.OK) => new(
        new HttpClient(new Recorded(File.ReadAllText(Path.Combine(RepoRoot(), "tests", "Hephaisto.Tests", "GitHub", "Recorded", recorded)), status)),
        Options.Create(new GitHubOptions
        {
            Enabled = true,
            Token = "ghp_notARealTokenNotARealToken1234567890",
            Repositories = [Repo],
        }),
        new GitHubRateLimit(),
        Given.Clock(),
        NullLogger<GitHubClient>.Instance);

    private sealed class Recorded(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                // As GitHub sends it: with a charset.
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hephaisto.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
