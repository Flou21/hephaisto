using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Safety;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.GitHub;

/// <summary>How one call to GitHub ended, in the classes a caller does something different for.</summary>
public enum GitHubOutcome
{
    Ok = 0,

    /// <summary>A 304 to a conditional request: what the caller already holds is still true. Free against the rate limit.</summary>
    NotModified = 1,

    /// <summary>404 or 410. Also what GitHub answers for a private repository the token cannot see.</summary>
    NotFound = 2,

    /// <summary>401, or a 403 that is not a rate limit: the token is wrong, expired or lacks the permission.</summary>
    Unauthorized = 3,

    /// <summary>GitHub said to stop asking until <see cref="GitHubResult{T}.RetryAt"/> - or the client did, because it was told so earlier.</summary>
    RateLimited = 4,

    /// <summary>5xx.</summary>
    ServerError = 5,

    /// <summary>No answer: DNS, connection, proxy, timeout.</summary>
    Unreachable = 6,

    /// <summary>Any other refusal, such as a 422 for a body GitHub will not take.</summary>
    Rejected = 7,
}

/// <summary>
/// How one call to GitHub ended. Never an exception: a failure is a value, so one repository
/// that cannot be read does not end the pass that reads the others.
/// </summary>
/// <param name="Detail">What GitHub said, shortened. Never a header, and never the credential.</param>
/// <param name="ETag">The answer's entity tag, to send back as <c>If-None-Match</c>.</param>
/// <param name="RetryAt">For <see cref="GitHubOutcome.RateLimited"/>: when asking again is allowed.</param>
public readonly record struct GitHubResult<T>(
    GitHubOutcome Outcome,
    T? Value,
    string? Detail = null,
    string? ETag = null,
    DateTimeOffset? RetryAt = null)
{
    public bool Ok => Outcome == GitHubOutcome.Ok;

    /// <summary>One line for a status row or a log: the class, and what GitHub said.</summary>
    public string Describe() => Outcome switch
    {
        GitHubOutcome.RateLimited when RetryAt is { } at =>
            $"rate limited until {at.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} UTC",
        GitHubOutcome.Unauthorized => $"the token was refused ({Detail ?? "no reason given"})",
        GitHubOutcome.Unreachable => $"no answer ({Detail ?? "no reason given"})",
        _ => string.IsNullOrEmpty(Detail) ? Outcome.ToString() : $"{Outcome}: {Detail}",
    };
}

/// <summary>An account is a login AND a number, and only the number is for ever.</summary>
public sealed record GitHubAccount(string Login, long Id);

public sealed record GitHubIssue(
    int Number,
    string NodeId,
    string Title,
    string? Body,
    string State,
    string Url,
    GitHubAccount Author,
    IReadOnlyList<GitHubAccount> Assignees,
    IReadOnlyList<string> Labels,
    string? Type)
{
    public bool IsOpen => string.Equals(State, "open", StringComparison.OrdinalIgnoreCase);

    public bool IsAssignedTo(string login) =>
        Assignees.Any(a => string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase));
}

/// <param name="HasMore">GitHub held more than one page. The caller was given the first one only.</param>
public sealed record GitHubIssuePage(IReadOnlyList<GitHubIssue> Issues, bool HasMore);

/// <param name="Id">Beyond 32 bits on github.com since 2024.</param>
public sealed record GitHubComment(
    long Id,
    string Body,
    GitHubAccount Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Url);

public sealed record GitHubPullRequest(
    int Number,
    string State,
    bool Draft,
    bool Merged,
    DateTimeOffset? MergedAt,
    string Url,
    string? HeadRef);

/// <summary>What the agent asks of GitHub. Seven calls, and nothing that deletes or closes.</summary>
public interface IGitHubClient
{
    /// <summary>Whose token this is.</summary>
    Task<GitHubResult<GitHubAccount>> GetAuthenticatedUserAsync(CancellationToken ct);

