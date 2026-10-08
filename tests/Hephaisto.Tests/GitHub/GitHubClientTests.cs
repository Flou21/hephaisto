using System.Net;
using System.Reflection;
using System.Text;
using Hephaisto.Agent.GitHub;
using Hephaisto.Tests.TestData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.GitHub;

/// <summary>
/// The eight calls to GitHub's REST API, against a handler that records what was sent and
/// answers with GitHub's own shapes: its field names, its headers, its error bodies.
/// </summary>
/// <remarks>
/// What is pinned here is what a client gets wrong against the real thing and right against a
/// naive fake: the list is issues AND pull requests, a tag is weak and has to go back verbatim,
/// a rate limit is a 403 that looks like a permission problem, a description nobody wrote is
/// null, and a comment's id does not fit in 32 bits.
/// </remarks>
public sealed class GitHubClientTests
{
    private const string Token = "ghp_notARealTokenNotARealToken1234567890";
    private const string Repo = "octo/shop";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- what every request carries --------------------------------------------------------

    [Fact]
    public async Task Every_call_says_who_it_is_and_which_api_it_speaks()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.OK, """{"login":"hephaisto-bot","id":9001}"""));

        var user = await client.GetAuthenticatedUserAsync(Ct);

        user.Ok.Should().BeTrue();
        user.Value.Should().Be(new GitHubAccount("hephaisto-bot", 9001));

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Uri.Should().Be("https://github.example/api/v3/user", "a path in the base URL is kept, as GitHub Enterprise Server needs");
        sent.Headers["Accept"].Should().Be("application/vnd.github+json");
        sent.Headers["X-GitHub-Api-Version"].Should().Be("2022-11-28");
        sent.Headers["User-Agent"].Should().Contain("hephaisto", "GitHub refuses a request without a User-Agent");
        sent.Headers["Authorization"].Should().Be($"Bearer {Token}");
    }

    [Fact]
    public void The_client_can_neither_delete_nor_close()
    {
        // Hephaisto writes comments. Closing an issue is its pull request's job, on merge, and
        // nothing here should ever be able to remove what a person wrote.
        typeof(IGitHubClient).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Should().NotContain(n => n.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Close", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Remove", StringComparison.OrdinalIgnoreCase));
    }

    // --- the list ---------------------------------------------------------------------------

    [Fact]
    public async Task The_list_asks_for_open_issues_assigned_to_the_account_and_reads_what_github_sends()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.OK, IssueList, etag: "W/\"abc123\""));

        var list = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", etag: null, Ct);

        var sent = handler.Requests.Single();
        sent.Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/issues?assignee=hephaisto-bot&state=open&per_page=100");
        sent.Headers.Should().NotContainKey("If-None-Match", "there is nothing to compare with the first time");

        list.Ok.Should().BeTrue();
        list.ETag.Should().Be("W/\"abc123\"");
        list.Value!.HasMore.Should().BeFalse();

        var issue = list.Value.Issues.Should().ContainSingle("the second entry carries pull_request: it is a pull request").Subject;
        issue.Number.Should().Be(42);
        issue.NodeId.Should().Be("I_kwDOabc");
        issue.Title.Should().Be("The order total is null for an empty cart");
        issue.Body.Should().BeNull("a description nobody wrote is null, not an empty string");
        issue.IsOpen.Should().BeTrue();
        issue.Url.Should().Be("https://github.com/octo/shop/issues/42");
        issue.Author.Should().Be(new GitHubAccount("reporter", 3003));
        issue.IsAssignedTo("Hephaisto-Bot").Should().BeTrue("a login is compared without case, as GitHub does");
        issue.Labels.Should().Equal("bug", "area:checkout");
        issue.Type.Should().Be("Bug");
    }

    [Fact]
    public async Task A_tag_goes_back_exactly_as_it_came_and_a_304_is_unchanged()
    {
        var (client, handler, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotModified));

        var list = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", etag: "W/\"abc123\"", Ct);

        // Weak, with its prefix and its quotes: a parsed-and-rewritten tag never matches.
        handler.Requests.Single().Headers["If-None-Match"].Should().Be("W/\"abc123\"");

        list.Outcome.Should().Be(GitHubOutcome.NotModified);
        list.Ok.Should().BeFalse("there is no list in a 304, and nothing may read one out of it");
        list.ETag.Should().Be("W/\"abc123\"", "the tag the caller holds is still the tag");
    }

    [Fact]
    public async Task More_than_a_page_is_said_and_not_fetched()
    {
        var (client, handler, _) = Build(_ => Json(
            HttpStatusCode.OK, IssueList, link: "<https://github.example/api/v3/repositories/1/issues?page=2>; rel=\"next\""));

        var list = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        list.Value!.HasMore.Should().BeTrue();
        handler.Requests.Should().ContainSingle("one request per repository per pass, however many issues there are");
    }

    [Fact]
    public async Task A_full_page_is_taken_to_have_more_behind_it()
    {
        var hundred = "[" + string.Join(",", Enumerable.Range(1, 100).Select(n =>
            $$"""{"number":{{n}},"node_id":"I_{{n}}","title":"t","body":null,"state":"open","html_url":"u","user":{"login":"a","id":1},"assignees":[],"labels":[]}""")) + "]";

        var (client, _, _) = Build(_ => Json(HttpStatusCode.OK, hundred));

        var list = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        list.Value!.Issues.Should().HaveCount(100);
        list.Value.HasMore.Should().BeTrue("a proxy may strip the Link header; a full page says the same thing");
    }

    [Fact]
    public async Task One_issue_is_read_and_a_missing_one_is_not_found()
    {
        var (client, handler, _) = Build(r => r.Uri.EndsWith("/issues/42", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, ClosedIssue)
            : Json(HttpStatusCode.NotFound, """{"message":"Not Found","documentation_url":"https://docs.github.com/rest","status":"404"}"""));

        var found = await client.GetIssueAsync(Repo, 42, Ct);
        var missing = await client.GetIssueAsync(Repo, 43, Ct);

        handler.Requests[0].Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/issues/42");
        found.Value!.IsOpen.Should().BeFalse();
        found.Value.IsAssignedTo("hephaisto-bot").Should().BeFalse();
        found.Value.Author.Login.Should().Be("ghost", "a deleted account's issue stays, with user: null");

        missing.Outcome.Should().Be(GitHubOutcome.NotFound);
        missing.Detail.Should().Contain("Not Found");
    }

    // --- failures are values ------------------------------------------------------------------

    [Fact]
    public async Task A_refused_token_is_unauthorized_and_the_token_is_in_nothing_that_comes_back()
    {
        var (client, _, _) = Build(_ => Json(
            HttpStatusCode.Unauthorized, """{"message":"Bad credentials","documentation_url":"https://docs.github.com/rest","status":"401"}"""));

        var result = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        result.Outcome.Should().Be(GitHubOutcome.Unauthorized);
        result.Detail.Should().Be("HTTP 401: Bad credentials");
        result.Describe().Should().Contain("token was refused").And.NotContain(Token);
    }

    [Fact]
    public async Task A_403_that_names_no_limit_is_a_permission_and_not_a_rate_limit()
    {
        var (client, handler, _) = Build(_ => Json(
            HttpStatusCode.Forbidden,
            """{"message":"Resource not accessible by personal access token","status":"403"}""",
            headers: [("x-ratelimit-remaining", "4990")]));

        var first = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);
        await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        first.Outcome.Should().Be(GitHubOutcome.Unauthorized);
        first.RetryAt.Should().BeNull();
        handler.Requests.Should().HaveCount(2, "nothing was told to wait, so the next call is made");
    }

    [Fact]
    public async Task A_used_up_limit_is_a_403_and_nothing_is_asked_until_it_resets()
    {
        var reset = Given.Now.AddSeconds(20);
        var limited = true;

        var (client, handler, clock) = Build(_ => limited
            ? Json(
                HttpStatusCode.Forbidden,
                """{"message":"API rate limit exceeded for user ID 9001.","documentation_url":"https://docs.github.com/rest/overview/rate-limits-for-the-rest-api"}""",
                headers: [("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))])
            : Json(HttpStatusCode.OK, "[]"));

        var first = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        first.Outcome.Should().Be(GitHubOutcome.RateLimited, "GitHub's primary limit is a 403, not a 429");
        first.RetryAt.Should().Be(reset.AddSeconds(1));
        first.Describe().Should().StartWith("rate limited until 12:00:21");

        // Every call, not only the one that was refused: the limit is the account's.
        clock.UtcNow = Given.Now.AddSeconds(15);
        var held = await client.GetIssueAsync(Repo, 42, Ct);
        var alsoHeld = await client.CreateCommentAsync(Repo, 42, "a plan", Ct);

        held.Outcome.Should().Be(GitHubOutcome.RateLimited);
        held.RetryAt.Should().Be(reset.AddSeconds(1));
        alsoHeld.Outcome.Should().Be(GitHubOutcome.RateLimited);
        handler.Requests.Should().ContainSingle("a client that keeps asking through a limit earns a longer one");

        limited = false;
        clock.UtcNow = reset.AddSeconds(2);
        var after = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        after.Ok.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_secondary_limit_says_how_long_to_wait(HttpStatusCode status)
    {
        var (client, _, _) = Build(_ => Json(
            status,
            """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}""",
            headers: [("retry-after", "30")]));

        var result = await client.GetPullRequestAsync(Repo, 7, Ct);

        result.Outcome.Should().Be(GitHubOutcome.RateLimited);
        result.RetryAt.Should().Be(Given.Now.AddSeconds(30));
    }

    [Fact]
    public async Task A_limit_that_names_no_time_still_waits()
    {
        var (client, handler, _) = Build(_ => Json(
            HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""", headers: [("x-ratelimit-remaining", "0")]));

        var result = await client.GetAuthenticatedUserAsync(Ct);
        await client.GetAuthenticatedUserAsync(Ct);

        result.RetryAt.Should().Be(Given.Now.Add(GitHubClient.DefaultRetryAfter));
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_5xx_is_a_server_error_and_is_asked_once(HttpStatusCode status)
    {
        var (client, handler, _) = Build(_ => Json(status, """{"message":"Server Error"}"""));

        var result = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        result.Outcome.Should().Be(GitHubOutcome.ServerError);
        result.Detail.Should().Be($"HTTP {(int)status}: Server Error");
        handler.Requests.Should().ContainSingle("the next pass is the retry; this client has none of its own");
    }

    [Fact]
    public async Task No_answer_is_unreachable_and_a_timeout_is_no_answer()
    {
        var (refused, _, _) = Build(_ => throw new HttpRequestException("Connection refused (github.example:443)"));
        var (slow, _, _) = Build(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 20 seconds elapsing."));

        var first = await refused.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);
        var second = await slow.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        first.Outcome.Should().Be(GitHubOutcome.Unreachable);
        first.Detail.Should().Contain("Connection refused");
        second.Outcome.Should().Be(GitHubOutcome.Unreachable, "the client's own timeout is GitHub not answering, not the caller stopping");
    }

    [Fact]
    public async Task The_caller_stopping_is_not_swallowed_as_a_failure_of_githubs()
    {
        using var stop = new CancellationTokenSource();
        var (client, _, _) = Build(_ =>
        {
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        });

        var act = () => client.GetAuthenticatedUserAsync(stop.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_200_that_is_not_githubs_json_is_refused_and_not_thrown()
    {
        var (client, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Sign in to the corporate proxy</body></html>", Encoding.UTF8, "text/html"),
        });

        var result = await client.ListAssignedIssuesAsync(Repo, "hephaisto-bot", null, Ct);

        result.Outcome.Should().Be(GitHubOutcome.Rejected);
        result.Detail.Should().StartWith("the answer was not GitHub's JSON");
    }

    // --- comments and pull requests: built now, used from stage 2.3 ---------------------------

    [Fact]
    public async Task Comments_are_asked_for_since_a_whole_second_and_their_ids_are_not_32_bit()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.OK, CommentList));

        var comments = await client.ListCommentsAsync(Repo, 42, new DateTimeOffset(2026, 10, 6, 9, 30, 15, 987, TimeSpan.Zero), Ct);

        handler.Requests.Single().Uri.Should().Be(
            "https://github.example/api/v3/repos/octo/shop/issues/42/comments?per_page=100&since=2026-10-06T09%3A30%3A15Z");

        comments.Value.Should().HaveCount(2);
        comments.Value![0].Id.Should().Be(1759743015123L);
        comments.Value[0].Author.Should().Be(new GitHubAccount("maintainer", 1001));
        comments.Value[0].Body.Should().Be("/approve");
        comments.Value[0].CreatedAt.Should().Be(new DateTimeOffset(2026, 10, 6, 9, 30, 15, TimeSpan.Zero));
        comments.Value[1].Author.Login.Should().Be("ghost");
    }

    [Fact]
    public async Task Without_a_time_every_comment_is_asked_for()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.OK, "[]"));

        await client.ListCommentsAsync(Repo, 42, since: null, Ct);

        handler.Requests.Single().Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/issues/42/comments?per_page=100");
    }

    [Fact]
    public async Task A_comment_is_posted_on_the_issue_and_comes_back_with_its_id()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.Created, Comment));

        const string plan = "The plan:\n\n1. \"quote\" it, and <escape> nothing else";

        var created = await client.CreateCommentAsync(Repo, 42, plan, Ct);

        var sent = handler.Requests.Single();
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/issues/42/comments");

        using var body = System.Text.Json.JsonDocument.Parse(sent.Body);
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("body");
        body.RootElement.GetProperty("body").GetString().Should().Be(plan, "what GitHub reads back is the text, whatever the encoder escaped");

        created.Ok.Should().BeTrue();
        created.Value!.Id.Should().Be(1759743015123L);
        created.Value.Url.Should().EndWith("#issuecomment-1759743015123");
    }

    [Fact]
    public async Task A_comment_is_edited_by_its_id_and_not_through_its_issue()
    {
        var (client, handler, _) = Build(_ => Json(HttpStatusCode.OK, Comment));

        var edited = await client.UpdateCommentAsync(Repo, 1759743015123L, "status: planning", Ct);

        var sent = handler.Requests.Single();
        sent.Method.Should().Be(HttpMethod.Patch);
        sent.Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/issues/comments/1759743015123");
        sent.Body.Should().Be("""{"body":"status: planning"}""");
        edited.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task A_comment_github_will_not_take_is_rejected()
    {
        var (client, _, _) = Build(_ => Json(HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed"}"""));

        var result = await client.CreateCommentAsync(Repo, 42, " ", Ct);

        result.Outcome.Should().Be(GitHubOutcome.Rejected);
        result.Detail.Should().Be("HTTP 422: Validation Failed");
    }

    [Fact]
    public async Task A_pull_request_says_whether_it_was_merged()
    {
        var (client, handler, _) = Build(r => r.Uri.EndsWith("/pulls/7", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """{"number":7,"state":"closed","draft":false,"merged":true,"merged_at":"2026-10-06T10:00:00Z","html_url":"https://github.com/octo/shop/pull/7","head":{"ref":"hephaisto/issue-42"},"body":"Closes octo/shop#42"}""")
            : Json(HttpStatusCode.OK, """{"number":8,"state":"open","draft":true,"merged":false,"merged_at":null,"html_url":"https://github.com/octo/shop/pull/8","head":{"ref":null},"body":null}"""));

        var merged = await client.GetPullRequestAsync(Repo, 7, Ct);
        var draft = await client.GetPullRequestAsync(Repo, 8, Ct);

        handler.Requests[0].Uri.Should().Be("https://github.example/api/v3/repos/octo/shop/pulls/7");

        merged.Value.Should().Be(new GitHubPullRequest(
            7, "closed", false, true, new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero), "https://github.com/octo/shop/pull/7", "hephaisto/issue-42"));

        draft.Value!.Merged.Should().BeFalse();
        draft.Value.Draft.Should().BeTrue();
        draft.Value.MergedAt.Should().BeNull();
        draft.Value.HeadRef.Should().BeNull();
    }

    [Fact]
    public async Task A_repository_is_asked_for_its_default_branch()
    {
        var (client, handler, _) = Build(request => request.Uri.EndsWith("/octo/shop", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """{"id":1,"full_name":"octo/shop","private":true,"default_branch":"develop","archived":false}""")
            : Json(HttpStatusCode.OK, """{"id":2,"full_name":"octo/empty","default_branch":null}"""));

        var shop = await client.GetRepositoryAsync(Repo, Ct);
        var empty = await client.GetRepositoryAsync("octo/empty", Ct);

        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Uri.Should().Be("https://github.example/api/v3/repos/octo/shop");

        shop.Value.Should().Be(new GitHubRepository("octo/shop", "develop"));
        empty.Value!.DefaultBranch.Should().BeNull("an empty repository has no branch yet, and the caller decides what that means");
    }

    [Fact]
    public async Task A_repository_the_token_cannot_see_is_not_found_and_says_nothing_about_a_branch()
    {
        var (client, _, _) = Build(_ => Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));

        var result = await client.GetRepositoryAsync(Repo, Ct);

        result.Outcome.Should().Be(GitHubOutcome.NotFound);
        result.Value.Should().BeNull();
    }

    // --- GitHub's shapes ------------------------------------------------------------------------

    private const string IssueList = """
        [
          {
            "number": 42,
            "node_id": "I_kwDOabc",
            "title": "The order total is null for an empty cart",
            "body": null,
            "state": "open",
            "html_url": "https://github.com/octo/shop/issues/42",
            "user": {"login": "reporter", "id": 3003},
            "assignees": [{"login": "hephaisto-bot", "id": 9001}],
            "labels": [{"id": 1, "name": "bug", "color": "d73a4a"}, "area:checkout"],
            "type": {"id": 5, "name": "Bug"},
            "comments": 0,
            "created_at": "2026-10-06T09:00:00Z",
            "updated_at": "2026-10-06T09:00:00Z"
          },
          {
            "number": 43,
            "node_id": "PR_kwDOabd",
            "title": "fix: the order total",
            "body": "Closes #42",
            "state": "open",
            "html_url": "https://github.com/octo/shop/pull/43",
            "user": {"login": "hephaisto-bot", "id": 9001},
            "assignees": [{"login": "hephaisto-bot", "id": 9001}],
            "labels": [],
            "pull_request": {"url": "https://api.github.com/repos/octo/shop/pulls/43", "merged_at": null}
          }
        ]
        """;

    private const string ClosedIssue = """
        {
          "number": 42, "node_id": "I_kwDOabc", "title": "t", "body": "b", "state": "closed",
          "html_url": "https://github.com/octo/shop/issues/42",
          "user": null, "assignees": [], "labels": [], "type": null
        }
        """;

    private const string Comment = """
        {
          "id": 1759743015123,
          "body": "/approve",
          "user": {"login": "maintainer", "id": 1001},
          "created_at": "2026-10-06T09:30:15Z",
          "updated_at": "2026-10-06T09:30:15Z",
          "html_url": "https://github.com/octo/shop/issues/42#issuecomment-1759743015123"
        }
        """;

    private const string CommentList = $$"""
        [
          {{Comment}},
          {"id": 1759743015124, "body": "me too", "user": null,
           "created_at": "2026-10-06T09:31:00Z", "updated_at": "2026-10-06T09:31:00Z",
           "html_url": "https://github.com/octo/shop/issues/42#issuecomment-1759743015124"}
        ]
        """;

    // --- harness ------------------------------------------------------------------------------

    internal sealed record Sent(HttpMethod Method, string Uri, string Body, IReadOnlyDictionary<string, string> Headers);

    private static (GitHubClient Client, RecordingHandler Handler, Given.FixedClock Clock) Build(Func<Sent, HttpResponseMessage> answer)
    {
        var handler = new RecordingHandler(answer);
        var clock = Given.Clock();

        var client = new GitHubClient(
            new HttpClient(handler),
            Options.Create(new GitHubOptions
            {
                Enabled = true,
                ApiBaseUrl = "https://github.example/api/v3",
                Token = Token,
                Repositories = [Repo],
            }),
            new GitHubRateLimit(),
            clock,
            NullLogger<GitHubClient>.Instance);

        return (client, handler, clock);
    }

    private static HttpResponseMessage Json(
        HttpStatusCode status,
        string body,
        string? etag = null,
        string? link = null,
        (string Name, string Value)[]? headers = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
        }

        if (link is not null)
        {
            response.Headers.TryAddWithoutValidation("Link", link);
        }

        foreach (var (name, value) in headers ?? [])
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    private sealed class RecordingHandler(Func<Sent, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var sent = new Sent(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase));

            Requests.Add(sent);

            return answer(sent);
        }
    }
}
