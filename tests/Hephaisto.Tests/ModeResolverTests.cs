using Hephaisto.Core.Domain;
using Hephaisto.Core.Safety;

namespace Hephaisto.Tests;

/// <summary>
/// The precedence table for the kill switch.
/// </summary>
/// <remarks>
/// These exist because the failure they guard against is silent and one-directional. An
/// operator who sets <c>HEPHAISTO_MODE=observe</c> to STOP an agent running in Auto gets no
/// error if the arm is ignored - the agent simply keeps acting, and the big red button is
/// painted on. Nothing in the running system would report that; only a test can.
/// </remarks>
public class ModeResolverTests
{
    private const string Env = "env";
    private const string Cfg = "configmap";
    private const string Db = "db";

    // ---------------------------------------------------------------------------------
    // The ordering the whole design rests on
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// "Most restrictive wins" is implemented as Min over the enum, so the enum's numeric
    /// order IS the safety property. Reordering the members would silently inverse every
    /// kill switch in the system while leaving all the other tests passing, so it is pinned
    /// here rather than left as an implicit assumption.
    /// </summary>
    [Fact]
    public void AgentMode_is_ordered_from_most_to_least_restrictive()
    {
        ((int)AgentMode.Off).Should().BeLessThan((int)AgentMode.Observe);
        ((int)AgentMode.Observe).Should().BeLessThan((int)AgentMode.DryRun);
        ((int)AgentMode.DryRun).Should().BeLessThan((int)AgentMode.Auto);
    }

    // ---------------------------------------------------------------------------------
    // The headline case
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The exact scenario the kill switch exists for, and the one that was broken: the
    /// database says Auto, a human sets the env var to observe to stop it. The human wins.
    /// </summary>
    [Fact]
    public void Env_observe_stops_a_database_that_says_auto()
    {
        var resolved = ModeResolver.Resolve(
            ModeResolver.Parse(Env, "observe"),
            ModeArm.Declaring(Db, AgentMode.Auto));

        resolved.Effective.Should().Be(AgentMode.Observe);
        resolved.DecidedBy.Should().Be(Env);
        resolved.IsConstrained.Should().BeTrue();
    }

    /// <summary>The same, through the ConfigMap - the arm that works without a restart.</summary>
    [Fact]
    public void ConfigMap_observe_stops_a_database_that_says_auto()
    {
        var resolved = ModeResolver.Resolve(
            ModeResolver.Parse(Env, "auto"),
            ModeResolver.Parse(Cfg, "Observe"),
            ModeArm.Declaring(Db, AgentMode.Auto));

        resolved.Effective.Should().Be(AgentMode.Observe);
        resolved.DecidedBy.Should().Be(Cfg);
    }