    /// <summary>
    /// The open issues of a repository assigned to <paramref name="assignee"/>, newest first,
    /// one page of <see cref="GitHubClient.PageSize"/>. Pull requests, which GitHub lists among
    /// issues, are left out. With <paramref name="etag"/>, an unchanged list is
    /// <see cref="GitHubOutcome.NotModified"/>.
    /// </summary>
    Task<GitHubResult<GitHubIssuePage>> ListAssignedIssuesAsync(string repository, string assignee, string? etag, CancellationToken ct);

    Task<GitHubResult<GitHubIssue>> GetIssueAsync(string repository, int number, CancellationToken ct);

    /// <summary>
    /// An issue's comments, oldest first. <paramref name="since"/> is inclusive and GitHub's
    /// times are whole seconds, so the comment a caller last saw comes back: know it by its id.
    /// </summary>
    Task<GitHubResult<IReadOnlyList<GitHubComment>>> ListCommentsAsync(string repository, int number, DateTimeOffset? since, CancellationToken ct);

    Task<GitHubResult<GitHubComment>> CreateCommentAsync(string repository, int number, string body, CancellationToken ct);

    Task<GitHubResult<GitHubComment>> UpdateCommentAsync(string repository, long commentId, string body, CancellationToken ct);

    Task<GitHubResult<GitHubPullRequest>> GetPullRequestAsync(string repository, int number, CancellationToken ct);
}

/// <summary>
/// When GitHub last said to stop asking. One for the process, shared by every client the factory
/// builds: a rate limit is the account's, not a request's.
/// </summary>
public sealed class GitHubRateLimit
{
    private long untilUnixSeconds;

    public DateTimeOffset? Until =>
        Interlocked.Read(ref untilUnixSeconds) is > 0 and var s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    public void Hold(DateTimeOffset until) => Interlocked.Exchange(ref untilUnixSeconds, until.ToUnixTimeSeconds());

    public void Clear() => Interlocked.Exchange(ref untilUnixSeconds, 0);
}

