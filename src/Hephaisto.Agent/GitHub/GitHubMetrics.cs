using System.Diagnostics.Metrics;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.Agent.GitHub;

/// <summary>
/// The instruments of issues as work. Every label is a closed set - an outcome, a state, a reason
/// code - never a repository, an issue number or a sentence.
/// </summary>
public sealed class GitHubMetrics
{
    public const string ReasonClosed = "issue_closed";
    public const string ReasonUnassigned = "unassigned";
    public const string ReasonGone = "issue_gone";
    public const string ReasonMerged = "merged";
    public const string ReasonPullRequestClosed = "pull_request_closed";

    private readonly Counter<long> polls;
    private readonly Counter<long> taken;
    private readonly Counter<long> closed;
    private readonly Counter<long> commands;

    public GitHubMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(HephaistoTelemetry.MeterName);

        polls = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.GitHubPolls);
        taken = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.WorkItemsTaken);
        closed = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.WorkItemsClosed);
        commands = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.WorkItemCommands);
    }

    /// <summary>One repository was asked for its assigned issues.</summary>
    public void Polled(GitHubOutcome outcome) => polls.Add(1, new KeyValuePair<string, object?>("outcome", Label(outcome)));

    public void Taken(string source) => taken.Add(1, new KeyValuePair<string, object?>("source", source));

    public void Closed(WorkItemState state, string reason) =>
        closed.Add(1, new("state", state.ToString()), new("reason", reason));

    public const string CommandAccepted = "accepted";
    public const string CommandNotApprover = "not_approver";

    /// <summary>
    /// One command on an issue was dealt with. <paramref name="outcome"/> is
    /// <see cref="CommandAccepted"/>, <see cref="CommandNotApprover"/> or
    /// <see cref="CommandRefused"/> of the door's cause - each a closed set.
    /// </summary>
    public void Command(bool approve, string outcome) =>
        commands.Add(1, new("verb", approve ? "approve" : "reject"), new("outcome", outcome));

    /// <summary><c>refused:&lt;cause&gt;</c>; the cause is the key of the one-time answer (<c>IssueComments.AnswerKey</c>).</summary>
    public static string CommandRefused(string cause) => $"refused:{cause}";

    public static string Label(GitHubOutcome outcome) => outcome switch
    {
        GitHubOutcome.Ok => "ok",
        GitHubOutcome.NotModified => "not_modified",
        GitHubOutcome.NotFound => "not_found",
        GitHubOutcome.Unauthorized => "unauthorized",
        GitHubOutcome.RateLimited => "rate_limited",
        GitHubOutcome.ServerError => "server_error",
        GitHubOutcome.Unreachable => "unreachable",
        _ => "rejected",
    };
}