    // ---------------------------------------------------------------------------------
    // The full precedence table
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(AgentMode.Auto, AgentMode.Auto, AgentMode.Auto, AgentMode.Auto)]
    [InlineData(AgentMode.Auto, AgentMode.Auto, AgentMode.Observe, AgentMode.Observe)]
    [InlineData(AgentMode.Auto, AgentMode.Observe, AgentMode.Auto, AgentMode.Observe)]
    [InlineData(AgentMode.Observe, AgentMode.Auto, AgentMode.Auto, AgentMode.Observe)]
    [InlineData(AgentMode.Auto, AgentMode.DryRun, AgentMode.Auto, AgentMode.DryRun)]
    [InlineData(AgentMode.DryRun, AgentMode.Auto, AgentMode.Observe, AgentMode.Observe)]
    [InlineData(AgentMode.Off, AgentMode.Auto, AgentMode.Auto, AgentMode.Off)]
    [InlineData(AgentMode.Auto, AgentMode.Off, AgentMode.DryRun, AgentMode.Off)]
    [InlineData(AgentMode.Observe, AgentMode.Observe, AgentMode.Observe, AgentMode.Observe)]
    public void The_most_restrictive_arm_wins(AgentMode env, AgentMode cfg, AgentMode db, AgentMode expected) =>
        ModeResolver.Resolve(
                ModeArm.Declaring(Env, env),
                ModeArm.Declaring(Cfg, cfg),
                ModeArm.Declaring(Db, db))
            .Effective.Should().Be(expected);

    /// <summary>
    /// The invariant behind the table, stated directly: no arm can ever RAISE the result.
    /// Exhaustive over all 64 combinations, so it holds for cases the table does not list.
    /// </summary>
    [Fact]
    public void No_arm_can_raise_the_mode_above_the_lowest_request()
    {
        AgentMode[] all = [AgentMode.Off, AgentMode.Observe, AgentMode.DryRun, AgentMode.Auto];

        foreach (var env in all)
        {
            foreach (var cfg in all)
            {
                foreach (var db in all)
                {
                    var resolved = ModeResolver.Resolve(
                        ModeArm.Declaring(Env, env),
                        ModeArm.Declaring(Cfg, cfg),
                        ModeArm.Declaring(Db, db));

                    resolved.Effective.Should().Be(
                        (AgentMode)Math.Min((int)env, Math.Min((int)cfg, (int)db)),
                        $"env={env} configmap={cfg} db={db} must resolve to the minimum");
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------
    // Silence versus failure - the distinction that inverts the safety property
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A silent arm is one that is not configured in this environment - no env var, no
    /// mounted ConfigMap. It must not constrain, or the agent could never leave Observe
    /// outside Kubernetes and every developer would learn to ignore the mode.
    /// </summary>
    [Fact]
    public void A_silent_arm_expresses_no_opinion()
    {
        var resolved = ModeResolver.Resolve(
            ModeArm.Silent(Env),
            ModeArm.Silent(Cfg),
            ModeArm.Declaring(Db, AgentMode.Auto));

        resolved.Effective.Should().Be(AgentMode.Auto);
        resolved.DecidedBy.Should().Be(Db);
        resolved.IsConstrained.Should().BeFalse();
    }

    /// <summary>
    /// A malformed arm is configured and unreadable, which is the opposite of silent. This
    /// is the single most important asymmetry in the file: if a typo read as silence, then
    /// <c>HEPHAISTO_MODE=obserev</c> would not restrict the agent - it would REMOVE the
    /// restriction the operator was trying to apply.
    /// </summary>
    [Fact]
    public void A_malformed_arm_pins_the_agent_to_observe()
    {
        var resolved = ModeResolver.Resolve(
            ModeResolver.Parse(Env, "obserev"),
            ModeArm.Declaring(Db, AgentMode.Auto));

        resolved.Effective.Should().Be(AgentMode.Observe);
        resolved.DecidedBy.Should().Be(Env);
    }

    [Fact]
    public void An_unreadable_arm_pins_the_agent_to_observe()
    {
        var resolved = ModeResolver.Resolve(
            ModeArm.Unreadable(Cfg, "/etc/hephaisto/mode does not exist"),
            ModeArm.Declaring(Db, AgentMode.Auto));

        resolved.Effective.Should().Be(AgentMode.Observe);
        resolved.DecidedBy.Should().Be(Cfg);
    }

    /// <summary>
    /// Postgres being unreachable must not read as permission. "No audit, no action" - the
    /// agent cannot record what it did, so it does not get to do it.
    /// </summary>
    [Fact]
    public void An_unreachable_database_pins_the_agent_to_observe()
    {
        var resolved = ModeResolver.Resolve(
            ModeResolver.Parse(Env, "auto"),
            ModeArm.Unreadable(Db, "connection refused"));

        resolved.Effective.Should().Be(AgentMode.Observe);
    }

    /// <summary>
    /// Not Auto, because "nobody configured it" would then be the most dangerous state in
    /// the system. Not Off either, because an agent that reports nothing is indistinguishable
    /// from a healthy cluster, and the whole point is to be told.
    /// </summary>
    [Fact]
    public void With_every_arm_silent_the_agent_observes()
    {
        var resolved = ModeResolver.Resolve(
            ModeArm.Silent(Env),
            ModeArm.Silent(Cfg),
            ModeArm.Silent(Db));

        resolved.Effective.Should().Be(AgentMode.Observe);
        resolved.DecidedBy.Should().Be("default");
    }

    [Fact]
    public void With_no_arms_at_all_the_agent_observes() =>
        ModeResolver.Resolve().Effective.Should().Be(AgentMode.Observe);

    // ---------------------------------------------------------------------------------
    // Parsing
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("off", AgentMode.Off)]
    [InlineData("Observe", AgentMode.Observe)]
    [InlineData("OBSERVE", AgentMode.Observe)]
    [InlineData("  observe  ", AgentMode.Observe)]
    [InlineData("dryrun", AgentMode.DryRun)]
    [InlineData("dry-run", AgentMode.DryRun)]
    [InlineData("dry_run", AgentMode.DryRun)]
    [InlineData("DryRun", AgentMode.DryRun)]
    [InlineData("auto", AgentMode.Auto)]
    public void Parses_the_spellings_an_operator_actually_types(string raw, AgentMode expected)
    {
        var arm = ModeResolver.Parse(Env, raw);

        arm.Status.Should().Be(ModeArmStatus.Declared);
        arm.Declared.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Treats_an_unset_value_as_silence(string? raw) =>
        ModeResolver.Parse(Env, raw).Status.Should().Be(ModeArmStatus.Silent);

    /// <summary>
    /// <c>Enum.TryParse</c> would accept "3" and quietly mean Auto. An operator typing a
    /// number into a kill switch has misunderstood it, and a misunderstanding reads as
    /// Observe.
    /// </summary>
    [Theory]
    [InlineData("3")]
    [InlineData("0")]
    [InlineData("atuo")]
    [InlineData("on")]
    [InlineData("enabled")]
    [InlineData("true")]
    public void Rejects_values_that_are_not_mode_names(string raw)
    {
        var arm = ModeResolver.Parse(Env, raw);

        arm.Status.Should().Be(ModeArmStatus.Malformed);
        arm.Ceiling.Should().Be(AgentMode.Observe);
    }

    // ---------------------------------------------------------------------------------
    // The emergency stop
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Deliberately the opposite of how a normal boolean flag parses. Someone hitting this
    /// key in an emergency may type anything; all of it must stop the agent. Only an
    /// unambiguous false leaves it running.
    /// </summary>
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("STOP")]
    [InlineData("engaged")]
    [InlineData("please stop")]
    public void Any_unclear_emergency_stop_value_engages_it(string raw)
    {
        var arm = ModeResolver.ParseEmergencyStop(Cfg, raw);

        arm.Ceiling.Should().Be(AgentMode.Observe);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("0")]
    [InlineData("disengaged")]
    public void An_unambiguous_false_leaves_the_emergency_stop_disengaged(string raw)
    {
        var arm = ModeResolver.ParseEmergencyStop(Cfg, raw);

        arm.Status.Should().Be(ModeArmStatus.Silent);
        arm.Ceiling.Should().BeNull();
    }

    [Fact]
    public void An_engaged_emergency_stop_overrides_auto_everywhere_else()
    {
        var resolved = ModeResolver.Resolve(
            ModeResolver.Parse(Env, "auto"),
            ModeResolver.ParseEmergencyStop(Cfg, "true"),
            ModeArm.Declaring(Db, AgentMode.Auto));

        resolved.Effective.Should().Be(AgentMode.Observe);
        resolved.DecidedBy.Should().Be(Cfg);
    }

    /// <summary>An absent stop key is silence, not an engaged stop - it is an optional key.</summary>
    [Fact]
    public void An_absent_emergency_stop_does_not_constrain() =>
        ModeResolver.ParseEmergencyStop(Cfg, null).Ceiling.Should().BeNull();

    // ---------------------------------------------------------------------------------
    // Explaining itself
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The binding arm has to be named correctly, because it is the one a human has to go
    /// and change. Naming the wrong one sends them to edit something that will not help.
    /// </summary>
    [Fact]
    public void Names_the_arm_that_actually_bound_the_result()
    {
        var resolved = ModeResolver.Resolve(
            ModeArm.Declaring(Env, AgentMode.DryRun),
            ModeArm.Declaring(Cfg, AgentMode.Off),
            ModeArm.Declaring(Db, AgentMode.Auto));

        resolved.Effective.Should().Be(AgentMode.Off);
        resolved.DecidedBy.Should().Be(Cfg);
    }

    /// <summary>When two arms tie at the binding value the explanation must be stable.</summary>
    [Fact]
    public void Names_the_first_arm_when_two_tie()
    {
        var resolved = ModeResolver.Resolve(
            ModeArm.Declaring(Env, AgentMode.Observe),
            ModeArm.Declaring(Cfg, AgentMode.Observe));

        resolved.DecidedBy.Should().Be(Env);
    }

    [Fact]
    public void Explains_every_arm_for_a_human()
    {
        var explanation = ModeResolver.Resolve(
                ModeResolver.Parse(Env, "auto"),
                ModeArm.Unreadable(Cfg, "no such file"),
                ModeArm.Declaring(Db, AgentMode.Auto))
            .Explain();

        explanation.Should().Contain("Observe");
        explanation.Should().Contain(Cfg);
        explanation.Should().Contain("no such file");
    }

    [Fact]
    public void Is_not_constrained_when_every_arm_agrees() =>
        ModeResolver.Resolve(
                ModeArm.Declaring(Env, AgentMode.DryRun),
                ModeArm.Declaring(Db, AgentMode.DryRun))
            .IsConstrained.Should().BeFalse();
}
