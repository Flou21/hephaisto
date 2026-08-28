using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Safety;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Safety;

namespace Hephaisto.Tests;

/// <summary>
/// How the ConfigMap arm reads the projected volume.
/// </summary>
/// <remarks>
/// The distinction under test is the one that decides behaviour in the cluster and is easy
/// to get backwards: <b>no switch directory configured</b> means there is no ConfigMap arm
/// here at all (a developer running <c>dotnet run</c>) and must not constrain, while a
/// <b>configured directory with a missing mode file</b> means a value that should exist has
/// gone, and must constrain to Observe. Collapsing the two either pins every local run to
/// Observe or lets a deleted ConfigMap key restore Auto.
/// </remarks>
public sealed class KillSwitchFileTests : IDisposable
{
    private readonly string directory =
        Directory.CreateTempSubdirectory("hephaisto-switches-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private KillSwitch Build(string? switchDirectory, string? envMode)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(envMode is null
                ? []
                : new Dictionary<string, string?> { ["HEPHAISTO_MODE"] = envMode })
            .Build();

        var options = new OptionsMonitorStub(new KillSwitchOptions { SwitchDirectory = switchDirectory });

        var scopes = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        return new KillSwitch(configuration, options, scopes, NullLogger<KillSwitch>.Instance);
    }

    private ModeArm ArmNamed(KillSwitch sut, string name) =>
        sut.ExternalArms.Single(a => a.Name == name);

    [Fact]
    public void No_switch_directory_makes_the_configmap_arms_silent()
    {
        var sut = Build(switchDirectory: null, envMode: null);

        ArmNamed(sut, KillSwitch.ConfigMapModeArm).Status.Should().Be(ModeArmStatus.Silent);
        ArmNamed(sut, KillSwitch.ConfigMapStopArm).Status.Should().Be(ModeArmStatus.Silent);
        sut.External.Effective.Should().Be(AgentMode.Observe, "no arm spoke, so the default applies");
    }

    /// <summary>Deleting the key out of the ConfigMap must not read as permission.</summary>
    [Fact]
    public void A_configured_directory_with_no_mode_file_is_unreadable()
    {
        var sut = Build(directory, envMode: "auto");

        ArmNamed(sut, KillSwitch.ConfigMapModeArm).Status.Should().Be(ModeArmStatus.Unreadable);
        sut.External.Effective.Should().Be(AgentMode.Observe);
        sut.External.DecidedBy.Should().Be(KillSwitch.ConfigMapModeArm);
    }

    [Fact]
    public void Reads_the_mode_from_the_projected_file()
    {
        File.WriteAllText(Path.Combine(directory, "mode"), "DryRun");

        var sut = Build(directory, envMode: "auto");

        ArmNamed(sut, KillSwitch.ConfigMapModeArm).Declared.Should().Be(AgentMode.DryRun);
        sut.External.Effective.Should().Be(AgentMode.DryRun, "the configmap is more restrictive than the env var");
    }

    /// <summary>
    /// ConfigMap values routinely arrive with a trailing newline; a mode arm that choked on
    /// one would pin a correctly-configured agent to Observe and look like a bug in the
    /// operator's editor.
    /// </summary>
    [Fact]
    public void Tolerates_the_trailing_newline_a_configmap_adds()
    {
        File.WriteAllText(Path.Combine(directory, "mode"), "Auto\n");

        Build(directory, envMode: "auto").External.Effective.Should().Be(AgentMode.Auto);
    }

    [Fact]
    public void An_engaged_emergency_stop_file_holds_the_agent_at_observe()
    {
        File.WriteAllText(Path.Combine(directory, "mode"), "Auto");
        File.WriteAllText(Path.Combine(directory, "killSwitch"), "true");

        var sut = Build(directory, envMode: "auto");

        sut.External.Effective.Should().Be(AgentMode.Observe);
        sut.External.DecidedBy.Should().Be(KillSwitch.ConfigMapStopArm);
    }

    [Fact]
    public void A_disengaged_emergency_stop_file_does_not_constrain()
    {
        File.WriteAllText(Path.Combine(directory, "mode"), "Auto");
        File.WriteAllText(Path.Combine(directory, "killSwitch"), "false");

        Build(directory, envMode: "auto").External.Effective.Should().Be(AgentMode.Auto);
    }

    /// <summary>
    /// The whole point of the ConfigMap arm: a change takes effect without a restart. If
    /// the value were cached at startup this would still return the old mode.
    /// </summary>
    [Fact]
    public void Picks_up_a_change_without_a_restart()
    {
        var modeFile = Path.Combine(directory, "mode");
        File.WriteAllText(modeFile, "Auto");

        var sut = Build(directory, envMode: "auto");
        sut.External.Effective.Should().Be(AgentMode.Auto);

        File.WriteAllText(modeFile, "Observe");

        sut.External.Effective.Should().Be(AgentMode.Observe, "the arm is re-read on every call");
    }

    [Fact]
    public void The_env_var_still_binds_when_the_configmap_is_more_permissive()
    {
        File.WriteAllText(Path.Combine(directory, "mode"), "Auto");

        var sut = Build(directory, envMode: "observe");

        sut.External.Effective.Should().Be(AgentMode.Observe);
        sut.External.DecidedBy.Should().Be(KillSwitch.EnvironmentArm);
    }

    private sealed class OptionsMonitorStub(KillSwitchOptions value) : IOptionsMonitor<KillSwitchOptions>
    {
        public KillSwitchOptions CurrentValue => value;

        public KillSwitchOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<KillSwitchOptions, string?> listener) => null;
    }
}
