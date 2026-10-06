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

    private readonly Counter<long> polls;
    private readonly Counter<long> taken;
    private readonly Counter<long> closed;

    public GitHubMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(HephaistoTelemetry.MeterName);

        polls = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.GitHubPolls);
        taken = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.WorkItemsTaken);
        closed = meter.CreateCounter<long>(HephaistoTelemetry.Metrics.WorkItemsClosed);
    }

    /// <summary>One repository was asked for its assigned issues.</summary>
    public void Polled(GitHubOutcome outcome) => polls.Add(1, new KeyValuePair<string, object?>("outcome", Label(outcome)));

    public void Taken(string source) => taken.Add(1, new KeyValuePair<string, object?>("source", source));

    public void Closed(WorkItemState state, string reason) =>
        closed.Add(1, new("state", state.ToString()), new("reason", reason));

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
