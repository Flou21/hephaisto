using System.Diagnostics.Metrics;
using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.WorkItems;
using Hephaisto.Agent.CodeFix;
using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Hephaisto.Tests.GitHub;

/// <summary>
/// <c>hephaisto.workitems.commands</c> (v0.14.0, #248): what was done with each command read off
/// an issue, by verb and outcome - and every label a member of a closed set.
/// </summary>
public sealed class GitHubCommandMetricTests
{
    [Fact]
    public void A_command_is_counted_by_verb_and_outcome()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var seen = new List<(long Value, string Verb, string Outcome)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            // This factory's meter only: another test's poller counts on its own.
            if (instrument.Name == HephaistoTelemetry.Metrics.WorkItemCommands && ReferenceEquals(instrument.Meter.Scope, factory))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string verb = string.Empty, outcome = string.Empty;

            foreach (var tag in tags)
            {
                if (tag.Key == "verb") verb = (string)tag.Value!;
                if (tag.Key == "outcome") outcome = (string)tag.Value!;
            }

            seen.Add((value, verb, outcome));
        });
        listener.Start();

        var metrics = new GitHubMetrics(factory);

        metrics.Command(approve: true, GitHubMetrics.CommandAccepted);
        metrics.Command(approve: false, GitHubMetrics.CommandAccepted);
        metrics.Command(approve: true, GitHubMetrics.CommandNotApprover);
        metrics.Command(approve: true, GitHubMetrics.CommandRefused(IssueComments.AnswerKey(CodeFixRefusal.ModeBelowPr, CodeFixMode.Plan)));

        seen.Should().Equal(
            (1, "approve", "accepted"),
            (1, "reject", "accepted"),
            (1, "approve", "not_approver"),
            (1, "approve", "refused:mode-plan"));
    }

    [Fact]
    public void A_refusals_cause_is_one_of_the_doors_and_never_a_sentence()
    {
        // The label is built from the key of the one-time answer, so the set of outcomes is the
        // set of causes the door has - a new member of CodeFixRefusal is a new, known label.
        var causes = Enum.GetValues<CodeFixRefusal>()
            .SelectMany(r => new[] { IssueComments.AnswerKey(r, CodeFixMode.Plan), IssueComments.AnswerKey(r, CodeFixMode.Off) })
            .Distinct()
            .ToList();

        causes.Should().BeEquivalentTo(
            ["mode-plan", "mode-off", "emergency-stop", "kill-switch", "second-repository", "not-waiting", "taken-back", "refused"]);
        causes.Select(GitHubMetrics.CommandRefused).Should().OnlyContain(o => System.Text.RegularExpressions.Regex.IsMatch(o, "^refused:[a-z-]+$"));
    }
}
