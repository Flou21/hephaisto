using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Persistence.Repositories;
using Hephaisto.Agent.Safety;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Safety;
using NSubstitute;

namespace Hephaisto.Tests;

/// <summary>
/// What the database arm of the kill switch is allowed to say.
/// </summary>
/// <remarks>
/// <para>
/// The mode is a Helm value. It reaches the pod on the environment variable and the projected
/// ConfigMap, so raising autonomy is a reviewed commit rather than a click. The database arm
/// exists to <b>restrain</b> - it carries the runaway latch - and must never be the reason a
/// deployment's configured mode fails to take effect.
/// </para>
/// <para>
/// It was exactly that until v0.2.0. The arm declared the <c>agent_mode</c> row's mode column,
/// the InitialCreate migration seeds that column to <c>Observe</c>, and the resolver takes the
/// minimum over every arm that speaks - so <c>mode: Auto</c> in the chart resolved to Observe
/// on every database that had ever been migrated, and the only way to lift it was a
/// hand-written UPDATE against Postgres. Nothing failed; it just never worked.
/// </para>
/// </remarks>
public sealed class AgentModeDatabaseArmTests
{
    private static KillSwitch Build(AgentModeRow? row, string envMode = "Auto")
    {
        var store = Substitute.For<IAgentModeStore>();
        store.GetRowOrDefaultAsync(Arg.Any<CancellationToken>()).Returns(row);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HEPHAISTO_MODE"] = envMode })
            .Build();

        var scopes = new ServiceCollection()
            .AddScoped(_ => store)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        return new KillSwitch(
            configuration,
            new Stub(new KillSwitchOptions { SwitchDirectory = null }),
            scopes,
            NullLogger<KillSwitch>.Instance);
    }

    private static AgentModeRow Row(bool latched = false) =>
        new()
        {
            Id = AgentModeRow.SingletonId,
            RunawayLatched = latched,
            LatchReason = latched ? "spend runaway" : null,
            ChangedAt = DateTimeOffset.UnixEpoch,
        };

    [Fact]
    public async Task An_unlatched_row_is_silent_so_the_charts_mode_takes_effect()
    {
        // The regression. Before v0.2.0 this resolved to Observe.
        var resolved = await Build(Row()).ResolveAsync(CancellationToken.None);

        resolved.Effective.Should().Be(AgentMode.Auto);
        resolved.Arms.Single(a => a.Name == KillSwitch.DatabaseArm)
            .Status.Should().Be(ModeArmStatus.Silent);
    }

    [Fact]
    public async Task The_row_cannot_express_a_mode_at_all()
    {
        // agent_mode.mode is GONE, dropped in the ActingSchema migration, so "the column is
        // ignored" is now structural rather than something this test has to police. What is
        // left to pin is that the row carries no way to say anything about the mode - if a
        // column like it ever comes back, the arm has to stay silent about it.
        typeof(AgentModeRow).GetProperties()
            .Select(p => p.PropertyType)
            .Should().NotContain(typeof(AgentMode), "the database does not decide the mode; the Helm values do");

        var resolved = await Build(Row(), envMode: "DryRun").ResolveAsync(CancellationToken.None);

        resolved.Effective.Should().Be(AgentMode.DryRun);
    }

    [Fact]
    public async Task A_latched_row_still_floors_the_agent_at_Observe()
    {
        // The arm keeps the one power it should have: stopping things.
        var resolved = await Build(Row(latched: true)).ResolveAsync(CancellationToken.None);

        resolved.Effective.Should().Be(AgentMode.Observe);
        resolved.DecidedBy.Should().Be(KillSwitch.DatabaseArm);
        resolved.IsConstrained.Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_row_is_an_anomaly_and_floors_the_agent()
    {
        // Distinct from an unlatched row, and deliberately so: the migration seeds the row, so
        // its absence means a truncated or half-restored database. Reading that as "no opinion"
        // would turn losing the table into an autonomy upgrade.
        var resolved = await Build(row: null).ResolveAsync(CancellationToken.None);

        resolved.Effective.Should().Be(AgentMode.Observe);
        resolved.Arms.Single(a => a.Name == KillSwitch.DatabaseArm)
            .Status.Should().Be(ModeArmStatus.Unreadable);
    }

    [Fact]
    public async Task The_database_arm_can_never_raise_the_mode()
    {
        // The direction that matters. An operator who sets Observe in the chart to stop an
        // agent must actually stop it, whatever any row says.
        var resolved = await Build(Row(), envMode: "Observe").ResolveAsync(CancellationToken.None);

        resolved.Effective.Should().Be(AgentMode.Observe);
    }

    private sealed class Stub(KillSwitchOptions value) : IOptionsMonitor<KillSwitchOptions>
    {
        public KillSwitchOptions CurrentValue => value;

        public KillSwitchOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<KillSwitchOptions, string?> listener) => null;
    }
}
