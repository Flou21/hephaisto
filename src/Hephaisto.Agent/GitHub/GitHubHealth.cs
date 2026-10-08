using System.Collections.Concurrent;
using System.Globalization;
using Hephaisto.Agent.Observability;
using Hephaisto.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Hephaisto.Agent.GitHub;

/// <summary>How the last poll of one repository ended.</summary>
public sealed record GitHubRepositoryPoll(string Repository, bool Succeeded, string Detail, DateTimeOffset At);

/// <summary>
/// What the poller last learned about GitHub, per repository. In memory on purpose: it describes
/// this process's last pass, and after a restart the first pass says it again.
/// </summary>
public sealed class GitHubHealth
{
    private readonly ConcurrentDictionary<string, GitHubRepositoryPoll> polls = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource firstPass = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string? paused;

    /// <summary>Completes when the first pass has ended, however it ended.</summary>
    public Task FirstPass => firstPass.Task;

    /// <summary>Why the poller is not asking at all, or null when it is.</summary>
    public string? Paused => paused;

    public IReadOnlyList<GitHubRepositoryPoll> Polls =>
        [.. polls.Values.OrderBy(p => p.Repository, StringComparer.OrdinalIgnoreCase)];

    public void Succeeded(string repository, string detail, DateTimeOffset at) =>
        polls[repository] = new(repository, true, detail, at);

    public void Failed(string repository, string detail, DateTimeOffset at) =>
        polls[repository] = new(repository, false, detail, at);

    /// <summary>A pass ended. <paramref name="pausedBecause"/> is set when it asked nothing on purpose.</summary>
    public void PassEnded(string? pausedBecause = null)
    {
        paused = pausedBecause;
        firstPass.TrySetResult();
    }
}

/// <summary>
/// GitHub, reported from what the poller last saw rather than by a call of its own.
/// </summary>
/// <remarks>
/// <b>Deliberately not a live check.</b> The question the row answers is "can issues be taken as
/// work right now", and the poller has just asked exactly that of every listed repository - with
/// the token, through the proxy, against the rate limit. A second request here would spend the
/// limit to learn less. A 304 counts as success: it is GitHub answering.
/// </remarks>
public sealed class GitHubProbe(
    GitHubHealth health,
    IOptions<GitHubOptions> options,
    IClock clock) : IConnectionProbe
{
    /// <summary>
    /// How long the first answer after a start waits for the first pass. Without it the row
    /// would say "nothing polled yet" for a whole refresh interval on every start.
    /// </summary>
    internal static readonly TimeSpan FirstPassWait = TimeSpan.FromSeconds(5);

    public string Name => "github";

    public async Task<ConnectionReport> ProbeAsync(CancellationToken ct)
    {
        var o = options.Value;

        if (!o.Enabled)
        {
            return ConnectionReport.NotConfigured(
                Name, "GitHub:Enabled is false, so no issue is ever taken as work.", clock.UtcNow);
        }

        if (!health.FirstPass.IsCompleted)
        {
            await Task.WhenAny(health.FirstPass, Task.Delay(FirstPassWait, ct)).ConfigureAwait(false);
        }

        if (health.Paused is { } paused)
        {
            // Deliberate, like an integration that is unset: nothing is asked while the agent is
            // Off, so there is nothing to call healthy or broken.
            return ConnectionReport.NotConfigured(Name, $"Not polling: {paused}.", clock.UtcNow);
        }

        var polls = health.Polls;

        if (polls.Count == 0)
        {
            return new ConnectionReport(Name, ConnectionState.Degraded, "No poll pass has finished yet.", clock.UtcNow);
        }

        var at = polls.Max(p => p.At);
        var failed = polls.Where(p => !p.Succeeded).ToArray();

        if (failed.Length == 0)
        {
            return new ConnectionReport(
                Name,
                ConnectionState.Healthy,
                polls.Count == 1
                    ? $"{polls[0].Repository} polled at {Time(at)} UTC."
                    : $"{polls.Count} repositories polled, last at {Time(at)} UTC.",
                at);
        }

        // One line: the first failure in full, the rest as a count. The reasons are usually one
        // reason - a token or a rate limit is the account's, not a repository's.
        var more = failed.Length > 1 ? $" (and {failed.Length - 1} more of {polls.Count})" : string.Empty;

        return new ConnectionReport(
            Name,
            ConnectionState.Degraded,
            PostgresProbe.Trim($"{failed[0].Repository}: {failed[0].Detail}{more}"),
            at);
    }

    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
