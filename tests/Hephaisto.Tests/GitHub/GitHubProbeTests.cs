using Hephaisto.Agent.GitHub;
using Hephaisto.Agent.Observability;
using Hephaisto.Tests.TestData;
using Microsoft.Extensions.Options;

namespace Hephaisto.Tests.GitHub;

/// <summary>
/// GitHub's row in the connections panel: what the poller last saw, in one line.
/// </summary>
public sealed class GitHubProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (GitHubProbe Probe, GitHubHealth Health) Build(bool enabled = true)
    {
        var health = new GitHubHealth();

        return (new GitHubProbe(health, Options.Create(new GitHubOptions { Enabled = enabled }), Given.Clock()), health);
    }

    [Fact]
    public async Task Not_enabled_is_not_configured_and_not_a_fault()
    {
        var (probe, _) = Build(enabled: false);

        var report = await probe.ProbeAsync(Ct);

        report.Name.Should().Be("github");
        report.State.Should().Be(ConnectionState.NotConfigured);
        report.Detail.Should().Contain("GitHub:Enabled is false");
    }

    [Fact]
    public async Task Every_repository_answering_is_healthy_and_a_304_is_an_answer()
    {
        var (probe, health) = Build();
        health.Succeeded("octo/shop", "2 assigned", Given.Now.AddSeconds(-30));
        health.Succeeded("octo/api", "unchanged", Given.Now.AddSeconds(-10));
        health.PassEnded();

        var report = await probe.ProbeAsync(Ct);

        report.State.Should().Be(ConnectionState.Healthy);
        report.Detail.Should().Be("2 repositories polled, last at 11:59:50 UTC.");
        report.CheckedAt.Should().Be(Given.Now.AddSeconds(-10), "the row carries when GitHub last answered, not when somebody looked");
    }

    [Fact]
    public async Task One_repository_failing_is_degraded_with_its_reason_in_one_line()
    {
        var (probe, health) = Build();
        health.Succeeded("octo/api", "unchanged", Given.Now);
        health.Failed("octo/shop", "rate limited until 12:00:21 UTC", Given.Now);
        health.Failed("octo/web", "rate limited until 12:00:21 UTC", Given.Now);
        health.PassEnded();

        var report = await probe.ProbeAsync(Ct);

        report.State.Should().Be(ConnectionState.Degraded);
        report.Detail.Should().Be("octo/shop: rate limited until 12:00:21 UTC (and 1 more of 3)");
        report.Detail.Should().NotContain("\n");
    }

    [Fact]
    public async Task It_recovers_when_the_next_pass_succeeds()
    {
        var (probe, health) = Build();
        health.Failed("octo/shop", "ServerError: HTTP 500: Server Error", Given.Now);
        health.PassEnded();
        (await probe.ProbeAsync(Ct)).State.Should().Be(ConnectionState.Degraded);

        health.Succeeded("octo/shop", "unchanged", Given.Now.AddSeconds(5));
        health.PassEnded();

        (await probe.ProbeAsync(Ct)).State.Should().Be(ConnectionState.Healthy);
    }

    [Fact]
    public async Task With_the_agent_off_it_says_it_is_not_polling_rather_than_that_all_is_well()
    {
        var (probe, health) = Build();
        health.Succeeded("octo/shop", "unchanged", Given.Now.AddHours(-3));
        health.PassEnded("the agent is Off (env:HEPHAISTO_MODE)");

        var report = await probe.ProbeAsync(Ct);

        report.State.Should().Be(ConnectionState.NotConfigured);
        report.Detail.Should().Be("Not polling: the agent is Off (env:HEPHAISTO_MODE).");
    }

    [Fact]
    public async Task A_pass_that_recorded_nothing_is_not_called_healthy()
    {
        var (probe, health) = Build();
        health.PassEnded();

        var report = await probe.ProbeAsync(Ct);

        report.State.Should().Be(ConnectionState.Degraded);
        report.Detail.Should().Be("No poll pass has finished yet.");
    }

    [Fact]
    public async Task The_first_answer_after_a_start_waits_for_the_first_pass()
    {
        var (probe, health) = Build();

        var pending = probe.ProbeAsync(Ct);
        pending.IsCompleted.Should().BeFalse("nothing has been polled yet, and the next refresh is a minute away");

        health.Succeeded("octo/shop", "1 assigned", Given.Now);
        health.PassEnded();

        (await pending).State.Should().Be(ConnectionState.Healthy);
    }
}
