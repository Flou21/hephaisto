using System.Globalization;
using System.Text.RegularExpressions;

namespace Hephaisto.Agent.GitHub;

/// <summary>
/// GitHub issues as work (v0.14.0). Bound from <c>GitHub:*</c>; the chart emits <c>GitHub__*</c>.
/// </summary>
/// <remarks>
/// Every default is inert: not enabled, no repository listed. An install that never heard of this
/// holds no GitHub credential and asks GitHub nothing.
/// </remarks>
public sealed partial class GitHubOptions
{
    public const string SectionName = "GitHub";

    /// <summary>The fastest the poller may be told to go. GitHub's own guidance for polling is a minute.</summary>
    public static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(5);

    public bool Enabled { get; set; }

    /// <summary>
    /// The REST API's root. GitHub Enterprise Server's is <c>https://host/api/v3</c>; a test's is a
    /// stand-in. A path in it is kept: every call is made relative to it.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary>
    /// The account's token, from the environment variable <c>GitHub__Token</c>, which the chart
    /// fills from a Secret of the AGENT's namespace (<c>secrets.github</c>). Never the coder's
    /// Secret: that one the agent cannot read, and must stay unable to.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// The account issues are assigned to. Empty means whoever the token belongs to
    /// (<c>GET /user</c>), which is the right answer unless the token is somebody else's.
    /// </summary>
    public string BotLogin { get; set; } = string.Empty;

    /// <summary>
    /// The authorization list: <c>owner/repo</c>. An issue in a repository that is not listed is
    /// never asked about, however it is assigned. Empty lists nothing.
    /// </summary>
    public List<string> Repositories { get; set; } = [];

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>An HTTP proxy for every call to GitHub, or empty for a direct connection.</summary>
    public string ProxyUrl { get; set; } = string.Empty;

    /// <summary>
    /// Who may answer a plan on the issue, by account NUMBER. Kept as the strings the operator
    /// typed so that a login where a number belongs is refused with a sentence rather than with
    /// a binder's exception. Stored and checked here; read when a comment is a command.
    /// </summary>
    public List<string> Approvers { get; set; } = [];

    /// <summary>The approvers as numbers. Only meaningful once <see cref="Problems"/> is empty.</summary>
    public IReadOnlySet<long> ApproverIds() =>
        Approvers
            .Select(a => long.TryParse(a.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();

    /// <summary>The API root with exactly one trailing slash, so a relative path is appended to it and not to its parent.</summary>
    public Uri ApiBase() => new(ApiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);

    /// <summary>
    /// Everything wrong with this configuration, one sentence each. Empty when it may start -
    /// and always empty when the feature is off, because an option nobody enabled is not a fault.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        if (!Enabled)
        {
            return [];
        }

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Token))
        {
            problems.Add(
                "GitHub:Enabled needs GitHub:Token: the account's token, from the Secret secrets.github names "
                + "(key GITHUB_TOKEN). Without one every call is refused and nothing is ever taken.");
        }

        if (Repositories.Count == 0)
        {
            problems.Add(
                "GitHub:Enabled needs at least one entry in GitHub:Repositories (owner/repo): the list is "
                + "what authorizes a repository, and an empty one polls nothing.");
        }

        foreach (var repository in Repositories.Where(r => !RepositoryName().IsMatch(r)))
        {
            problems.Add($"GitHub:Repositories entry '{repository}' is not owner/repo.");
        }

        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var api) || api.Scheme is not ("http" or "https"))
        {
            problems.Add($"GitHub:ApiBaseUrl '{ApiBaseUrl}' is not an absolute http(s) URL.");
        }

        if (!string.IsNullOrWhiteSpace(ProxyUrl)
            && (!Uri.TryCreate(ProxyUrl, UriKind.Absolute, out var proxy) || proxy.Scheme is not ("http" or "https")))
        {
            problems.Add($"GitHub:ProxyUrl '{ProxyUrl}' is not an absolute http(s) URL.");
        }

        if (PollInterval < MinimumPollInterval)
        {
            problems.Add(
                $"GitHub:PollInterval must be at least {MinimumPollInterval.TotalSeconds:0} seconds; "
                + "GitHub asks a poller for a minute.");
        }

        foreach (var approver in Approvers.Where(a => !long.TryParse(a.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0))
        {
            problems.Add(
                $"GitHub:Approvers entry '{approver}' is not an account number. A login can be renamed and "
                + "taken by somebody else; list the numeric id (gh api users/<login> --jq .id).");
        }

        return problems;
    }

    // GitHub's own rules, loosely: an owner is letters, digits and hyphens; a repository adds
    // dots and underscores. Loose is enough - the point is to refuse a URL or a bare name.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+$")]
    private static partial Regex RepositoryName();
}
