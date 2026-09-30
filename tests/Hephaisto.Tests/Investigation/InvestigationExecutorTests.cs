using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Investigations.Jobs;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Investigations;
using Hephaisto.Core.Safety;

namespace Hephaisto.Tests.Investigations;

/// <summary>
/// The executor precedence table (v0.12.0 F5). Silence is in-process, the most restrictive arm
/// wins, not enabled is in-process whatever the arms say, and the stop and the latch beat every arm.
/// </summary>
public sealed class InvestigationExecutorTests
{
    private const string Stop = "configmap:killSwitch";
    private const string Latch = "db:agent_mode";

    private static ModeResolution Agent(params ModeArm[] arms) => ModeResolver.Resolve(arms);

    private static ModeResolution Observe => Agent(ModeArm.Declaring("env", AgentMode.Observe));

    private static InvestigationExecutorResolution Resolve(
        ModeResolution agent, bool enabled, params InvestigationExecutorArm[] arms) =>
        InvestigationExecutorResolver.Resolve(arms, enabled, agent, Stop, Latch);

    private static InvestigationExecutorArm P(string name, string? raw) => InvestigationExecutorResolver.Parse(name, raw);

    [Fact]
    public void The_order_is_load_bearing()
    {
        ((int)InvestigationExecutor.InProcess).Should().BeLessThan((int)InvestigationExecutor.Job,
            "the resolver takes the minimum, so in-process must be the smaller");
    }

    [Fact]
    public void Silence_is_in_process()
    {
        var r = Resolve(Observe, enabled: true, P("env", null), P("cm", " "));

        r.Effective.Should().Be(InvestigationExecutor.InProcess);
        r.DecidedBy.Should().Be("default");
    }

    [Fact]
    public void Both_arms_saying_job_is_job()
    {
        Resolve(Observe, enabled: true, P("env", "job"), P("cm", "Job")).Effective.Should().Be(InvestigationExecutor.Job);
    }

    [Fact]
    public void One_silent_arm_leaves_the_other_to_decide()
    {
        Resolve(Observe, enabled: true, P("env", "job"), P("cm", null)).Effective.Should().Be(InvestigationExecutor.Job);
    }

    [Fact]
    public void The_most_restrictive_arm_wins()
    {
        var r = Resolve(Observe, enabled: true, P("env", "job"), P("cm", "inprocess"));

        r.Effective.Should().Be(InvestigationExecutor.InProcess);
        r.DecidedBy.Should().Be("cm");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("jobs")]
    [InlineData("always")]
    public void A_malformed_arm_reads_as_in_process(string raw)
    {
        var r = Resolve(Observe, enabled: true, P("env", "job"), P("cm", raw));

        r.Effective.Should().Be(InvestigationExecutor.InProcess);
        r.Arms[1].Status.Should().Be(ModeArmStatus.Malformed);
    }

    [Fact]
    public void An_unreadable_arm_reads_as_in_process()
    {
        Resolve(Observe, enabled: true, P("env", "job"), InvestigationExecutorResolver.Unreadable("cm", "io"))
            .Effective.Should().Be(InvestigationExecutor.InProcess);
    }

    [Fact]
    public void Not_enabled_is_in_process_whatever_the_arms_say()
    {
        // A ConfigMap edited to job on an install without the investigator's port, policy and
        // credentials would only launch Jobs that cannot reach anything.
        var r = Resolve(Observe, enabled: false, P("env", "job"), P("cm", "job"));

        r.Effective.Should().Be(InvestigationExecutor.InProcess);
        r.Declared.Should().Be(InvestigationExecutor.Job);
        r.Explain().Should().Contain("investigation.job.enabled is false");
    }

    [Fact]
    public void The_emergency_stop_forces_in_process_although_the_agent_axis_says_observe()
    {
        var agent = Agent(ModeArm.Declaring("env", AgentMode.Auto), ModeResolver.ParseEmergencyStop(Stop, "yes"));

        var r = Resolve(agent, enabled: true, P("env", "job"));

        r.Effective.Should().Be(InvestigationExecutor.InProcess);
        r.EmergencyStop.Should().BeTrue();
        r.DecidedBy.Should().Be(Stop);
    }