/// <summary>
/// GitHub's REST API over a plain <see cref="HttpClient"/>: no SDK, a base URL that can be
/// pointed anywhere, and failures as values.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rate limit is honoured here, not by each caller.</b> When GitHub answers that the limit
/// is used up, or sends <c>retry-after</c>, every call until that time is answered
/// <see cref="GitHubOutcome.RateLimited"/> without a request. GitHub's terms for a client that
/// keeps asking through a limit are a longer limit and then a ban, and a poller's next pass is
/// five seconds away on a dev cluster.
/// </para>
/// <para>
/// <b>It retries nothing.</b> The standard resilience handler is removed from this client where
/// it is registered: the poller is level-triggered, so its next pass is the retry, and three
/// hidden attempts per call against an API that is failing is the hammering the paragraph above
/// exists to prevent.
/// </para>
/// </remarks>
public sealed class GitHubClient(
    HttpClient http,
    IOptions<GitHubOptions> options,
    GitHubRateLimit rateLimit,
    IClock clock,
    ILogger<GitHubClient> logger) : IGitHubClient
{
    /// <summary>GitHub's largest page. A repository with more open issues assigned to one account than this is not a case worth a second request per pass.</summary>
    public const int PageSize = 100;

    /// <summary>What a rate limit that names no time is taken to mean. GitHub's own advice for a secondary limit.</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    private const int MaxDetail = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<GitHubResult<GitHubAccount>> GetAuthenticatedUserAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "user", null, null, (json, _) => Account(json), ct);

    public Task<GitHubResult<GitHubIssuePage>> ListAssignedIssuesAsync(string repository, string assignee, string? etag, CancellationToken ct) =>
        SendAsync(
            HttpMethod.Get,
            $"repos/{repository}/issues?assignee={Uri.EscapeDataString(assignee)}&state=open&per_page={PageSize}",
            null,
            etag,
            (json, response) =>
            {
                var all = json.EnumerateArray().ToList();

                return new GitHubIssuePage(
                    // GitHub's issue list is issues AND pull requests; the latter carry this key.
                    [.. all.Where(i => !i.TryGetProperty("pull_request", out _)).Select(Issue)],
                    HasNextPage(response) || all.Count >= PageSize);
            },
            ct);

    public Task<GitHubResult<GitHubIssue>> GetIssueAsync(string repository, int number, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"repos/{repository}/issues/{Number(number)}", null, null, (json, _) => Issue(json), ct);

    public Task<GitHubResult<IReadOnlyList<GitHubComment>>> ListCommentsAsync(string repository, int number, DateTimeOffset? since, CancellationToken ct)
    {
        var path = $"repos/{repository}/issues/{Number(number)}/comments?per_page={PageSize}";

        if (since is { } from)
        {
            path += "&since=" + Uri.EscapeDataString(from.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        return SendAsync<IReadOnlyList<GitHubComment>>(
            HttpMethod.Get, path, null, null, (json, _) => [.. json.EnumerateArray().Select(Comment)], ct);
    }

    public Task<GitHubResult<GitHubComment>> CreateCommentAsync(string repository, int number, string body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"repos/{repository}/issues/{Number(number)}/comments", new { body }, null, (json, _) => Comment(json), ct);

    public Task<GitHubResult<GitHubComment>> UpdateCommentAsync(string repository, long commentId, string body, CancellationToken ct) =>
        SendAsync(
            HttpMethod.Patch,
            $"repos/{repository}/issues/comments/{commentId.ToString(CultureInfo.InvariantCulture)}",
            new { body },
            null,
            (json, _) => Comment(json),
            ct);

    public Task<GitHubResult<GitHubPullRequest>> GetPullRequestAsync(string repository, int number, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"repos/{repository}/pulls/{Number(number)}", null, null, (json, _) => PullRequest(json), ct);

    private async Task<GitHubResult<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        string? etag,
        Func<JsonElement, HttpResponseMessage, T> read,
        CancellationToken ct)
    {
        var now = clock.UtcNow;

        if (rateLimit.Until is { } until)
        {
            if (until > now)
            {
                return new(GitHubOutcome.RateLimited, default, "waiting for the limit GitHub named earlier", RetryAt: until);
            }

            rateLimit.Clear();
        }

        var o = options.Value;

        using var request = new HttpRequestMessage(method, new Uri(o.ApiBase(), path));

        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("hephaisto");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.Token.Trim());

        if (!string.IsNullOrEmpty(etag))
        {
            // As given: GitHub's tags for JSON are weak (W/"..."), and a parsed-and-rewritten one
            // is a different string that never matches.
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            var tag = response.Headers.TryGetValues("ETag", out var tags) ? tags.FirstOrDefault() : null;

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new(GitHubOutcome.NotModified, default, ETag: tag ?? etag);
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(text);

                return new(GitHubOutcome.Ok, read(document.RootElement, response), ETag: tag);
            }

            var detail = $"HTTP {(int)response.StatusCode}: {Message(text)}";

            if (RateLimitedUntil(response, now) is { } retryAt)
            {
                rateLimit.Hold(retryAt);

                logger.LogWarning(
                    "GitHub rate limit on {Method} {Path}: not asking again before {RetryAt:O}. {Detail}",
                    method.Method, PathOnly(path), retryAt, detail);

                return new(GitHubOutcome.RateLimited, default, detail, RetryAt: retryAt);
            }

            var outcome = (int)response.StatusCode switch
            {
                401 or 403 => GitHubOutcome.Unauthorized,
                404 or 410 => GitHubOutcome.NotFound,
                >= 500 => GitHubOutcome.ServerError,
                _ => GitHubOutcome.Rejected,
            };

            return new(outcome, default, detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // The client's own timeout is a TaskCanceledException, which is not the caller
            // stopping: it is GitHub not answering.
            return new(GitHubOutcome.Unreachable, default, Trim(SecretRedactor.Redact(ex.Message)));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // A 200 that is not what GitHub sends: a proxy's error page, a captive portal.
            return new(GitHubOutcome.Rejected, default, "the answer was not GitHub's JSON: " + Trim(ex.Message));
        }
    }

    /// <summary>
    /// When a refusal is a rate limit, and until when. A primary limit is a 403 or 429 with
    /// <c>x-ratelimit-remaining: 0</c> and the reset as a Unix time; a secondary one carries
    /// <c>retry-after</c> in seconds. A 403 with neither is a permission problem, not a limit.
    /// </summary>
    private static DateTimeOffset? RateLimitedUntil(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return null;
        }

        if (Header(response, "retry-after") is { } after
            && int.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return now.AddSeconds(Math.Max(1, seconds));
        }

        if (Header(response, "x-ratelimit-remaining") == "0")
        {
            // The second after, so that a clock a little ahead of GitHub's does not ask in the
            // limit's last moment and earn another one. A reset that is missing, unreadable or
            // already past is no reason to ask at once.
            return Header(response, "x-ratelimit-reset") is { } reset
                && long.TryParse(reset, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)
                && unix > now.ToUnixTimeSeconds()
                && unix < now.AddDays(1).ToUnixTimeSeconds()
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).AddSeconds(1)
                    : now.Add(DefaultRetryAfter);
        }

        return response.StatusCode == HttpStatusCode.TooManyRequests ? now.Add(DefaultRetryAfter) : null;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    private static bool HasNextPage(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Link", out var links) && links.Any(l => l.Contains("rel=\"next\"", StringComparison.Ordinal));

    /// <summary>GitHub's own sentence from an error body, or the start of whatever came instead.</summary>
    private static string Message(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return Trim(message.GetString()!);
            }
        }
        catch (JsonException)
        {
            // Not JSON: an HTML error page from something in between. Its first line is still
            // more use than nothing.
        }

        return Trim(body.ReplaceLineEndings(" "));
    }

    private static string Trim(string text) => text.Length <= MaxDetail ? text : text[..MaxDetail] + "…";

    private static string PathOnly(string path) => path.Split('?', 2)[0];

    private static string Number(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static GitHubAccount Account(JsonElement json) =>
        new(json.GetProperty("login").GetString() ?? string.Empty, json.GetProperty("id").GetInt64());

    private static GitHubAccount AccountOrGhost(JsonElement json, string name) =>
        // A deleted account's issues and comments stay, with "user": null. GitHub shows them as
        // @ghost, whose number is nobody's.
        json.TryGetProperty(name, out var user) && user.ValueKind == JsonValueKind.Object ? Account(user) : new("ghost", 0);

    private static GitHubIssue Issue(JsonElement json) => new(
        json.GetProperty("number").GetInt32(),
        Text(json, "node_id") ?? string.Empty,
        Text(json, "title") ?? string.Empty,
        Text(json, "body"),
        Text(json, "state") ?? string.Empty,
        Text(json, "html_url") ?? string.Empty,
        AccountOrGhost(json, "user"),
        json.TryGetProperty("assignees", out var assignees) && assignees.ValueKind == JsonValueKind.Array
            ? [.. assignees.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object).Select(Account)]
            : [],
        json.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
            // An object with a name, or - from older API versions - the name itself.
            ? [.. labels.EnumerateArray()
                .Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : Text(l, "name"))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l!)]
            : [],
        json.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Object ? Text(type, "name") : null);

    private static GitHubComment Comment(JsonElement json) => new(
        json.GetProperty("id").GetInt64(),
        Text(json, "body") ?? string.Empty,
        AccountOrGhost(json, "user"),
        json.GetProperty("created_at").GetDateTimeOffset(),
        json.GetProperty("updated_at").GetDateTimeOffset(),
        Text(json, "html_url") ?? string.Empty);

    private static GitHubPullRequest PullRequest(JsonElement json) => new(
        json.GetProperty("number").GetInt32(),
        Text(json, "state") ?? string.Empty,
        json.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
        json.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True,
        json.TryGetProperty("merged_at", out var at) && at.ValueKind == JsonValueKind.String ? at.GetDateTimeOffset() : null,
        Text(json, "html_url") ?? string.Empty,
        json.TryGetProperty("head", out var head) && head.ValueKind == JsonValueKind.Object ? Text(head, "ref") : null);

    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
