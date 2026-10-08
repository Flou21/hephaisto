using System.Diagnostics.Metrics;

using Hephaisto.Core.CodeFix;
using Hephaisto.Core.Telemetry;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// <c>hephaisto.workitems.commands</c> (v0.14.0, #248), counted by the real poller over the real
/// database: once per command, by what was done with it.
/// </summary>
public sealed partial class WorkItemStageTests
{
    [Fact]
    public async Task Every_command_on_an_issue_is_counted_once_by_what_was_done_with_it()
    {
        var seen = new List<string>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == HephaistoTelemetry.Metrics.WorkItemCommands)
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

            lock (seen)
            {
                seen.Add($"{verb} {outcome} {value}");
            }
        });
        listener.Start();

        // Plan mode: an approval is refused by the door, for the mode.
        var (world, issue, _, _) = await PlanOnTheIssueAsync(mode: "plan");

        world.GitHub.Comment(Repo, issue, Passerby, "/approve", PasserbyId);
        world.GitHub.Comment(Repo, issue, Passerby, "looks good to me", PasserbyId);
        world.GitHub.Comment(Repo, issue, Maintainer, "/approve", MaintainerId);
        await world.Poller().PassAsync(Ct);

        // Nothing new on the issue: nothing is counted twice, by this poller or by one that
        // starts from the database.
        await world.Poller().PassAsync(Ct);
        await world.Poller().PassAsync(Ct);

        world.GitHub.Comment(Repo, issue, Maintainer, "/reject not this way", MaintainerId);
        await world.Poller().PassAsync(Ct);
        await world.Poller().PassAsync(Ct);

        (await AttemptAsync()).State.Should().Be(CodeFixState.Denied);

        seen.Should().Equal(
            "approve not_approver 1",
            "approve refused:mode-plan 1",
            "reject accepted 1");
    }
}