    [Fact]
    public void The_runaway_latch_forces_in_process()
    {
        var agent = Agent(ModeArm.Declaring("env", AgentMode.Auto), ModeArm.Declaring(Latch, AgentMode.Observe, "runaway latch"));

        var r = Resolve(agent, enabled: true, P("env", "job"));

        r.Effective.Should().Be(InvestigationExecutor.InProcess);
        r.RunawayLatched.Should().BeTrue();
    }

    [Fact]
    public void A_disengaged_stop_does_not_constrain()
    {
        var agent = Agent(ModeArm.Declaring("env", AgentMode.Auto), ModeResolver.ParseEmergencyStop(Stop, "false"));

        Resolve(agent, enabled: true, P("env", "job")).Effective.Should().Be(InvestigationExecutor.Job);
    }

    // ---- the per-investigation policy -----------------------------------------------------

    private static readonly ExecutorCaps Caps = new() { MaxConcurrentJobs = 1, MaxJobsPerHour = 20 };

    [Theory]
    [InlineData(InvestigationExecutor.InProcess, 0, 0, false, ExecutorChoice.InProcessByMode)]
    [InlineData(InvestigationExecutor.Job, 0, 0, false, ExecutorChoice.Job)]
    [InlineData(InvestigationExecutor.Job, 0, 19, false, ExecutorChoice.Job)]
    [InlineData(InvestigationExecutor.Job, 1, 0, false, ExecutorChoice.Overflow)]
    [InlineData(InvestigationExecutor.Job, 0, 20, false, ExecutorChoice.HourlyCapReached)]
    [InlineData(InvestigationExecutor.Job, 1, 20, false, ExecutorChoice.Overflow)]
    [InlineData(InvestigationExecutor.Job, 0, 0, true, ExecutorChoice.ForeignCluster)]
    [InlineData(InvestigationExecutor.InProcess, 5, 50, true, ExecutorChoice.InProcessByMode)]
    public void The_policy_table(
        InvestigationExecutor effective, int running, int lastHour, bool foreign, ExecutorChoice expected)
    {
        InvestigationExecutorPolicy.Decide(effective, Caps, running, lastHour, foreign).Should().Be(expected);
    }

    [Fact]
    public void A_cap_of_zero_sends_everything_in_process()
    {
        InvestigationExecutorPolicy.Decide(
                InvestigationExecutor.Job, new ExecutorCaps { MaxConcurrentJobs = 0, MaxJobsPerHour = 20 }, 0, 0, false)
            .Should().Be(ExecutorChoice.Overflow);
    }

    // ---- startup validation ---------------------------------------------------------------

    private static InvestigationJobOptions Bind(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHephaistoInvestigationJob(configuration);

        return services.BuildServiceProvider().GetRequiredService<IOptions<InvestigationJobOptions>>().Value;
    }

    [Fact]
    public void Defaults_are_inert()
    {
        var o = Bind();

        o.Enabled.Should().BeFalse();
        o.Executor.Should().BeNull();
        o.FallbackToInProcess.Should().BeTrue();
        o.Source.Enabled.Should().BeFalse();
    }

    [Fact]
    public void A_typo_in_the_env_arm_refuses_to_start()
    {
        var act = () => Bind(("Investigation:Job:Executor", "jobs"));

        act.Should().Throw<OptionsValidationException>().WithMessage("*inprocess or job*");
    }

    [Fact]
    public void Enabled_without_an_endpoint_refuses_to_start()
    {
        var act = () => Bind(("Investigation:Job:Enabled", "true"));

        act.Should().Throw<OptionsValidationException>().WithMessage("*EndpointUrl*");
    }

    [Theory]
    [InlineData("8080")]
    [InlineData("8083")]
    public void Enabled_on_another_surface_s_port_refuses_to_start(string port)
    {
        var act = () => Bind(
            ("Investigation:Job:Enabled", "true"),
            ("Investigation:Job:EndpointUrl", "http://hephaisto.hephaisto.svc:8084/investigate"),
            ("Investigation:Job:Port", port));

        act.Should().Throw<OptionsValidationException>().WithMessage("*its own port*");
    }

    [Fact]
    public void A_complete_configuration_starts()
    {
        var o = Bind(
            ("Investigation:Job:Enabled", "true"),
            ("Investigation:Job:Executor", "job"),
            ("Investigation:Job:EndpointUrl", "http://hephaisto.hephaisto.svc:8084/investigate"));

        o.Enabled.Should().BeTrue();
        o.Port.Should().Be(8084);
    }
}
